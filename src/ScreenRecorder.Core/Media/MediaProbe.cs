using System.Globalization;
using System.Text.Json;
using ScreenRecorder.Core.Ffmpeg;

namespace ScreenRecorder.Core.Media;

public sealed record MediaInfo(TimeSpan Duration, int Width, int Height, bool HasAudio, double FrameRate);

public static class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(FfmpegPaths ffmpeg, string path, CancellationToken cancellationToken = default)
    {
        var (result, json) = await FfmpegProcess.RunWithOutputAsync(ffmpeg.Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], cancellationToken);
        result.EnsureSuccess($"Reading '{Path.GetFileName(path)}'");
        return Parse(json);
    }

    internal static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var duration = TimeSpan.Zero;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d) &&
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        int width = 0, height = 0;
        double frameRate = 0;
        var hasAudio = false;
        var hasVideo = false;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            switch (stream.GetProperty("codec_type").GetString())
            {
                case "video" when !hasVideo:
                    hasVideo = true;
                    width = stream.GetProperty("width").GetInt32();
                    height = stream.GetProperty("height").GetInt32();
                    if (stream.TryGetProperty("avg_frame_rate", out var rate)) frameRate = ParseRational(rate.GetString());
                    break;
                case "audio":
                    hasAudio = true;
                    break;
            }
        }

        if (!hasVideo) throw new InvalidDataException("The file has no video stream.");
        return new MediaInfo(duration, width, height, hasAudio, frameRate);
    }

    private static double ParseRational(string? value)
    {
        var parts = value?.Split('/');
        if (parts is not { Length: 2 }) return 0;
        var num = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var den = double.Parse(parts[1], CultureInfo.InvariantCulture);
        return den == 0 ? 0 : num / den;
    }
}
