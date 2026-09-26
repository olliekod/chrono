using System;
using System.Speech.Recognition;

namespace ChronoRecorder
{
    /// <summary>
    /// Listens for "chrono, clip that" on the default microphone and raises <see cref="Triggered"/> when it hears it,
    /// so a clip can be saved hands-free. Windows' own offline speech recognizer (<c>System.Speech</c>, the older
    /// SAPI-based one that ships with Windows): no cloud service, no download, no internet needed. A constrained
    /// grammar - just the one phrase, not open dictation - is far more reliable and far lighter than listening for
    /// anything at all and trying to make sense of it, the same reason hotkeys are exact key combinations rather than
    /// "something that feels like a save".
    /// <see cref="VoiceTrigger"/> holds the (testable) decision of whether a recognized phrase should actually fire;
    /// this class is the (hand-verified, needs a real microphone and Windows' speech component) plumbing to it.
    /// </summary>
    public sealed class VoiceClipListener : IDisposable
    {
        private const string Phrase = "chrono clip that";

        private SpeechRecognitionEngine? engine;
        private DateTime lastTriggerUtc = DateTime.MinValue;

        /// <summary>Raised (on the recognizer's own background thread, not the UI thread) when the phrase is heard.</summary>
        public event Action? Triggered;

        /// <summary>Something stopped listening from working (no microphone, no speech recognizer installed, ...).
        /// Never thrown - only ever raised, the same "never worth crashing over" treatment as <see cref="ClipCue"/>.</summary>
        public event Action<string>? Failed;

        public bool IsListening => engine != null;

        /// <summary>How many times <see cref="Reconcile"/> has run, and whether the last call asked for listening to be
        /// on. For tests: whether a real engine could then actually start depends on this PC having a working
        /// microphone and speech recognizer, which a test run can't assume - this checks the wiring runs at all,
        /// not that far.</summary>
        public int ReconcileCalls { get; private set; }
        public bool? LastReconciledTo { get; private set; }

        /// <summary>Starts and stops listening to match <see cref="RecorderConfig.VoiceClipEnabled"/>. Safe to call
        /// after every settings save, whether or not the setting actually changed.</summary>
        public void Reconcile(RecorderConfig config)
        {
            ReconcileCalls++;
            LastReconciledTo = config.VoiceClipEnabled;
            if (config.VoiceClipEnabled) Start(); else Stop();
        }

        /// <summary>Starts listening. Safe to call again while already listening (it just keeps going); never throws.</summary>
        public void Start()
        {
            if (engine != null) return;

            try
            {
                var recognizer = new SpeechRecognitionEngine();
                recognizer.LoadGrammar(new Grammar(new GrammarBuilder(Phrase)));
                recognizer.SetInputToDefaultAudioDevice();
                recognizer.SpeechRecognized += OnSpeechRecognized;
                recognizer.RecognizeAsync(RecognizeMode.Multiple);
                engine = recognizer;
            }
            catch (Exception ex)
            {
                engine = null;
                Failed?.Invoke($"Couldn't listen for \"chrono, clip that\" ({ex.Message}).");
            }
        }

        public void Stop()
        {
            var recognizer = engine;
            if (recognizer == null) return;
            engine = null;

            recognizer.SpeechRecognized -= OnSpeechRecognized;
            try { recognizer.RecognizeAsyncStop(); } catch { /* already stopped */ }
            recognizer.Dispose();
        }

        private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            var now = DateTime.UtcNow;
            bool fires = VoiceTrigger.ShouldFire(e.Result.Confidence, now, lastTriggerUtc);

            // Logged every time, not just on a real trigger: the only way to see what the recognizer is actually
            // matching false triggers against (it only ever reports this one phrase, since it's the only one loaded).
            Console.WriteLine($"Voice-activated clip heard \"{e.Result.Text}\" at {e.Result.Confidence:0.00} confidence{(fires ? "" : " (ignored)")}");

            if (!fires) return;

            lastTriggerUtc = now;
            Triggered?.Invoke();
        }

        public void Dispose() => Stop();
    }
}
