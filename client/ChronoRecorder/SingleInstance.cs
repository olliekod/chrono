using System;
using System.Threading;

namespace ChronoRecorder
{
    /// <summary>
    /// Only one Chrono may run: two recorders would fight over the hotkeys, the temp folder and the encoder. A second
    /// launch (double-clicking the shortcut again) instead asks the first to show its window, then exits.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        private readonly Mutex mutex;
        private readonly EventWaitHandle showSignal;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private Thread? listener;

        /// <summary>True when this is the first Chrono; false when one is already running.</summary>
        public bool IsFirst { get; }

        public SingleInstance(string name = "ChronoRecorder")
        {
            mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.Instance", out bool created);
            IsFirst = created;
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
        }

        /// <summary>Second instance: ask the running Chrono to show itself.</summary>
        public void AskFirstToShow() => showSignal.Set();

        /// <summary>First instance: call <paramref name="onShow"/> (on a background thread) whenever another launch asks.</summary>
        public void ListenForShowRequests(Action onShow)
        {
            listener = new Thread(() =>
            {
                var handles = new[] { showSignal, stop.Token.WaitHandle };
                while (WaitHandle.WaitAny(handles) == 0)
                {
                    try { onShow(); } catch (Exception ex) { Console.WriteLine($"Show request failed: {ex.Message}"); }
                }
            }) { IsBackground = true, Name = "Chrono single instance" };
            listener.Start();
        }

        public void Dispose()
        {
            stop.Cancel();
            listener?.Join(1000);
            if (IsFirst) { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } }
            mutex.Dispose();
            showSignal.Dispose();
            stop.Dispose();
        }
    }
}
