using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ChronoRecorder
{
    /// <summary>
    /// Listens to a microphone and reports how loud it is, so the settings page can show a live level bar while you
    /// pick the microphone and its volume. It only listens (nothing is recorded or kept) and only while Settings is
    /// showing that bar. Windows lets several programs open the same microphone, so it doesn't disturb a recording.
    /// </summary>
    public sealed class MicMeter : IDisposable
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(70);   // about 14 updates a second is smooth enough

        private readonly WasapiCapture capture;
        private readonly Action<double> onLevel;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private TimeSpan lastReport = TimeSpan.Zero;
        private double peakSinceReport;
        private bool disposed;

        /// <summary>Null if the microphone can't be opened (unplugged, or in use exclusively by another program).</summary>
        public static MicMeter? TryStart(string? deviceId, Action<double> onLevel, out string? problem)
        {
            problem = null;
            try
            {
                var device = AudioDevices.Open(DataFlow.Capture, deviceId, out _);
                if (device == null) { problem = "There is no microphone on this PC."; return null; }

                var meter = new MicMeter(new WasapiCapture(device), onLevel);
                meter.capture.StartRecording();
                return meter;
            }
            catch (Exception ex)
            {
                problem = $"Couldn't listen to that microphone ({ex.Message}).";
                return null;
            }
        }

        private MicMeter(WasapiCapture capture, Action<double> onLevel)
        {
            this.capture = capture;
            this.onLevel = onLevel;
            capture.DataAvailable += OnData;
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (disposed) return;
            peakSinceReport = Math.Max(peakSinceReport, Peak(e.Buffer, e.BytesRecorded, capture.WaveFormat));

            if (clock.Elapsed - lastReport < Interval) return;
            lastReport = clock.Elapsed;
            double peak = peakSinceReport;
            peakSinceReport = 0;
            try { onLevel(peak); } catch { /* the window may be closing */ }
        }

        /// <summary>The loudest sample in a buffer, 0 to 1, for 32-bit float or 16-bit sound.</summary>
        public static double Peak(byte[] buffer, int count, WaveFormat format)
        {
            double peak = 0;
            // Extensible headers wrap the real format; unwrap so float and PCM can be told apart.
            if (format is WaveFormatExtensible ext) format = ext.ToStandardWaveFormat();
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat;

            if (isFloat && format.BitsPerSample == 32)
            {
                for (int i = 0; i + 4 <= count; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer, i)));
            }
            else if (format.BitsPerSample == 16)
            {
                for (int i = 0; i + 2 <= count; i += 2) peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(buffer, i)) / 32768.0);
            }
            return Math.Min(1.0, peak);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { capture.DataAvailable -= OnData; capture.StopRecording(); } catch { }
            try { capture.Dispose(); } catch { }
        }
    }
}
