using Xunit;

namespace ChronoRecorder.Tests
{
    public class AudioInputTests
    {
        [Fact]
        public void Float32_IsPassedAsF32le()
        {
            var input = AudioInput.Create(@"\\.\pipe\p", 48000, 2, 32, isFloat: true)!;

            Assert.Equal(AudioSampleFormat.Float32, input.Format);
            Assert.Equal("f32le", input.FfmpegFormat);
            Assert.Equal(8, input.BytesPerFrame);
        }

        [Fact]
        public void Pcm16_IsPassedAsS16le()
        {
            var input = AudioInput.Create(@"\\.\pipe\p", 44100, 1, 16, isFloat: false)!;

            Assert.Equal(AudioSampleFormat.Pcm16, input.Format);
            Assert.Equal("s16le", input.FfmpegFormat);
            Assert.Equal(2, input.BytesPerFrame);
        }

        [Fact]
        public void SurroundDevices_KeepTheirChannelCount()
        {
            var input = AudioInput.Create(@"\\.\pipe\p", 48000, 8, 32, isFloat: true)!;

            Assert.Equal(8, input.Channels);
            Assert.Equal(32, input.BytesPerFrame);
        }

        [Theory]
        [InlineData(24, false)]
        [InlineData(32, false)]
        [InlineData(8, false)]
        [InlineData(64, true)]
        [InlineData(16, true)]
        public void FormatsWeCantHandle_AreRefusedSoTheRecordingCanGoAheadWithoutSound(int bits, bool isFloat)
        {
            Assert.Null(AudioInput.Create(@"\\.\pipe\p", 48000, 2, bits, isFloat));
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(48000, 0)]
        [InlineData(-1, 2)]
        public void NonsenseRatesOrChannels_AreRefused(int rate, int channels)
        {
            Assert.Null(AudioInput.Create(@"\\.\pipe\p", rate, channels, 32, isFloat: true));
        }
    }
}
