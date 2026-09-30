using System.Text;
using ScreenRecorder.Core.Ffmpeg;
using ScreenRecorder.Core.Media;

namespace ScreenRecorder.Core.Recording;

/// <summary>One MKV file recorded between a start/resume and the following pause/stop.</summary>
internal sealed class RecordedSegment(string path, DateTime? origin)
{
    public string Path { get; } = path;

    /// <summary>Wall-clock time the segment's timestamps are relative to (unknown for recovered segments).</summary>
    public DateTime? Origin { get; } = origin;

    public DateTime? StopRequestedAt { get; set; }
}

/// <summary>Joins recorded segments into the final MP4 without re-encoding the video.</summary>
internal static class SegmentJoiner
{
    /// <summary>
    /// Audio ending at most this long before the video is just FFmpeg's in-flight audio being dropped at stop (see
    /// <see cref="RecordingSession.AudioDrainDelay"/>), so the segment is cut where the audio ends. A bigger gap means
    /// the microphone stopped mid-recording; then the video is kept and the rest is silent.
    /// </summary>
    private static readonly TimeSpan AudioLagTolerance = TimeSpan.FromSeconds(2);

    public static async Task JoinAsync(FfmpegPaths ffmpeg, IReadOnlyList<RecordedSegment> segments, bool withAudio,
        string workDirectory, string output, Action<string>? warn)
    {
        var list = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (!File.Exists(segment.Path) || new FileInfo(segment.Path).Length <= 1024) continue;

            TimeRange? span;
            try
            {
                span = await KeptSpanAsync(ffmpeg, segment, withAudio, warn);
            }
            catch (FfmpegException)
            {
                warn?.Invoke($"Part {i + 1} of the recording is damaged and was skipped.");
                continue;
            }

            if (span is not { } kept) continue;
            list.Append($"file '{Path.GetFileName(segment.Path)}'\n")
                .Append($"inpoint {FfmpegTime.Format(kept.Start)}\n")
                .Append($"outpoint {FfmpegTime.Format(kept.End)}\n");
        }

        if (list.Length == 0) throw new InvalidOperationException("Nothing was recorded.");
        var listPath = Path.Combine(workDirectory, "segments.txt");
        await File.WriteAllTextAsync(listPath, list.ToString(), new UTF8Encoding(false));

        // Video is copied as-is. Audio is re-encoded so silence can fill a late microphone start and pause gaps.
        var args = new List<string> { "-y", "-hide_banner", "-f", "concat", "-safe", "0", "-i", listPath, "-map", "0:v", "-c:v", "copy" };
        if (withAudio) args.AddRange(["-map", "0:a", "-af", "aresample=async=1:first_pts=0", "-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(["-movflags", "+faststart", output]);

        var result = await FfmpegProcess.RunAsync(ffmpeg.Ffmpeg, args);
        result.EnsureSuccess("Saving the recording");
    }

    /// <summary>
    /// The part of a segment to keep: from its first video frame to the earliest of the last video frame, the end of
    /// the audio (if that is just in-flight audio lost at stop), and the moment Stop/Pause was pressed. Audio keeps its
    /// offset relative to the video, so a microphone that started late becomes leading silence.
    /// </summary>
    private static async Task<TimeRange?> KeptSpanAsync(FfmpegPaths ffmpeg, RecordedSegment segment, bool withAudio,
        Action<string>? warn)
    {
        if (await MediaProbe.GetStreamSpanAsync(ffmpeg, segment.Path) is not { } video) return null;
        var end = video.End;

        if (withAudio && await MediaProbe.GetStreamSpanAsync(ffmpeg, segment.Path, audio: true) is { } audio && audio.End < end)
        {
            if (end - audio.End <= AudioLagTolerance) end = audio.End;
            else warn?.Invoke("The microphone stopped delivering sound part-way through, so part of the recording is silent.");
        }

        if (segment is { Origin: { } origin, StopRequestedAt: { } stop } && stop - origin < end) end = stop - origin;

        return end > video.Start ? new TimeRange(video.Start, end) : null;
    }
}
