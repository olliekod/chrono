using System;
using Xunit;

namespace ChronoRecorder.Tests
{
    public class VoiceTriggerTests
    {
        private static readonly DateTime Start = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void AConfidentPhrase_WithNothingBeforeIt_Fires()
        {
            Assert.True(VoiceTrigger.ShouldFire(0.9f, Start, DateTime.MinValue));
        }

        [Theory]
        [InlineData(0.74f)]
        [InlineData(0.5f)]
        [InlineData(0f)]
        public void AnUnsureRecognition_DoesNotFire(float confidence)
        {
            Assert.False(VoiceTrigger.ShouldFire(confidence, Start, DateTime.MinValue));
        }

        [Fact]
        public void RightAtTheConfidenceLine_Fires()
        {
            Assert.True(VoiceTrigger.ShouldFire(VoiceTrigger.MinConfidence, Start, DateTime.MinValue));
        }

        [Fact]
        public void HeardAgainRightAway_IsIgnored_SoOneUtteranceIsOneClip()
        {
            var lastTrigger = Start;
            var now = Start + TimeSpan.FromSeconds(0.5);

            Assert.False(VoiceTrigger.ShouldFire(0.95f, now, lastTrigger));
        }

        [Fact]
        public void OnceTheCooldownHasPassed_ItCanFireAgain()
        {
            var lastTrigger = Start;
            var now = Start + VoiceTrigger.Cooldown;

            Assert.True(VoiceTrigger.ShouldFire(0.95f, now, lastTrigger));
        }

        [Fact]
        public void JustShortOfTheCooldown_StillWaits()
        {
            var lastTrigger = Start;
            var now = Start + VoiceTrigger.Cooldown - TimeSpan.FromMilliseconds(1);

            Assert.False(VoiceTrigger.ShouldFire(0.95f, now, lastTrigger));
        }

        [Fact]
        public void LowConfidence_IsRefused_EvenLongAfterTheCooldown()
        {
            var lastTrigger = Start;
            var now = Start + TimeSpan.FromMinutes(10);

            Assert.False(VoiceTrigger.ShouldFire(0.1f, now, lastTrigger));
        }
    }
}
