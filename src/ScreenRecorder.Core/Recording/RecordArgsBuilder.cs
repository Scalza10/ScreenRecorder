using System.Globalization;
using ScreenRecorder.Core.Ffmpeg;

namespace ScreenRecorder.Core.Recording;

public static class RecordArgsBuilder
{
    private const string EvenSize = "crop=trunc(iw/2)*2:trunc(ih/2)*2";

    /// <summary>Builds the FFmpeg arguments that record one segment (from start/resume until pause/stop).</summary>
    /// <param name="timestampOrigin">
    /// Unix time in seconds, taken just before FFmpeg starts. The segment's timestamps become "seconds since this
    /// moment", so the app can map its own clock (e.g. when Stop was pressed) onto the recording.
    /// </param>
    public static List<string> Build(RecordingOptions options, CaptureBackend backend, bool includeMic, string segmentPath,
        double? timestampOrigin = null)
    {
        if (options.Fps is < 1 or > 120) throw new ArgumentException("Frame rate must be between 1 and 120.", nameof(options));
        var region = ValidRegion(options);
        var cursor = options.DrawCursor ? "1" : "0";
        var fps = options.Fps.ToString(CultureInfo.InvariantCulture);

        // Both inputs are stamped with the wall clock (and -copyts keeps those stamps) so the microphone, which
        // starts delivering audio a fraction of a second after the screen, stays aligned with the picture.
        var args = new List<string> { "-y", "-hide_banner", "-use_wallclock_as_timestamps", "1", "-thread_queue_size", "1024" };
        string videoFilter;

        if (backend == CaptureBackend.Ddagrab)
        {
            var index = options.DdagrabOutputIndex
                        ?? throw new InvalidOperationException("This monitor cannot be captured with Desktop Duplication.");
            var source = $"ddagrab=output_idx={index}:framerate={fps}:draw_mouse={cursor}:dup_frames=1";
            if (region is { } r) source += $":offset_x={r.X}:offset_y={r.Y}:video_size={r.Width}x{r.Height}";
            args.AddRange(["-f", "lavfi", "-i", source]);
            // ddagrab produces GPU frames; copy them to system memory for the software H.264 encoder.
            videoFilter = $"hwdownload,format=bgra,{EvenSize},format=yuv420p";
        }
        else
        {
            var m = options.MonitorBounds;
            // Whole monitor: odd sizes are trimmed by the EvenSize filter below.
            var area = region is { } r ? new PixelRect(m.X + r.X, m.Y + r.Y, r.Width, r.Height) : m;
            args.AddRange(
            [
                "-f", "gdigrab", "-framerate", fps, "-draw_mouse", cursor,
                "-offset_x", I(area.X), "-offset_y", I(area.Y), "-video_size", $"{area.Width}x{area.Height}",
                "-i", "desktop",
            ]);
            videoFilter = $"{EvenSize},format=yuv420p";
        }

        var withMic = includeMic && !string.IsNullOrWhiteSpace(options.MicDevice);
        if (withMic)
        {
            args.AddRange(
            [
                "-use_wallclock_as_timestamps", "1", "-f", "dshow", "-audio_buffer_size", "50", "-thread_queue_size", "1024",
                "-i", $"audio={options.MicDevice}",
            ]);
        }

        args.AddRange(["-map", "0:v"]);
        if (withMic) args.AddRange(["-map", "1:a"]);
        args.Add("-copyts");
        if (timestampOrigin is { } origin) args.AddRange(["-output_ts_offset", FfmpegTime.Number(-origin)]);
        else args.AddRange(["-avoid_negative_ts", "make_zero"]);
        // ultrafast: capture must keep up in real time (edits are re-encoded at higher quality on export).
        // No B-frames: the first packet of every segment is then a clean cut point for joining.
        args.AddRange(["-vf", videoFilter, "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23", "-bf", "0", "-g", I(options.Fps * 2)]);
        // The first audio packet is placed by the wall clock; later timestamps follow the sample count. (Microphone
        // buffers arrive in bursts, and bursty wall-clock stamps overlap, which makes FFmpeg drop audio.)
        if (withMic) args.AddRange(["-c:a", "aac", "-b:a", "160k", "-af", "asetpts=N/SR/TB+STARTPTS"]);
        args.Add(segmentPath);
        return args;
    }

    private static PixelRect? ValidRegion(RecordingOptions options)
    {
        if (options.Region is not { } r) return null;
        var m = options.MonitorBounds;
        if (r.X < 0 || r.Y < 0 || r.Width < 16 || r.Height < 16 || r.X + r.Width > m.Width || r.Y + r.Height > m.Height)
            throw new ArgumentException("The recording region must be at least 16x16 pixels and inside the monitor.", nameof(options));
        return r.ToEven();
    }

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
}
