using Xunit;

namespace ChronoRecorder.Tests
{
    public class MediaInfoParserTests
    {
        // Real output of `ffmpeg -hide_banner -i clip.mp4`.
        private const string Mp4 = @"Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'clip.mp4':
  Duration: 00:00:32.47, start: 0.000000, bitrate: 8490 kb/s
  Stream #0:0[0x1](und): Video: h264 (Main) (avc1 / 0x31637661), yuv420p(tv, bt470bg/bt709/iec61966-2-1, progressive), 2560x1440 [SAR 1:1 DAR 16:9], 7910 kb/s, 59.78 fps, 60 tbr, 90k tbn (default)
  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 161 kb/s (default)
At least one output file must be specified";

        // A recording segment: MPEG-TS, no bitrate on the video stream.
        private const string Ts = @"Input #0, mpegts, from 'segment_1.ts':
  Duration: 00:00:05.55, start: 1.412667, bitrate: 2192 kb/s
  Stream #0:0[0x100]: Video: h264 (High) ([27][0][0][0] / 0x001B), yuv420p(tv, bt470bg/bt709/iec61966-2-1, progressive), 1440x2560 [SAR 1:1 DAR 9:16], 60 fps, 60 tbr, 90k tbn
  Stream #0:1[0x101]: Audio: aac (LC) ([15][0][0][0] / 0x000F), 48000 Hz, stereo, fltp, 162 kb/s";

        [Fact]
        public void ReadsTheDuration()
        {
            Assert.Equal(32.47, MediaInfoParser.Duration(Mp4)!.Value, 3);
            Assert.Equal(5.55, MediaInfoParser.Duration(Ts)!.Value, 3);
        }

        [Fact]
        public void ReadsHoursAndMinutes()
        {
            Assert.Equal(3723.5, MediaInfoParser.Duration("  Duration: 01:02:03.50, start: 0")!.Value, 3);
        }

        [Theory]
        [InlineData("  Duration: N/A, bitrate: N/A")]
        [InlineData("nothing useful here")]
        [InlineData("")]
        [InlineData(null)]
        public void NoDuration_IsNull(string? text)
        {
            Assert.Null(MediaInfoParser.Duration(text));
        }

        [Fact]
        public void ReadsThePictureSize_NotTheCodecTag()
        {
            // "0x31637661" sits between the codec name and the size and must not be mistaken for one.
            Assert.Equal("2560x1440", MediaInfoParser.VideoSize(Mp4));
            Assert.Equal("1440x2560", MediaInfoParser.VideoSize(Ts));
        }

        [Fact]
        public void TheAudioLineIsNotAPicture()
        {
            Assert.Null(MediaInfoParser.VideoSize("  Stream #0:0: Audio: aac (LC), 48000 Hz, stereo, fltp, 161 kb/s"));
            Assert.Null(MediaInfoParser.VideoSize(""));
            Assert.Null(MediaInfoParser.VideoSize(null));
        }
    }
}
