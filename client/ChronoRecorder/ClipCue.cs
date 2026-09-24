using System;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ChronoRecorder
{
    /// <summary>
    /// The little chime that plays when a hotkey saves a clip, so you know it worked without looking away. Two soft
    /// rising notes (E5 then B5, a pleasant open fifth), about a third of a second, made in code so there is no sound
    /// file to ship. It plays through the speakers you chose in Settings (Windows' default otherwise).
    /// It would otherwise end up in the clip: whatever plays through the speakers is also what loopback capture
    /// records. <see cref="Recorder.MuteForClipCue"/> silences the recording for <see cref="Duration"/> (plus a
    /// margin for playback latency) right as this plays, so it is heard live but never in a saved clip.
    /// </summary>
    public static class ClipCue
    {
        public const int SampleRate = 44100;

        /// <summary>Loud enough to hear over a game's sound on a normal volume, quiet enough not to startle.</summary>
        public const double Volume = 0.42;

        private static readonly (double Hz, double Start, double Length)[] Notes =
        {
            (659.25, 0.00, 0.16),    // E5
            (987.77, 0.10, 0.24),    // B5, overlapping the first note's tail
        };

        /// <summary>How long the chime itself runs for, start of the first note to the end of the last.</summary>
        public static readonly TimeSpan Duration = TimeSpan.FromSeconds(Notes.Max(n => n.Start + n.Length));

        /// <summary>
        /// Minion mode's sound, shipped next to the program (like FFmpeg) so a portable copy finds it too. A WAV, not
        /// the MP3 it started as: NAudio.Core 2.2.1 (netstandard2.0, what this project has) has no MP3 decoder, only
        /// <see cref="WaveFileReader"/>. Converted once with the bundled FFmpeg (mono, 44100 Hz, 16-bit).
        /// </summary>
        private static readonly string MinionPath = Path.Combine(AppContext.BaseDirectory, "Assets", "minion.wav");

        // Read once: the file never changes while Chrono is running.
        private static readonly Lazy<TimeSpan> MinionDuration = new(() =>
        {
            try { using var reader = new WaveFileReader(MinionPath); return reader.TotalTime; }
            catch { return TimeSpan.FromSeconds(1); }   // couldn't read it: still mutes a sane window if Play falls back to the chime
        });

        /// <summary>
        /// How long whatever would play now runs for: the chime, or Minion mode's sound. What
        /// <see cref="Recorder.MuteForClipCue"/> sizes its mute window from.
        /// </summary>
        public static TimeSpan DurationFor(bool minionMode) => minionMode && File.Exists(MinionPath) ? MinionDuration.Value : Duration;

        /// <summary>The chime as mono 16-bit samples.</summary>
        public static short[] Samples()
        {
            double total = 0;
            foreach (var n in Notes) total = Math.Max(total, n.Start + n.Length);
            var mix = new double[(int)(total * SampleRate) + 1];

            foreach (var (hz, start, length) in Notes)
            {
                int first = (int)(start * SampleRate);
                int count = (int)(length * SampleRate);
                for (int i = 0; i < count && first + i < mix.Length; i++)
                {
                    double t = (double)i / SampleRate;
                    double attack = Math.Min(1.0, i / (0.006 * SampleRate));              // 6 ms fade-in: no click
                    double release = Math.Pow(1.0 - (double)i / count, 2.2);               // smooth decay to silence
                    double tone = Math.Sin(2 * Math.PI * hz * t) + 0.18 * Math.Sin(2 * Math.PI * hz * 2 * t);   // a little brightness
                    mix[first + i] += tone * attack * release;
                }
            }

            var samples = new short[mix.Length];
            for (int i = 0; i < mix.Length; i++)
                samples[i] = (short)Math.Round(Math.Clamp(mix[i] * Volume / 1.6, -1.0, 1.0) * short.MaxValue);
            return samples;
        }

        /// <summary>The chime as a WAV file's bytes (for tests, and anything that wants a file).</summary>
        public static byte[] Wav()
        {
            var samples = Samples();
            using var stream = new MemoryStream();
            using (var writer = new WaveFileWriter(stream, new WaveFormat(SampleRate, 16, 1)))
                writer.WriteSamples(samples, 0, samples.Length);
            return stream.ToArray();
        }

        /// <summary>
        /// Play it now without waiting: the chime, or, in Minion mode, the minion sound (falling back to the chime if
        /// that file isn't there). Never throws: a chime that fails is not worth a problem.
        /// </summary>
        public static void Play(string? speakerDeviceId, bool minionMode = false)
        {
            try
            {
                var device = AudioDevices.Open(DataFlow.Render, speakerDeviceId, out _);
                if (device == null) return;

                IWaveProvider source;
                if (minionMode && File.Exists(MinionPath))
                {
                    source = new WaveFileReader(MinionPath);
                }
                else
                {
                    var bytes = new byte[Samples().Length * 2];
                    Buffer.BlockCopy(Samples(), 0, bytes, 0, bytes.Length);
                    source = new RawSourceWaveStream(new MemoryStream(bytes), new WaveFormat(SampleRate, 16, 1));
                }

                var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 60);
                output.PlaybackStopped += (s, e) => { output.Dispose(); (source as IDisposable)?.Dispose(); };
                output.Init(source);
                output.Play();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Couldn't play the clip sound: {ex.Message}");
            }
        }
    }
}
