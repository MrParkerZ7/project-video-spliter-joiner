using FluentAssertions;
using VideoSplitJoiner.Core.Thumbnails;
using Xunit;

namespace VideoSplitJoiner.Core.Tests;

/// <summary>
/// T-196 review (SPEC-005 I28) - <see cref="ThumbnailTimes.ClampBeforeEnd"/>: an exact seek at or past the last frame has no frame to show
/// (ffmpeg exits with no file), where the floored grab it replaced always found one, so a caller that knows the file's length stops a
/// tenth of a second short of the end.
/// </summary>
public sealed class ThumbnailTimesTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // serves-spec: SPEC-005#I28 - a time at or past duration - 100 ms is clamped to it; anything earlier is untouched.
    [Trait("serves-spec", "SPEC-005")]
    [Theory]
    [InlineData(10.5, 10.5, 10.4)]   // the very end
    [InlineData(10.5, 10.49, 10.4)]  // a hair before the end
    [InlineData(10.5, 12.0, 10.4)]   // past it
    [InlineData(10.5, 10.4, 10.4)]   // exactly at the guard: unchanged
    [InlineData(10.5, 10.39, 10.39)]
    [InlineData(10.5, 0.0, 0.0)]
    [InlineData(60.0, 7.5075, 7.5075)]
    public void ATimeNearTheEnd_IsClampedToTheGuard_AnyOtherIsUntouched(double duration, double seek, double expected)
    {
        ThumbnailTimes.ClampBeforeEnd(S(seek), S(duration)).TotalSeconds.Should().BeApproximately(expected, 1e-9);
    }

    // serves-spec: SPEC-005#I28 - an unknown length, and a file shorter than the guard, leave the time alone.
    [Trait("serves-spec", "SPEC-005")]
    [Fact]
    public void WithoutAUsableLength_TheTimeIsLeftAlone()
    {
        ThumbnailTimes.ClampBeforeEnd(S(5), null).Should().Be(S(5));
        ThumbnailTimes.ClampBeforeEnd(S(0.05), S(0.08)).Should().Be(S(0.05), "a file shorter than the guard has nothing to clamp to");
    }
}
