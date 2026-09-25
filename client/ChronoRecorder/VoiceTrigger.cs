using System;

namespace ChronoRecorder
{
    /// <summary>
    /// The decision behind "voice-activated clip", kept apart from the speech engine so it can be tested without one:
    /// is a recognized phrase sure enough, and far enough from the last one that fired, to actually save a clip.
    /// </summary>
    public static class VoiceTrigger
    {
        /// <summary>How sure the recognizer has to be. A constrained grammar (the one phrase it listens for, not open
        /// dictation) is usually confident when it matches at all; this mainly guards against a stray sound or a
        /// half-heard word being force-fit to the only phrase the engine knows.</summary>
        public const float MinConfidence = 0.75f;

        /// <summary>One save per utterance: a recognizer can report the same phrase twice for one thing said, and
        /// this is well under how long it takes to actually say "chrono, clip that" again on purpose.</summary>
        public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);

        /// <summary>Whether a phrase heard right now, at this confidence, should actually save a clip.</summary>
        public static bool ShouldFire(float confidence, DateTime nowUtc, DateTime lastTriggerUtc)
            => confidence >= MinConfidence && nowUtc - lastTriggerUtc >= Cooldown;
    }
}
