using NAudio.Wave;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class AudioDevicePickerTests
    {
        private static readonly AudioDeviceInfo Speakers = new("id-speakers", "Speakers (Realtek(R) Audio)", true);
        private static readonly AudioDeviceInfo Headset = new("id-headset", "Headset (Arctis Nova)", false);
        private static readonly AudioDeviceInfo Broadcast = new("id-nv", "Microphone (NVIDIA Broadcast)", false);
        private static readonly AudioDeviceInfo Maono = new("id-maono", "Microphone (Maono 2x2)", true);

        [Fact]
        public void NoChoice_MeansWindowsDefault()
        {
            var (device, fellBack) = AudioDevicePicker.Choose(new[] { Headset, Speakers }, "");

            Assert.Equal(Speakers, device);
            Assert.False(fellBack);
        }

        [Fact]
        public void ASavedChoice_IsUsedWhenItIsStillThere_EvenWhenItIsNotTheDefault()
        {
            // The case that matters: the microphone should be NVIDIA Broadcast, not the Maono that Windows defaults to.
            var (device, fellBack) = AudioDevicePicker.Choose(new[] { Maono, Broadcast }, "id-nv");

            Assert.Equal(Broadcast, device);
            Assert.False(fellBack);
        }

        [Fact]
        public void ADeviceThatIsGone_FallsBackToTheDefault_AndSaysSo()
        {
            var (device, fellBack) = AudioDevicePicker.Choose(new[] { Maono }, "id-nv");

            Assert.Equal(Maono, device);
            Assert.True(fellBack);
        }

        [Fact]
        public void TheIdIsMatchedIgnoringCase()
        {
            Assert.Equal(Broadcast, AudioDevicePicker.Choose(new[] { Maono, Broadcast }, "ID-NV").Device);
        }

        [Fact]
        public void WithNoDeviceAtAll_ThereIsNothingToUse()
        {
            var (device, _) = AudioDevicePicker.Choose(new AudioDeviceInfo[0], "id-nv");

            Assert.Null(device);
        }

        [Fact]
        public void WithNoDefaultMarked_TheFirstDeviceIsUsed()
        {
            var (device, _) = AudioDevicePicker.Choose(new[] { Headset, Broadcast }, null);

            Assert.Equal(Headset, device);
        }
    }

    public class ClipCueTests
    {
        [Fact]
        public void TheChimeIsShort_AndLongEnoughToHear()
        {
            double seconds = (double)ClipCue.Samples().Length / ClipCue.SampleRate;

            Assert.InRange(seconds, 0.25, 0.6);
        }

        [Fact]
        public void TheChimeIsAudible_ButNotLoud()
        {
            double peak = ClipCue.Samples().Max(s => Math.Abs(s / 32768.0));

            Assert.InRange(peak, 0.25, 0.6);
        }

        [Fact]
        public void TheChimeStartsAndEndsAtSilence_SoItDoesNotClick()
        {
            var samples = ClipCue.Samples();

            Assert.True(Math.Abs(samples[0]) < 200, "starts near zero");
            Assert.True(Math.Abs(samples[^1]) < 200, "ends near zero");
            int loudest = samples.Max(s => Math.Abs((int)s));
            Assert.True(samples.Take(ClipCue.SampleRate / 2000).Max(s => Math.Abs((int)s)) < loudest / 4, "fades in over a few milliseconds");
        }

        [Fact]
        public void TheChimeIsAValidWavFile()
        {
            byte[] wav = ClipCue.Wav();

            using var reader = new WaveFileReader(new MemoryStream(wav));
            Assert.Equal(ClipCue.SampleRate, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.Equal(16, reader.WaveFormat.BitsPerSample);
            Assert.Equal(ClipCue.Samples().Length * 2, reader.Length);
        }

        [Fact]
        public void TheChimeIsOnByDefault() => Assert.True(new RecorderConfig().PlaySoundOnClip);
    }

    public class MicMeterTests
    {
        [Fact]
        public void PeakOfFloatSound_IsTheLoudestSample()
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            var samples = new float[] { 0.1f, -0.6f, 0.3f, 0.2f };
            var bytes = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

            Assert.Equal(0.6, MicMeter.Peak(bytes, bytes.Length, format), 3);
        }

        [Fact]
        public void PeakOfSixteenBitSound_IsScaledToZeroToOne()
        {
            var format = new WaveFormat(44100, 16, 1);
            var samples = new short[] { 1000, -16384, 200 };
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

            Assert.Equal(0.5, MicMeter.Peak(bytes, bytes.Length, format), 3);
        }

        [Fact]
        public void SilenceIsZero_AndAnythingPastFullScaleIsCapped()
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
            var silence = new byte[400];
            var loud = BitConverter.GetBytes(3.5f);

            Assert.Equal(0, MicMeter.Peak(silence, silence.Length, format));
            Assert.Equal(1.0, MicMeter.Peak(loud, loud.Length, format));
        }

        [Fact]
        public void OnlyWholeSamplesAreRead()
        {
            var format = new WaveFormat(44100, 16, 1);
            var bytes = new byte[] { 0x00, 0x40, 0xFF };   // one sample and a stray byte

            Assert.Equal(0.5, MicMeter.Peak(bytes, bytes.Length, format), 3);
        }
    }
}
