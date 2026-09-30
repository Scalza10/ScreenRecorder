using ScreenRecorder.Core.Media;

namespace ScreenRecorder.Core.Tests.Media;

public class TimeTextTests
{
    [Theory]
    [InlineData(0, "0:00.0")]
    [InlineData(10_000, "0:10.0")]
    [InlineData(62_540, "1:02.5")]
    [InlineData(3_723_000, "1:02:03.0")]
    public void Formats_for_display(int ms, string expected)
    {
        Assert.Equal(expected, TimeText.Format(TimeSpan.FromMilliseconds(ms)));
    }

    [Theory]
    [InlineData("10", 10_000)]
    [InlineData("10.5", 10_500)]
    [InlineData("0:10", 10_000)]
    [InlineData("1:02.5", 62_500)]
    [InlineData(" 1:02:03 ", 3_723_000)]
    public void Parses_user_input(string text, int expectedMs)
    {
        Assert.True(TimeText.TryParse(text, out var t));
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), t);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1:75")]
    [InlineData("1:2:3:4")]
    public void Rejects_invalid_input(string text)
    {
        Assert.False(TimeText.TryParse(text, out _));
    }
}
