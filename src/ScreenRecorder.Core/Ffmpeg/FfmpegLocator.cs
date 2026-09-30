namespace ScreenRecorder.Core.Ffmpeg;

public sealed record FfmpegPaths(string Ffmpeg, string Ffprobe);

public sealed class FfmpegNotFoundException(string message) : Exception(message);

/// <summary>
/// Finds the pinned FFmpeg binaries: next to the exe (published build), or in this repository's tools\ffmpeg
/// (development). Deliberately never looks on PATH or in unrelated folders, so the app only ever runs the copy that
/// scripts/setup.ps1 verified or the one shipped with the published exe.
/// </summary>
public static class FfmpegLocator
{
    public static FfmpegPaths Locate(string? startDirectory = null)
    {
        var start = startDirectory ?? AppContext.BaseDirectory;

        if (TryDirectory(start, out var paths)) return paths;

        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            // Only the repository root counts (recognised by the FFmpeg lock file next to its tools folder).
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "ffmpeg.lock.json")) &&
                TryDirectory(Path.Combine(dir.FullName, "tools", "ffmpeg"), out paths))
            {
                return paths;
            }
        }

        throw new FfmpegNotFoundException(
            "FFmpeg was not found. Run 'powershell -ExecutionPolicy Bypass -File scripts\\setup.ps1' from the repository " +
            "root to download the pinned, hash-verified build, or place ffmpeg.exe and ffprobe.exe next to ScreenRecorder.exe.");
    }

    private static bool TryDirectory(string directory, out FfmpegPaths paths)
    {
        var ffmpeg = Path.Combine(directory, "ffmpeg.exe");
        var ffprobe = Path.Combine(directory, "ffprobe.exe");
        paths = new FfmpegPaths(ffmpeg, ffprobe);
        return File.Exists(ffmpeg) && File.Exists(ffprobe);
    }
}
