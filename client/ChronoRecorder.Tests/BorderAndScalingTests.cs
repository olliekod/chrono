using System;
using System.Drawing;
using System.Linq;
using Xunit;

namespace ChronoRecorder.Tests
{
    /// <summary>
    /// The yellow border on Windows 10, and recording at the screen's real size under display scaling. Both came from a friend
    /// on Windows 10 with a scaled 2560x1440 screen (Windows told Chrono it was 2048x1152).
    /// </summary>
    public class YellowBorderTests
    {
        [Theory]
        [InlineData(10240, false)]   // Windows 10 1507
        [InlineData(17763, false)]   // 1809
        [InlineData(19041, false)]   // 2004
        [InlineData(19045, false)]   // 22H2, what the friend has
        [InlineData(21999, false)]
        [InlineData(22000, true)]    // the first Windows 11
        [InlineData(22631, true)]
        [InlineData(26200, true)]    // the dev PC
        public void OnlyWindows11CanCaptureAWindowWithoutABorder(int build, bool expected)
        {
            Assert.Equal(expected, WindowCaptureSupport.HasBorderlessCapture(build));
        }

        [Fact]
        public void OnWindows11_AutomaticCapturesTheGameAsItsOwnWindow()
        {
            Assert.Equal(GameCapturePlan.Window, GameCapturePlanner.Plan("auto", false, true, borderlessCapture: true));
            Assert.Equal(GameCapturePlan.Window, GameCapturePlanner.Plan(null, false, true, borderlessCapture: true));
        }

        [Theory]
        [InlineData("auto")]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("something the settings page never sent")]
        public void OnWindows10_AutomaticRecordsTheMonitorWhileTheGameIsInFront_SoNoBorderIsDrawn(string? setting)
        {
            Assert.Equal(GameCapturePlan.MonitorWhenInFront, GameCapturePlanner.Plan(setting, false, true, borderlessCapture: false));
        }

        [Fact]
        public void OnWindows10_ChoosingTheWindowIsRespected_ForSomeoneWhoDoesntMindTheBorder()
        {
            Assert.Equal(GameCapturePlan.Window, GameCapturePlanner.Plan("window", false, true, borderlessCapture: false));
            Assert.Equal(GameCapturePlan.Window, GameCapturePlanner.Plan("WINDOW", false, true, borderlessCapture: false));
        }

        [Fact]
        public void ChoosingTheMonitor_IsTheMonitorOnEveryWindowsVersion()
        {
            Assert.Equal(GameCapturePlan.MonitorWhenInFront, GameCapturePlanner.Plan("monitor", false, true, borderlessCapture: true));
            Assert.Equal(GameCapturePlan.MonitorWhenInFront, GameCapturePlanner.Plan("monitor", false, true, borderlessCapture: false));
        }

        [Theory]
        [InlineData("auto", true)]
        [InlineData("auto", false)]
        [InlineData("window", false)]
        [InlineData("monitor", false)]
        public void WithNoWindowToCapture_NothingIsRecorded_OnAnyWindowsVersion(string setting, bool borderless)
        {
            Assert.Equal(GameCapturePlan.Unavailable, GameCapturePlanner.Plan(setting, false, haveWindow: false, borderlessCapture: borderless));
        }

        [Fact]
        public void TheMonitorOnlyEverRecordsWhileTheGameIsInFront_SoTheDesktopStaysOutOfClips()
        {
            var plan = GameCapturePlanner.Plan("auto", false, true, borderlessCapture: false);

            Assert.False(GameCapturePlanner.MayCapture(plan, gameInFront: false));
            Assert.True(GameCapturePlanner.MayCapture(plan, gameInFront: true));
        }

        // -------------------------------------------------------------- old settings files

        [Fact]
        public void AnOldConfigThatSaysWindow_BecomesAutomatic_BecauseItWasTheDefaultNotAChoice()
        {
            var old = new RecorderConfig { ConfigVersion = 1, GameCapture = "window", RecorderEnabled = false, Mode = RecorderConfig.RecordingMode.Display };

            old.MigrateLegacyValues();

            Assert.Equal("auto", old.GameCapture);
            Assert.Equal(RecorderConfig.CurrentConfigVersion, old.ConfigVersion);
            // and nothing else that version 1 already settled is undone
            Assert.False(old.RecorderEnabled);
            Assert.Equal(RecorderConfig.RecordingMode.Display, old.Mode);
        }

        [Fact]
        public void AVersionZeroConfig_GetsBothMigrations()
        {
            var old = new RecorderConfig { ConfigVersion = 0, GameCapture = "window", RecorderEnabled = false, Mode = RecorderConfig.RecordingMode.Display };

            old.MigrateLegacyValues();

            Assert.Equal("auto", old.GameCapture);
            Assert.True(old.RecorderEnabled);
            Assert.Equal(RecorderConfig.RecordingMode.Auto, old.Mode);
        }

        [Fact]
        public void AChoiceMadeAfterThat_IsKept()
        {
            var current = new RecorderConfig { ConfigVersion = RecorderConfig.CurrentConfigVersion, GameCapture = "window" };

            current.MigrateLegacyValues();

            Assert.Equal("window", current.GameCapture);   // picked on purpose in Settings
        }

        [Fact]
        public void TheMonitorChoice_IsNeverChanged()
        {
            var old = new RecorderConfig { ConfigVersion = 1, GameCapture = "monitor" };

            old.MigrateLegacyValues();

            Assert.Equal("monitor", old.GameCapture);
        }
    }

    public class DisplayScalingTests
    {
        [Fact]
        public void A2560x1440ScreenAt125Percent_IsRecordedAtItsRealSize_NotTheScaledOne()
        {
            // Windows told a program that isn't DPI aware that the screen was 2048x1152.
            Assert.Equal(new Size(2560, 1440), DisplayScaling.Plausible(new Size(2048, 1152), new Size(2560, 1440)));
        }

        [Theory]
        [InlineData(1920, 1080, 1920, 1080)]   // no scaling
        [InlineData(2560, 1440, 3840, 2160)]   // 150%
        [InlineData(1536, 864, 1920, 1080)]    // a 1080p laptop at 125%
        public void ScalingThatAddsUp_IsUndone(int rw, int rh, int pw, int ph)
        {
            Assert.Equal(new Size(pw, ph), DisplayScaling.Plausible(new Size(rw, rh), new Size(pw, ph)));
        }

        [Fact]
        public void ARealSizeSmallerThanTheReportedOne_IsNotBelieved()
        {
            Assert.Equal(new Size(2560, 1440), DisplayScaling.Plausible(new Size(2560, 1440), new Size(1920, 1080)));
        }

        [Fact]
        public void ARotatedScreen_WhoseModeComesBackTheOtherWayRound_IsLeftAlone()
        {
            // 1440x2560 reported, 2560x1440 in the mode: not the same shape, so not a scaling of it.
            Assert.Equal(new Size(1440, 2560), DisplayScaling.Plausible(new Size(1440, 2560), new Size(2560, 1440)));
        }

        [Fact]
        public void AWildRatio_IsNotBelieved()
        {
            Assert.Equal(new Size(640, 360), DisplayScaling.Plausible(new Size(640, 360), new Size(3840, 2160)));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(-5, 100)]
        public void NothingToScale_IsLeftAlone(int w, int h)
        {
            Assert.Equal(new Size(w, h), DisplayScaling.Plausible(new Size(w, h), new Size(2560, 1440)));
        }

        [Fact]
        public void TheRealSizeIsAlwaysEven_BecauseVideoIs()
        {
            var size = DisplayScaling.Plausible(new Size(1281, 721), new Size(1601, 901));

            Assert.Equal(0, size.Width % 2);
            Assert.Equal(0, size.Height % 2);
        }

        [Fact]
        public void OnThisPc_TheRealSizeIsNeverSmallerThanWhatWindowsReports()
        {
            foreach (var m in MonitorLocator.Enumerate())
            {
                Assert.True(m.Pixels.Width >= m.Bounds.Width && m.Pixels.Height >= m.Bounds.Height);
                Assert.Equal(0, m.Pixels.Width % 2);
            }
        }

        // ----------------------------------------------------------- what gets recorded

        private static MonitorInfo Scaled(bool rotated = false, int adapter = 0)
            => new(adapter, 0, IntPtr.Zero, new Rectangle(0, 0, 2048, 1152), @"\\.\DISPLAY1", rotated, "GPU", 0x10DE, new Size(2560, 1440));

        [Fact]
        public void AMonitorWithNoScalingInfo_IsItsReportedSize()
        {
            var plain = new MonitorInfo(0, 0, IntPtr.Zero, new Rectangle(0, 0, 1920, 1080), "d", false);

            Assert.Equal(new Size(1920, 1080), plain.Pixels);
        }

        [Fact]
        public void DesktopDuplication_RecordsTheRealPixels()
        {
            var source = CaptureSourceChooser.Choose(Scaled(), new Rectangle(0, 0, 2048, 1152));

            Assert.Equal(CaptureMethod.DesktopDuplication, source.Method);
            Assert.Equal(new Size(2560, 1440), source.Bounds.Size);
        }

        [Fact]
        public void AWindowCapture_IsMadeAtTheRealPixels_SoNothingIsShrunkFirst()
        {
            var source = CaptureSourceChooser.ForWindow(new IntPtr(1234), Scaled(), new Rectangle(0, 0, 2048, 1152));

            Assert.Equal(CaptureMethod.WindowCapture, source.Method);
            Assert.Equal(new Size(2560, 1440), source.Bounds.Size);
        }

        [Fact]
        public void GdiWorksInTheScaledCoordinatesWindowsGivesThisProgram()
        {
            // A rotated screen goes through GDI, which is told a region in the coordinates Windows reports.
            var source = CaptureSourceChooser.Choose(Scaled(rotated: true), new Rectangle(0, 0, 2048, 1152));

            Assert.Equal(CaptureMethod.Gdi, source.Method);
            Assert.Equal(new Size(2048, 1152), source.Bounds.Size);
        }
    }

    public class DroppedFramesAndBorderDiagnosticsTests
    {
        private static FindingInputs Healthy() => new(
            Recording: true, Encoder: "h264_nvenc", EncoderNote: "", GpuType: GpuDetector.GpuType.NVIDIA, GpuTier: GpuDetector.GpuTier.Capable,
            Method: CaptureMethod.WindowCapture, Speed: 1.0, Dropped: 0, FfmpegCpuOneCore: 3, EncodeEnginePercent: 10, ThreeDEnginePercent: 20,
            LoadLevel: 0, ConfiguredFps: 60, ScreenAdapterVendorId: 0x10DE, ScreenAdapterName: "NVIDIA GeForce RTX 4080", RamGb: 64, GpuName: "NVIDIA GeForce RTX 4080");

        private static string AllText(FindingInputs f) => string.Join("\n", DiagnosticsFindings.Evaluate(f).Select(x => x.Text));

        [Fact]
        public void DroppedFrames_AreNotAProblemByThemselves()
        {
            // The friend's report: 1845 "dropped" after alt-tabbing, at 1.00x speed and 60.3 encoded FPS. Nothing was lost.
            var findings = DiagnosticsFindings.Evaluate(Healthy() with { Dropped = 1845, Speed = 1.0 });

            Assert.Single(findings);
            Assert.Equal("good", findings[0].Tone);
            Assert.DoesNotContain("dropped", AllText(Healthy() with { Dropped = 1845 }));
        }

        [Fact]
        public void WhatShowsARecordingFallingBehind_IsTheSpeed()
        {
            Assert.Contains("bad", DiagnosticsFindings.Evaluate(Healthy() with { Speed = 0.8, Dropped = 0 }).Select(f => f.Tone));
        }

        [Fact]
        public void OnWindows10_RecordingTheScreen_IsExplainedByTheYellowBorder()
        {
            var f = Healthy() with { Method = CaptureMethod.DesktopDuplication, BorderlessCapture = false, GameCapture = "auto" };

            var text = AllText(f);

            Assert.Contains("yellow border", text);
            Assert.Contains("alt-tab", text);
        }

        [Fact]
        public void TheBorderExplanation_IsNotGivenWhereItDoesntApply()
        {
            Assert.DoesNotContain("yellow border", AllText(Healthy() with { Method = CaptureMethod.DesktopDuplication, BorderlessCapture = true }));   // Windows 11
            Assert.DoesNotContain("yellow border", AllText(Healthy() with { Method = CaptureMethod.DesktopDuplication, BorderlessCapture = false, GameCapture = "monitor" }));   // chosen
            Assert.DoesNotContain("yellow border", AllText(Healthy() with { Method = CaptureMethod.WindowCapture, BorderlessCapture = false, GameCapture = "window" }));
        }
    }
}
