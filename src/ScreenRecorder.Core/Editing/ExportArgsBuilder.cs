using System.Text;
using ScreenRecorder.Core.Ffmpeg;

namespace ScreenRecorder.Core.Editing;

public static class ExportArgsBuilder
{
    public static List<string> Build(string input, string output, EditPlan plan)
    {
        var hasAudio = plan.Source.HasAudio;
        var graph = new StringBuilder();
        var concatInputs = new StringBuilder();

        for (var i = 0; i < plan.KeptSegments.Count; i++)
        {
            var segment = plan.KeptSegments[i];
            var start = FfmpegTime.Format(segment.Start);
            var end = FfmpegTime.Format(segment.End);
            graph.Append($"[0:v]trim=start={start}:end={end},setpts=PTS-STARTPTS[v{i}];");
            concatInputs.Append($"[v{i}]");
            if (hasAudio)
            {
                graph.Append($"[0:a]atrim=start={start}:end={end},asetpts=PTS-STARTPTS[a{i}];");
                concatInputs.Append($"[a{i}]");
            }
        }

        graph.Append(concatInputs)
            .Append($"concat=n={plan.KeptSegments.Count}:v=1:a={(hasAudio ? 1 : 0)}[vc]{(hasAudio ? "[ac]" : "")};");

        var videoFilters = new List<string>();
        if (plan.Crop is { } c) videoFilters.Add($"crop={c.Width}:{c.Height}:{c.X}:{c.Y}");
        if (plan.Speed != 1.0) videoFilters.Add($"setpts=PTS/{FfmpegTime.Number(plan.Speed)}");
        // Keep a constant frame rate so speed changes drop/duplicate frames instead of producing odd timestamps.
        var fps = plan.Source.FrameRate is > 0 and <= 240 ? plan.Source.FrameRate : 30;
        videoFilters.Add($"fps={FfmpegTime.Number(fps)}");
        videoFilters.Add("format=yuv420p");
        graph.Append($"[vc]{string.Join(",", videoFilters)}[vout]");

        if (hasAudio)
        {
            var audioFilters = AtempoChain(plan.Speed);
            graph.Append($";[ac]{(audioFilters.Count == 0 ? "anull" : string.Join(",", audioFilters))}[aout]");
        }

        var args = new List<string>
        {
            "-y", "-hide_banner",
            "-i", input,
            "-filter_complex", graph.ToString(),
            "-map", "[vout]",
        };
        if (hasAudio) args.AddRange(["-map", "[aout]", "-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-movflags", "+faststart", output]);
        return args;
    }

    /// <summary>atempo only accepts factors in [0.5, 2] reliably, so larger changes are chained.</summary>
    public static List<string> AtempoChain(double speed)
    {
        var filters = new List<string>();
        var remaining = speed;
        while (remaining > 2.0) { filters.Add("atempo=2"); remaining /= 2.0; }
        while (remaining < 0.5) { filters.Add("atempo=0.5"); remaining /= 0.5; }
        if (Math.Abs(remaining - 1.0) > 1e-9) filters.Add($"atempo={FfmpegTime.Number(remaining)}");
        return filters;
    }
}

public static class VideoExporter
{
    public static async Task ExportAsync(FfmpegPaths ffmpeg, string input, string output, EditPlan plan,
        Action<double>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Export must not overwrite the original recording.", nameof(output));

        var total = plan.OutputDuration.TotalSeconds;
        var result = await FfmpegProcess.RunAsync(ffmpeg.Ffmpeg, ExportArgsBuilder.Build(input, output, plan),
            t => onProgress?.Invoke(Math.Clamp(t.TotalSeconds / total, 0, 1)), cancellationToken);
        result.EnsureSuccess("Video export");
    }
}

public static class OutputNaming
{
    public static string Edited(string sourcePath) =>
        Path.Combine(Path.GetDirectoryName(sourcePath) ?? "", Path.GetFileNameWithoutExtension(sourcePath) + "_edited.mp4");

    /// <summary>Appends " (2)", " (3)", … until the path does not exist.</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
