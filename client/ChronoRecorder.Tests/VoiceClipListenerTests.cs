using Xunit;

namespace ChronoRecorder.Tests
{
    /// <summary>
    /// Only the parts that don't need a real microphone or speech recognizer installed: whether it actually starts
    /// listening depends on both, which a test run can't assume it has (see VoiceClipListener's own remarks). That
    /// part is verified by hand; VoiceTriggerTests covers the decision of when a heard phrase should fire.
    /// </summary>
    public sealed class VoiceClipListenerTests : IDisposable
    {
        private readonly VoiceClipListener listener = new();

        public void Dispose() => listener.Dispose();

        [Fact]
        public void StartsNotListening() => Assert.False(listener.IsListening);

        [Fact]
        public void Reconcile_WithItTurnedOff_NeverStartsListening()
        {
            listener.Reconcile(new RecorderConfig { VoiceClipEnabled = false });

            Assert.False(listener.IsListening);
        }

        [Fact]
        public void Reconcile_RecordsThatItRan_AndWhatItAskedFor()
        {
            listener.Reconcile(new RecorderConfig { VoiceClipEnabled = false });
            listener.Reconcile(new RecorderConfig { VoiceClipEnabled = true });

            Assert.Equal(2, listener.ReconcileCalls);
            Assert.True(listener.LastReconciledTo);
        }

        [Fact]
        public void StopIsSafeToCallWhenNeverStarted()
        {
            Assert.Null(Record.Exception(() => listener.Stop()));
            Assert.False(listener.IsListening);
        }

        [Fact]
        public void DisposeIsSafeToCallMoreThanOnce()
        {
            listener.Dispose();
            Assert.Null(Record.Exception(() => listener.Dispose()));
        }
    }
}
