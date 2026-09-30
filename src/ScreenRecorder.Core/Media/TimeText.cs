using System.Globalization;

namespace ScreenRecorder.Core.Media;

/// <summary>Human-friendly times: "1:02.5" for display, and "62.5", "1:02.5" or "1:02:03" as input.</summary>
public static class TimeText
{
    public static string Format(TimeSpan time)
    {
        var tenths = (long)Math.Floor(time.TotalMilliseconds / 100);
        var t = TimeSpan.FromMilliseconds(tenths * 100);
        var fraction = t.Milliseconds / 100;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{fraction}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{fraction}";
    }

    public static bool TryParse(string? text, out TimeSpan time)
    {
        time = default;
        var parts = (text ?? "").Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;

        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)) return false;
        if (parts.Length > 1 && seconds >= 60) return false;

        double total = seconds;
        var multiplier = 60;
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var unit)) return false;
            if (i > 0 && unit >= 60) return false;
            total += unit * multiplier;
            multiplier *= 60;
        }

        time = TimeSpan.FromSeconds(total);
        return true;
    }
}
