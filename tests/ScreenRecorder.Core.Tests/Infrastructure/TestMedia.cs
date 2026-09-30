using ScreenRecorder.Core.Ffmpeg;

namespace ScreenRecorder.Core.Tests.Infrastructure;

/// <summary>
/// Generates deterministic fixture videos with FFmpeg's built-in test sources, so tests need no real screen.
/// Files are created once per test run and shared.
/// </summary>
public static class TestMedia
{
    private static readonly Lazy<FfmpegPaths> LazyPaths = new(() => FfmpegLocator.Locate());
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, string> Cache = new();

    public static FfmpegPaths Ffmpeg => LazyPaths.Value;

    public static string Root { get; } = CreateRoot();

    private static string CreateRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ScreenRecorderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Don't leave test videos (possibly including a screen capture) lying around in %TEMP%.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
        return dir;
    }

    public static string NewTempDir()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>60 s, 1280x720, 30 fps H.264 + AAC sine tone MP4.</summary>
    public static Task<string> SixtySecondClipAsync() => GetOrCreateAsync("clip60.mp4", 60, 1280, 720, withAudio: true);

    /// <summary>6 s, 640x360, 30 fps, video-only MP4 (like a recording made without a microphone).</summary>
    public static Task<string> SilentClipAsync() => GetOrCreateAsync("silent6.mp4", 6, 640, 360, withAudio: false);

    private static async Task<string> GetOrCreateAsync(string name, int seconds, int width, int height, bool withAudio)
    {
        await Gate.WaitAsync();
        try
        {
            if (Cache.TryGetValue(name, out var existing)) return existing;
            var path = Path.Combine(Root, name);
            var args = new List<string>
            {
                "-y", "-hide_banner", "-v", "error",
                "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30:duration={seconds}",
            };
            if (withAudio) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds}"]);
            args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
            if (withAudio) args.AddRange(["-c:a", "aac", "-shortest"]);
            args.Add(path);

            var result = await FfmpegProcess.RunAsync(Ffmpeg.Ffmpeg, args);
            result.EnsureSuccess("Fixture generation");
            Cache[name] = path;
            return path;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
