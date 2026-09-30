using ScreenRecorder.Core.Editing;
using ScreenRecorder.Core.Ffmpeg;
using ScreenRecorder.Core.Media;

namespace ScreenRecorder.Core.Recording;

/// <summary>
/// Recordings are written as segments in "&lt;name&gt;.mp4.parts" and only joined on Stop. If the app crashed or saving
/// failed, the segments are still there; this turns them back into an MP4.
/// </summary>
public static class RecordingRecovery
{
    public static IReadOnlyList<string> FindUnsaved(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetDirectories(folder, "*.mp4.parts")
                .Where(d => Directory.EnumerateFiles(d, "segment_*.mkv").Any())
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    /// <summary>Joins the segments in <paramref name="segmentDirectory"/> and returns the recovered MP4's path.</summary>
    public static async Task<string> RecoverAsync(FfmpegPaths ffmpeg, string segmentDirectory, Action<string>? warn = null)
    {
        var output = OutputNaming.Unique(segmentDirectory[..^".parts".Length]);
        var segments = Directory.GetFiles(segmentDirectory, "segment_*.mkv")
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new RecordedSegment(path, origin: null))
            .ToList();

        var withAudio = false;
        foreach (var segment in segments)
        {
            try
            {
                withAudio = await MediaProbe.GetStreamSpanAsync(ffmpeg, segment.Path, audio: true) is not null;
                if (withAudio) break;
            }
            catch (FfmpegException)
            {
                // A damaged segment; the join skips it.
            }
        }

        await SegmentJoiner.JoinAsync(ffmpeg, segments, withAudio, segmentDirectory, output, warn);
        Directory.Delete(segmentDirectory, recursive: true);
        return output;
    }
}
