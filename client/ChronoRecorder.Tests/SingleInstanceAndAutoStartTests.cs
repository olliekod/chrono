using System;
using System.Threading;
using Microsoft.Win32;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class SingleInstanceTests
    {
        [Fact]
        public void OnlyTheFirstInstanceIsFirst_AndTheSecondCanAskItToShow()
        {
            string name = "ChronoTest" + Guid.NewGuid().ToString("N");
            using var first = new SingleInstance(name);
            using var second = new SingleInstance(name);

            Assert.True(first.IsFirst);
            Assert.False(second.IsFirst);

            using var shown = new ManualResetEventSlim();
            first.ListenForShowRequests(() => shown.Set());

            second.AskFirstToShow();

            Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "the first instance should be told to show its window");
        }

        [Fact]
        public void AfterTheFirstIsGone_TheNextLaunchIsFirstAgain()
        {
            string name = "ChronoTest" + Guid.NewGuid().ToString("N");
            var first = new SingleInstance(name);
            Assert.True(first.IsFirst);
            first.Dispose();

            using var next = new SingleInstance(name);
            Assert.True(next.IsFirst);
        }
    }

    public class AutoStartTests
    {
        [Fact]
        public void TheCommandQuotesThePath_AndKeepsTheWindowClosed()
        {
            Assert.Equal("\"C:\\Program Files\\Chrono\\ChronoRecorder.exe\" --background",
                AutoStart.Command(@"C:\Program Files\Chrono\ChronoRecorder.exe"));
        }

        [Theory]
        [InlineData(@"C:\Users\Player\AppData\Local\Chrono\ChronoRecorder.exe", true)]
        [InlineData(@"D:\Games\Chrono\ChronoRecorder.exe", true)]
        [InlineData(@"C:\src\chrono\client\ChronoRecorder\bin\Debug\net8.0-windows\ChronoRecorder.exe", false)]
        [InlineData(@"C:\src\chrono\client\ChronoRecorder\bin\Release\net8.0-windows\ChronoRecorder.exe", false)]
        [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OnlyARealInstallCanBeRegistered(string? path, bool expected)
        {
            Assert.Equal(expected, AutoStart.IsRegistrable(path));
        }

        [Fact]
        public void TurningItOnAndOff_WritesAndRemovesTheRunValue()
        {
            string valueName = "ChronoTest" + Guid.NewGuid().ToString("N");
            try
            {
                Assert.False(AutoStart.IsEnabled(valueName));

                Assert.True(AutoStart.Set(true, @"C:\Users\Player\AppData\Local\Chrono\ChronoRecorder.exe", valueName));
                Assert.True(AutoStart.IsEnabled(valueName));

                Assert.True(AutoStart.Set(false, null, valueName));
                Assert.False(AutoStart.IsEnabled(valueName));
            }
            finally
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }

        [Fact]
        public void ADevelopmentBuild_IsNeverRegistered()
        {
            string valueName = "ChronoTest" + Guid.NewGuid().ToString("N");
            Assert.False(AutoStart.Set(true, @"C:\src\chrono\bin\Debug\ChronoRecorder.exe", valueName));
            Assert.False(AutoStart.IsEnabled(valueName));
        }
    }
}
