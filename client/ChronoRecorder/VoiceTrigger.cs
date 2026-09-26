using System;

namespace ChronoRecorder
{
    /// <summary>
    /// The decision behind "voice-activated clip", kept apart from the speech engine so it can be tested without one:
    /// is a recognized phrase sure enough, and far enough from the last one that fired, to actually save a clip.
    /// </summary>
    public static class VoiceTrigger
    {
        /// <summary>How sure the recognizer has to be. With only one phrase loaded, the engine has nothing else to
        /// offer as an answer, so it can force-fit unrelated speech to that phrase and still report a middling
        /// confidence: 0.75 let real false triggers through in practice, so this sits higher, at the cost of
        /// occasionally needing the phrase said again if it goes unheard. `VoiceClipListener` logs every recognized
        /// phrase's confidence (fired or not), so this can be tuned again from what real usage actually reports.</summary>
        public const float MinConfidence = 0.9f;

        /// <summary>One save per utterance: a recognizer can report the same phrase twice for one thing said, and
        /// this is well under how long it takes to actually say "chrono, clip that" again on purpose.</summary>
        public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);

        /// <summary>Whether a phrase heard right now, at this confidence, should actually save a clip.</summary>
        public static bool ShouldFire(float confidence, DateTime nowUtc, DateTime lastTriggerUtc)
            => confidence >= MinConfidence && nowUtc - lastTriggerUtc >= Cooldown;
    }
}
