using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenRecorder.Core.Ffmpeg;

public static partial class FfmpegTime
{
    /// <summary>Formats a time as plain seconds ("62.25"), which every FFmpeg option and filter accepts.</summary>
    public static string Format(TimeSpan time) =>
        Math.Round(time.TotalSeconds, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Formats a number (speed factor, fps, …) invariantly.</summary>
    public static string Number(double value) =>
        Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Extracts the "time=HH:MM:SS.ss" value from an FFmpeg progress line.</summary>
    public static bool TryParseProgress(string line, out TimeSpan time)
    {
        var match = ProgressRegex().Match(line);
        if (match.Success &&
            TimeSpan.TryParseExact(match.Groups[1].Value, @"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture, out time))
        {
            return true;
        }

        time = default;
        return false;
    }

    [GeneratedRegex(@"time=(\d{2,}:\d{2}:\d{2}\.\d+)")]
    private static partial Regex ProgressRegex();
}
