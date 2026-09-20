using Xunit;

namespace ChronoRecorder.Tests
{
    public class EncoderProfileTests
    {
        [Fact]
        public void Nvenc_UsesNvencPreset()
        {
            string args = EncoderProfile.BuildArgs("h264_nvenc", 8000, 60);

            Assert.Contains("-c:v h264_nvenc", args);
            Assert.Contains("-preset p4", args);
        }

        [Theory]
        [InlineData("h264_amf")]
        [InlineData("h264_qsv")]
        [InlineData("libx264")]
        public void NonNvencEncoders_NeverGetTheNvencOnlyPreset(string encoder)
        {
            string args = EncoderProfile.BuildArgs(encoder, 8000, 60);

            Assert.Contains($"-c:v {encoder}", args);
            Assert.DoesNotContain("-preset p4", args);
        }

        [Fact]
        public void Amf_UsesAmfQualityFlag()
        {
            Assert.Contains("-quality speed", EncoderProfile.BuildArgs("h264_amf", 8000, 60));
        }

        [Fact]
        public void Software_UsesLightestPreset()
        {
            Assert.Contains("-preset ultrafast", EncoderProfile.BuildArgs("libx264", 8000, 60));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not_a_real_encoder")]
        public void UnknownEncoder_FallsBackToSoftware(string? encoder)
        {
            Assert.Equal("libx264", EncoderProfile.Normalize(encoder));
        }

        [Fact]
        public void Normalize_IsCaseInsensitive_AndPreservesKnownEncoders()
        {
            Assert.Equal("h264_nvenc", EncoderProfile.Normalize("H264_NVENC"));
        }

        [Fact]
        public void Args_SetBitrateAndVbvBuffer()
        {
            string args = EncoderProfile.BuildArgs("h264_nvenc", 6000, 30);

            Assert.Contains("-b:v 6000k", args);
            Assert.Contains("-maxrate 6000k", args);
            Assert.Contains("-bufsize 12000k", args);
        }

        [Theory]
        [InlineData("h264_nvenc")]
        [InlineData("h264_amf")]
        [InlineData("h264_qsv")]
        [InlineData("libx264")]
        public void Args_ForceKeyframesByTime_SoDroppedFramesCantStretchTheInterval(string encoder)
        {
            string args = EncoderProfile.BuildArgs(encoder, 8000, 60);

            Assert.Contains($"-force_key_frames \"expr:gte(t,n_forced*{EncoderProfile.KeyframeIntervalSeconds})\"", args);
        }

        [Fact]
        public void ForcedIdr_IsNvencOnly()
        {
            Assert.Contains("-forced-idr 1", EncoderProfile.BuildArgs("h264_nvenc", 8000, 60));
            Assert.DoesNotContain("-forced-idr", EncoderProfile.BuildArgs("libx264", 8000, 60));
        }

        [Theory]
        [InlineData(30, 60)]
        [InlineData(60, 120)]
        [InlineData(90, 180)]
        public void Args_KeyframeEveryTwoSeconds(int fps, int expectedGop)
        {
            // Regular keyframes keep copy-mode clip starts accurate to ~2s.
            Assert.Contains($"-g {expectedGop}", EncoderProfile.BuildArgs("libx264", 8000, fps));
        }
    }
}
