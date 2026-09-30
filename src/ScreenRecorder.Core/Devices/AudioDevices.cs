using System.Text.RegularExpressions;
using ScreenRecorder.Core.Ffmpeg;

namespace ScreenRecorder.Core.Devices;

public static partial class AudioDevices
{
    /// <summary>Lists DirectShow audio capture devices (microphones) by the name FFmpeg expects.</summary>
    public static async Task<IReadOnlyList<string>> ListAsync(FfmpegPaths ffmpeg, CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        // FFmpeg always exits with an error here ("dummy" is not a real input); the listing is in stderr.
        await FfmpegProcess.RunAsync(ffmpeg.Ffmpeg, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"],
            cancellationToken: cancellationToken, onStderrLine: line => { lock (lines) lines.Add(line); });
        lock (lines) return Parse(lines);
    }

    internal static IReadOnlyList<string> Parse(IEnumerable<string> lines) =>
        lines.Select(l => AudioLine().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();

    [GeneratedRegex("\"(.+)\" \\(audio\\)\\s*$")]
    private static partial Regex AudioLine();
}
