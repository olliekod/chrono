using Xunit;

namespace ChronoRecorder.Tests
{
    public class UpdateCheckTests
    {
        [Theory]
        [InlineData("1.2.3", 1, 2, 3)]
        [InlineData("v1.2.3", 1, 2, 3)]
        [InlineData("V1.2.3", 1, 2, 3)]
        [InlineData("  v1.2.3  ", 1, 2, 3)]
        [InlineData("1.2.10", 1, 2, 10)]
        [InlineData("v1.2.3 ", 1, 2, 3)]   // surrounding whitespace is trimmed
        public void Parse_ReadsAVersion(string text, int major, int minor, int patch)
        {
            Assert.Equal((major, minor, patch), UpdateCheck.Parse(text));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1.2")]
        [InlineData("1.2.3.4")]
        [InlineData("1.2.3-beta")]
        [InlineData("not a version")]
        public void Parse_RefusesAnythingElse(string? text)
        {
            Assert.Null(UpdateCheck.Parse(text));
        }

        [Theory]
        [InlineData("1.1.4", "1.1.5", true)]     // patch
        [InlineData("1.1.4", "1.2.0", true)]     // minor
        [InlineData("1.1.4", "2.0.0", true)]     // major
        [InlineData("1.1.9", "1.1.10", true)]    // not a string compare: "1.1.10" < "1.1.9" alphabetically, but is newer
        [InlineData("1.2.0", "1.10.0", true)]
        [InlineData("1.1.5", "1.1.5", false)]    // same version
        [InlineData("1.1.5", "1.1.4", false)]    // older
        [InlineData("1.2.0", "1.1.9", false)]
        public void IsNewer_ComparesVersionsNumerically_NotAsText(string current, string candidate, bool expected)
        {
            Assert.Equal(expected, UpdateCheck.IsNewer(current, candidate));
        }

        [Theory]
        [InlineData(null, "1.1.5")]
        [InlineData("1.1.4", null)]
        [InlineData("1.1.4", "not a version")]
        [InlineData("garbage", "garbage")]
        public void IsNewer_IsFalse_WhenEitherSideDoesntParse(string? current, string? candidate)
        {
            Assert.False(UpdateCheck.IsNewer(current, candidate));
        }

        [Fact]
        public void IsNewer_AcceptsTheLeadingVOnTheCandidate_AsGitHubTagsHaveIt()
        {
            Assert.True(UpdateCheck.IsNewer("1.1.4", "v1.1.5"));
        }
    }
}
