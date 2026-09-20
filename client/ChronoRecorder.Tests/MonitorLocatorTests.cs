using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class CaptureSourceChooserTests
    {
        private static readonly Rectangle Primary = new(0, 0, 2560, 1440);

        private static MonitorInfo Monitor(int adapter = 0, int output = 0, bool rotated = false, Rectangle? bounds = null)
            => new(adapter, output, IntPtr.Zero, bounds ?? Primary, @"\\.\DISPLAY1", rotated);

        [Fact]
        public void AMonitorOnTheDefaultGpu_UsesDesktopDuplication_WithItsOwnOutputNumber()
        {
            var source = CaptureSourceChooser.Choose(Monitor(adapter: 0, output: 1), Primary);

            Assert.Equal(CaptureMethod.DesktopDuplication, source.Method);
            Assert.Equal(1, source.OutputIndex);
            Assert.Equal(Primary, source.Bounds);
        }

        [Fact]
        public void AMonitorOnAnotherGpu_FallsBackToGdi()
        {
            // ddagrab only reaches the default GPU's monitors.
            var source = CaptureSourceChooser.Choose(Monitor(adapter: 1), Primary);

            Assert.Equal(CaptureMethod.Gdi, source.Method);
        }

        [Fact]
        public void ARotatedMonitor_FallsBackToGdi()
        {
            // Desktop Duplication hands back the un-rotated panel image, which would record sideways.
            var portrait = new Rectangle(-1440, -246, 1440, 2560);

            var source = CaptureSourceChooser.Choose(Monitor(rotated: true, bounds: portrait), Primary);

            Assert.Equal(CaptureMethod.Gdi, source.Method);
            Assert.Equal(portrait, source.Bounds);
        }

        [Fact]
        public void NoMonitorInfo_RecordsThePrimaryScreenWithGdi()
        {
            var source = CaptureSourceChooser.Choose(null, Primary);

            Assert.Equal(CaptureMethod.Gdi, source.Method);
            Assert.Equal(Primary, source.Bounds);
        }
    }

    /// <summary>These talk to the real display stack, so they only assert what must hold on any machine with a screen.</summary>
    public class MonitorLocatorTests
    {
        [Fact]
        public void FindsTheSameMonitorsWindowsDoes()
        {
            var screens = Screen.AllScreens;
            if (screens.Length == 0) return;   // headless machine

            var monitors = MonitorLocator.Enumerate();

            Assert.Equal(screens.Length, monitors.Count);
            foreach (var screen in screens)
            {
                // Rotated monitors report their un-rotated size in DXGI's rectangle too; compare position and area.
                Assert.Contains(monitors, m => m.Bounds.Location == screen.Bounds.Location
                                               && (long)m.Bounds.Width * m.Bounds.Height == (long)screen.Bounds.Width * screen.Bounds.Height);
            }
        }

        [Fact]
        public void EveryMonitorHasAHandle_AndUniqueOutputNumbersPerGpu()
        {
            var monitors = MonitorLocator.Enumerate();

            Assert.All(monitors, m => Assert.NotEqual(IntPtr.Zero, m.Handle));
            Assert.Equal(monitors.Count, monitors.Select(m => (m.AdapterIndex, m.OutputIndex)).Distinct().Count());
        }

        [Fact]
        public void ThePrimaryMonitorContainsTheOrigin()
        {
            if (Screen.PrimaryScreen == null) return;

            var primary = MonitorLocator.Primary();

            Assert.NotNull(primary);
            Assert.True(primary!.Bounds.Contains(Point.Empty));
        }

        [Fact]
        public void AWindowOnAScreen_ResolvesToThatMonitor()
        {
            if (Screen.PrimaryScreen == null) return;

            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = Screen.PrimaryScreen.Bounds.Location + new Size(50, 50), Size = new Size(200, 200) };
            form.Show();

            var monitor = MonitorLocator.ForWindow(form.Handle);

            Assert.NotNull(monitor);
            Assert.True(monitor!.Bounds.Contains(form.Location));
        }

        [Fact]
        public void ANullWindow_HasNoMonitor()
        {
            Assert.Null(MonitorLocator.ForWindow(IntPtr.Zero));
        }
    }
}
