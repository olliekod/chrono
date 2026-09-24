using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ChronoRecorder
{
    public enum AudioSource
    {
        /// <summary>Everything you hear: the game, voice chat, music. (WASAPI loopback of the default output.)</summary>
        SystemSound,

        /// <summary>The default microphone.</summary>
        Microphone
    }

    /// <summary>
    /// Captures one audio source and serves it to FFmpeg through a named pipe.
    /// Create it before starting FFmpeg (the pipe must exist for FFmpeg to open it), pass <see cref="Input"/> to the
    /// command line, then call <see cref="Start"/>.
    /// </summary>
    public sealed class AudioCapture : IDisposable
    {
        private readonly IWaveIn capture;
        private readonly NamedPipeServerStream pipe;
        private readonly AudioFeeder feeder;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private Thread? feedThread;
        private volatile bool disposed;

        public AudioSource Source { get; }

        /// <summary>How FFmpeg should read this stream.</summary>
        public AudioInput Input { get; }

        /// <summary>True once FFmpeg is reading the audio and the audio clock has started.</summary>
        public bool Consuming => feeder.Consuming;

        /// <summary>Frames written to FFmpeg so far, for diagnostics.</summary>
        public long FramesWritten => feeder.FramesWritten;
        public int SampleRate => Input.SampleRate;
        public double PendingMilliseconds => feeder.PendingMilliseconds;

        /// <summary>Ignore the device, so only <see cref="PushForDiagnostics"/> sound is heard. For tests.</summary>
        public volatile bool MuteDeviceForDiagnostics;

        /// <summary>Feed sound in directly, as if the device had captured it. For tests and diagnostics.</summary>
        public void PushForDiagnostics(byte[] buffer) => feeder.Push(buffer, buffer.Length);

        /// <summary>Silences this capture's contribution to the recording for a short window starting now, without
        /// touching the live device (so it still plays and is heard normally; only the recording is affected). See
        /// <see cref="ClipCue"/> and <see cref="Recorder.MuteForClipCue"/>.</summary>
        public void MuteBriefly(TimeSpan duration) => feeder.MuteFromNow(duration);

        /// <summary>True when the device asked for wasn't there and Windows' default was used instead.</summary>
        public bool FellBackToDefault { get; private set; }

        /// <summary>The device went away or stopped. The recording can carry on without this source.</summary>
        public event Action<string>? Failed;

        /// <summary>
        /// Opens the device. Returns null, with a reason fit to show the user, if there is no usable device
        /// or its format isn't one we can pass on.
        /// </summary>
        /// <param name="deviceId">The device to record, as saved in the settings; empty means Windows' default.</param>
        /// <param name="gain">1 = as it is; 2.5 = 250% (applied when the sounds are mixed).</param>
        public static AudioCapture? TryCreate(AudioSource source, double clockStartUnixSeconds, TimeSpan delay, string? deviceId, double gain, out string? problem)
        {
            problem = null;
            IWaveIn? capture = null;
            NamedPipeServerStream? pipe = null;
            bool fellBack = false;

            try
            {
                var device = AudioDevices.Open(source == AudioSource.SystemSound ? DataFlow.Render : DataFlow.Capture, deviceId, out fellBack);
                if (device == null)
                {
                    problem = $"there is no {Describe(source)} on this PC";
                    return null;
                }

                capture = source == AudioSource.SystemSound ? new WasapiLoopbackCapture(device) : new WasapiCapture(device);

                // Extensible headers wrap the real format; unwrap so we can see whether it is float or PCM.
                WaveFormat format = capture.WaveFormat is WaveFormatExtensible ext ? ext.ToStandardWaveFormat() : capture.WaveFormat;

                string pipeName = $"chrono_audio_{Guid.NewGuid():N}";
                var input = AudioInput.Create(
                    $@"\\.\pipe\{pipeName}", format.SampleRate, format.Channels, format.BitsPerSample,
                    isFloat: format.Encoding == WaveFormatEncoding.IeeeFloat);

                if (input == null)
                {
                    problem = $"the {Describe(source)} uses an audio format Chrono can't record ({format})";
                    capture.Dispose();
                    return null;
                }

                // A tiny buffer on purpose: it makes writes block while FFmpeg is still starting up, which is
                // how the feeder knows when to start the audio clock (see AudioFeeder).
                pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 4096);

                return new AudioCapture(source, capture, pipe, input with { Gain = gain }, clockStartUnixSeconds, delay) { FellBackToDefault = fellBack };
            }
            catch (Exception ex)
            {
                capture?.Dispose();
                pipe?.Dispose();
                problem = $"couldn't open the {Describe(source)} ({ex.Message})";
                return null;
            }
        }

        private static string Describe(AudioSource source) => source == AudioSource.SystemSound ? "sound output" : "microphone";

        private AudioCapture(AudioSource source, IWaveIn capture, NamedPipeServerStream pipe, AudioInput input, double clockStartUnixSeconds, TimeSpan delay)
        {
            Source = source;
            Input = input;
            this.capture = capture;
            this.pipe = pipe;

            // The feeder's clock reads "seconds since T0", the same start time the video is stamped against.
            var sinceT0AtCreation = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - clockStartUnixSeconds;
            var stopwatch = Stopwatch.StartNew();
            feeder = new AudioFeeder(pipe, input.SampleRate, input.BytesPerFrame,
                () => TimeSpan.FromSeconds(sinceT0AtCreation + stopwatch.Elapsed.TotalSeconds), Thread.Sleep, delay);

            capture.DataAvailable += (s, e) => { if (!MuteDeviceForDiagnostics) feeder.Push(e.Buffer, e.BytesRecorded); };
            capture.RecordingStopped += (s, e) =>
            {
                // A device unplugged or switched away mid-recording ends up here.
                if (!disposed) Failed?.Invoke(e.Exception?.Message ?? "the audio device stopped");
            };
        }

        /// <summary>Start capturing and feeding. FFmpeg must be launched right after (or before) this.</summary>
        public void Start()
        {
            capture.StartRecording();

            feedThread = new Thread(Feed) { IsBackground = true, Name = $"Chrono audio ({Source})" };
            feedThread.Start();
        }

        private void Feed()
        {
            try
            {
                // FFmpeg opens the pipe when it starts; give up if it never does.
                if (!pipe.WaitForConnectionAsync(cts.Token).Wait(TimeSpan.FromSeconds(30))) return;
                feeder.Run(cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or AggregateException or System.IO.IOException)
            {
                // Disposed while waiting or feeding.
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            cts.Cancel();
            try { capture.StopRecording(); } catch { }
            try { capture.Dispose(); } catch { }
            try { pipe.Dispose(); } catch { }
            feedThread?.Join(2000);
            cts.Dispose();
        }
    }
}
