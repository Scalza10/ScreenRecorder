using ScreenRecorder.Core.Media;

namespace ScreenRecorder.Core.Tests.Media;

public class TimeRangeTests
{
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Duration_is_end_minus_start()
    {
        Assert.Equal(S(7), new TimeRange(S(10), S(17)).Duration);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(-1, 5)]
    public void Rejects_invalid_ranges(double start, double end)
    {
        Assert.Throws<ArgumentException>(() => new TimeRange(S(start), S(end)));
    }

    [Fact]
    public void Overlap_detection()
    {
        var a = new TimeRange(S(10), S(20));
        Assert.True(a.Overlaps(new TimeRange(S(15), S(25))));
        Assert.False(a.Overlaps(new TimeRange(S(20), S(25))));
        Assert.True(a.Contains(S(10)));
        Assert.False(a.Contains(S(20)));
    }

    [Fact]
    public void Clamp_limits_to_bounds()
    {
        var clamped = new TimeRange(S(5), S(70)).ClampTo(S(60));
        Assert.Equal(new TimeRange(S(5), S(60)), clamped);
    }
}
