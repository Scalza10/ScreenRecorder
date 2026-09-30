using ScreenRecorder.Core.Ffmpeg;
using ScreenRecorder.Core.Media;

namespace ScreenRecorder.Core.Gif;

public sealed record GifOptions(TimeRange Range)
{
    public int Fps { get; init; } = 15;

    /// <summary>Downscale to at most this width (never upscales). Null keeps the source width.</summary>
    public int? MaxWidth { get; init; } = 800;

    /// <summary>0 = loop forever, -1 = play once.</summary>
    public int Loop { get; init; }
}

public static class GifArgsBuilder
{
    public static List<string> Build(string input, string output, GifOptions options)
    {
        if (options.Fps is < 1 or > 50) throw new ArgumentException("GIF frame rate must be between 1 and 50.", nameof(options));
        if (options.MaxWidth is < 16) throw new ArgumentException("GIF width must be at least 16 pixels.", nameof(options));

        var scale = options.MaxWidth is { } w ? $",scale='min({w},iw)':-1:flags=lanczos" : "";
        // One pass: build an optimal 256-colour palette from the clip, then map the frames onto it.
        var filter = $"[0:v]fps={options.Fps}{scale},split[a][b];" +
                     "[a]palettegen=stats_mode=diff[p];" +
                     "[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle";

        return
        [
            "-y", "-hide_banner",
            "-ss", FfmpegTime.Format(options.Range.Start),
            "-t", FfmpegTime.Format(options.Range.Duration),
            "-i", input,
            "-filter_complex", filter,
            "-loop", options.Loop.ToString(System.Globalization.CultureInfo.InvariantCulture),
            output,
        ];
    }

    /// <summary>"Recording_x.mp4" + 10s–17s → "Recording_x_0m10s-0m17s.gif".</summary>
    public static string DefaultFileName(string sourcePath, TimeRange range) =>
        $"{Path.GetFileNameWithoutExtension(sourcePath)}_{Label(range.Start)}-{Label(range.End)}.gif";

    private static string Label(TimeSpan t) =>
        $"{(int)t.TotalMinutes}m{FfmpegTime.Format(t - TimeSpan.FromMinutes((int)t.TotalMinutes))}s";
}

public static class GifExporter
{
    public static async Task ExportAsync(FfmpegPaths ffmpeg, string input, string output, GifOptions options,
        Action<double>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var total = options.Range.Duration.TotalSeconds;
        var result = await FfmpegProcess.RunAsync(ffmpeg.Ffmpeg, GifArgsBuilder.Build(input, output, options),
            t => onProgress?.Invoke(Math.Clamp(t.TotalSeconds / total, 0, 1)), cancellationToken);
        result.EnsureSuccess("GIF export");
    }
}
