using System.Drawing;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class CaptureCommandTests
    {
        private const string Pattern = @"C:\Temp\Chrono\segment_20260920120000_%05d.ts";

        private static CaptureSource Dda(int output = 0, int w = 2560, int h = 1440)
            => new(CaptureMethod.DesktopDuplication, output, new Rectangle(0, 0, w, h));

        private static CaptureRequest Request(string encoder, CaptureSource source, Size? output = null, int fps = 60)
            => new(encoder, fps, 8000, source, output ?? source.Bounds.Size, 10, Pattern);

        // ---------------------------------------------------------------- GPU capture

        [Fact]
        public void Native_OnNvidia_CapturesAndEncodesWithoutTouchingTheCpu()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda(output: 1)));

            Assert.Contains("-f lavfi -i \"ddagrab=output_idx=1:framerate=60\"", args);
            Assert.DoesNotContain("-vf", args);          // no filters: frames stay on the GPU
            Assert.DoesNotContain("-pix_fmt", args);     // would force a CPU conversion
            Assert.DoesNotContain("gdigrab", args);
            Assert.Contains("-c:v h264_nvenc", args);
        }

        [Theory]
        [InlineData("h264_amf")]
        [InlineData("h264_qsv")]
        public void Native_OnOtherHardwareEncoders_AlsoStaysOnTheGpu(string encoder)
        {
            string args = CaptureCommand.Build(Request(encoder, Dda()));

            Assert.DoesNotContain("-vf", args);
            Assert.Contains($"-c:v {encoder}", args);
        }

        [Fact]
        public void Native_WithSoftwareEncoding_MustDownloadTheFrame()
        {
            string args = CaptureCommand.Build(Request("libx264", Dda()));

            Assert.Contains("-vf \"hwdownload,format=bgra,format=yuv420p\"", args);
        }

        [Fact]
        public void Scaled_DownloadsAndScalesOnTheCpu()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda(), new Size(1920, 1080)));

            Assert.Contains("-vf \"hwdownload,format=bgra,scale=1920:1080:flags=fast_bilinear,format=nv12\"", args);
        }

        [Fact]
        public void Scaled_WithSoftwareEncoding_EndsInYuv420p()
        {
            string args = CaptureCommand.Build(Request("libx264", Dda(), new Size(1280, 720)));

            Assert.Contains("scale=1280:720:flags=fast_bilinear,format=yuv420p\"", args);
        }

        [Fact]
        public void UsesTheConfiguredFrameRateAndBitrate()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda(), fps: 90));

            Assert.Contains("framerate=90", args);
            Assert.Contains("-b:v 8000k", args);
            Assert.Contains("-g 180", args);
        }

        // ------------------------------------------------------------ GDI fallback

        [Fact]
        public void Gdi_CapturesOnlyThatMonitorsRegionOfTheDesktop()
        {
            // The second monitor sits left of and above the primary, so its offsets are negative.
            var source = new CaptureSource(CaptureMethod.Gdi, 0, new Rectangle(-1440, -246, 1440, 2560));

            string args = CaptureCommand.Build(Request("h264_nvenc", source));

            Assert.Contains("-f gdigrab -framerate 60 -offset_x -1440 -offset_y -246 -video_size 1440x2560 -i desktop", args);
            Assert.Contains("-pix_fmt yuv420p", args);
            Assert.DoesNotContain("ddagrab", args);
            Assert.DoesNotContain("hwdownload", args);
            Assert.DoesNotContain("-vf", args);
        }

        [Fact]
        public void Gdi_Scaled_UsesASimpleScaleFilter()
        {
            var source = new CaptureSource(CaptureMethod.Gdi, 0, new Rectangle(0, 0, 2560, 1440));

            string args = CaptureCommand.Build(Request("h264_nvenc", source, new Size(1920, 1080)));

            Assert.Contains("-vf \"scale=1920:1080:flags=fast_bilinear\"", args);
        }

        // ---------------------------------------------------------------- audio

        private static AudioInput Pipe(string name = "a", int rate = 48000, int channels = 2, AudioSampleFormat format = AudioSampleFormat.Float32)
            => new($@"\\.\pipe\chrono_{name}", rate, channels, format);

        private static CaptureRequest WithAudio(params AudioInput[] inputs)
            => Request("h264_nvenc", Dda()) with { Audio = inputs, ClockStartUnixSeconds = 1758400000.0 };

        [Fact]
        public void NoAudio_AddsNothingAudioRelated()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda()));

            Assert.DoesNotContain("-map", args);
            Assert.DoesNotContain("-c:a", args);
            Assert.DoesNotContain("pipe", args);
        }

        [Fact]
        public void OneAudioSource_ReadsItsPipeAndMapsBothStreams()
        {
            string args = CaptureCommand.Build(WithAudio(Pipe("sys")));

            Assert.Contains(@"-f f32le -ar 48000 -ac 2 -i \\.\pipe\chrono_sys", args);
            Assert.Contains("-map 0:v:0 -map 1:a:0", args);
            Assert.Contains("-c:a aac -b:a 160k -ar 48000 -ac 2", args);
            Assert.DoesNotContain("amix", args);
        }

        [Fact]
        public void AudioIsReadAfterTheVideoInput_SoStreamNumbersMatchTheMaps()
        {
            string args = CaptureCommand.Build(WithAudio(Pipe("sys")));

            Assert.True(args.IndexOf("ddagrab") < args.IndexOf("pipe"));
            Assert.True(args.IndexOf("pipe") < args.IndexOf("-map"));
        }

        [Fact]
        public void TheAudioFormatOfTheDeviceIsPassedThrough()
        {
            string args = CaptureCommand.Build(WithAudio(Pipe("x", rate: 44100, channels: 8, format: AudioSampleFormat.Pcm16)));

            Assert.Contains(@"-f s16le -ar 44100 -ac 8 -i", args);
            Assert.Contains("-c:a aac -b:a 160k -ar 48000 -ac 2", args);   // always mixed down to stereo for the clip
        }

        [Fact]
        public void TwoAudioSources_AreMixedTogether()
        {
            string args = CaptureCommand.Build(WithAudio(Pipe("sys"), Pipe("mic", rate: 44100, format: AudioSampleFormat.Pcm16)));

            Assert.Contains(@"-i \\.\pipe\chrono_sys", args);
            Assert.Contains(@"-i \\.\pipe\chrono_mic", args);
            Assert.Contains("-filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[aout]\"", args);
            Assert.Contains("-map 0:v:0 -map \"[aout]\"", args);
        }

        [Fact]
        public void Audio_WorksWithTheGdiFallbackToo()
        {
            var gdi = new CaptureSource(CaptureMethod.Gdi, 0, new Rectangle(0, 0, 2560, 1440));
            string args = CaptureCommand.Build(Request("h264_nvenc", gdi) with { Audio = new[] { Pipe("sys") }, ClockStartUnixSeconds = 1758400000.0 });

            Assert.Contains("-f gdigrab", args);
            Assert.Contains("-map 0:v:0 -map 1:a:0", args);
        }

        [Fact]
        public void AudioStaysBeforeTheOutput()
        {
            string args = CaptureCommand.Build(WithAudio(Pipe("sys")));

            Assert.True(args.IndexOf("-c:a") < args.IndexOf("-f segment"));
            Assert.EndsWith($"-y \"{Pattern}\"", args);
        }

        // ------------------------------------------------ audio and picture share one clock

        private const double T0 = 1758400000.1234;
        private const string ClockFilter = "setpts=(time(0)-1758400000.123)/TB";

        private static CaptureRequest Synced(CaptureSource? source = null, Size? output = null, string encoder = "h264_nvenc", int fps = 60)
        {
            var src = source ?? Dda();
            return Request(encoder, src, output, fps) with { Audio = new[] { Pipe("sys") }, ClockStartUnixSeconds = T0 };
        }

        [Fact]
        public void WithAudio_TheVideoIsStampedAgainstTheSharedStartTime()
        {
            string args = CaptureCommand.Build(Synced());

            Assert.Contains($"-vf \"{ClockFilter}\"", args);   // still no CPU work: it only relabels frames
            Assert.Contains("-fps_mode cfr -r 60", args);
        }

        [Fact]
        public void TheStartTimeIsWrittenTheSameWayInAnyCulture()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");   // decimal comma
                string args = CaptureCommand.Build(Synced());

                Assert.Contains("1758400000.123", args);
                Assert.DoesNotContain("1758400000,123", args);
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        }

        [Fact]
        public void TheClockFilterComesFirst_BeforeAnyCpuWork()
        {
            string scaled = CaptureCommand.Build(Synced(output: new Size(1920, 1080)));
            string software = CaptureCommand.Build(Synced(encoder: "libx264"));

            Assert.Contains($"-vf \"{ClockFilter},hwdownload,format=bgra,scale=1920:1080:flags=fast_bilinear,format=nv12\"", scaled);
            Assert.Contains($"-vf \"{ClockFilter},hwdownload,format=bgra,format=yuv420p\"", software);
        }

        [Fact]
        public void TheClockFilterAlsoCoversTheGdiFallback()
        {
            var gdi = new CaptureSource(CaptureMethod.Gdi, 0, new Rectangle(0, 0, 2560, 1440));

            string args = CaptureCommand.Build(Synced(source: gdi, output: new Size(1920, 1080)));

            Assert.Contains($"-vf \"{ClockFilter},scale=1920:1080:flags=fast_bilinear\"", args);
        }

        [Fact]
        public void TheFrameRateFollowsTheSetting()
        {
            Assert.Contains("-fps_mode cfr -r 144", CaptureCommand.Build(Synced(fps: 144)));
        }

        [Fact]
        public void WithoutAudio_NothingChanges()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda()) with { ClockStartUnixSeconds = T0 });

            Assert.DoesNotContain("setpts", args);
            Assert.DoesNotContain("-fps_mode", args);
            Assert.DoesNotContain("-vf", args);
        }

        [Fact]
        public void AudioNeedsTheSharedStartTime()
        {
            var request = Request("h264_nvenc", Dda()) with { Audio = new[] { Pipe("sys") } };

            Assert.Throws<ArgumentException>(() => CaptureCommand.Build(request));
        }

        [Fact]
        public void EveryOutputOptionComesAfterTheLastInput()
        {
            // ffmpeg treats an option placed before an -i as an input option and refuses output-only ones there.
            string args = CaptureCommand.Build(Synced(output: new Size(1920, 1080)) with { Audio = new[] { Pipe("sys"), Pipe("mic") } });

            int lastInput = args.LastIndexOf(" -i ");
            Assert.True(args.IndexOf("-vf") > lastInput);
            Assert.True(args.IndexOf("-map") > lastInput);
            Assert.True(args.IndexOf("-c:v") > lastInput);
            Assert.True(args.IndexOf("-c:a") > lastInput);
            Assert.True(args.IndexOf("-fps_mode") > lastInput);
        }

        // ---------------------------------------------------------- segmenting

        [Fact]
        public void WritesOneContinuousStreamOfRollingSegments()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda()));

            Assert.Contains("-f segment", args);
            Assert.Contains("-segment_time 10", args);
            Assert.Contains("-segment_format mpegts", args);
            Assert.Contains("-segment_format_options flush_packets=1", args);   // in-progress segment stays readable
            Assert.Contains("-reset_timestamps 1", args);
            Assert.EndsWith($"-y \"{Pattern}\"", args);
        }

        [Fact]
        public void KeyframesAreForcedByTime_SoSegmentsSplitOnBoundaries()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda()));

            Assert.Contains("-force_key_frames \"expr:gte(t,n_forced*2)\"", args);
        }

        [Fact]
        public void KeepsFfmpegQuiet()
        {
            string args = CaptureCommand.Build(Request("h264_nvenc", Dda()));

            Assert.StartsWith("-hide_banner -nostats -loglevel warning", args);
        }
    }
}
