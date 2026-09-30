namespace ScreenRecorder.Core.Ffmpeg;

public sealed record FfmpegPaths(string Ffmpeg, string Ffprobe);

public sealed class FfmpegNotFoundException(string message) : Exception(message);

/// <summary>
/// Finds the pinned FFmpeg binaries. Deliberately never falls back to PATH, so the app only ever runs the
/// copy that scripts/setup.ps1 verified (or the one shipped next to the published exe).
/// </summary>
public static class FfmpegLocator
{
    public static FfmpegPaths Locate(string? startDirectory = null)
    {
        var start = startDirectory ?? AppContext.BaseDirectory;

        if (TryDirectory(start, out var paths)) return paths;

        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (TryDirectory(Path.Combine(dir.FullName, "tools", "ffmpeg"), out paths)) return paths;
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
