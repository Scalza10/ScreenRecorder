using System.Diagnostics;
using System.Text;
using ScreenRecorder.Core.Ffmpeg;
using ScreenRecorder.Core.Media;

namespace ScreenRecorder.Core.Recording;

public enum RecordingState { Idle, Recording, Paused, Stopped }

/// <summary>Produces the FFmpeg arguments for one recording segment. Replaceable so tests can fake the screen.</summary>
/// <param name="timestampOrigin">Unix time (seconds) just before FFmpeg starts; see <see cref="RecordArgsBuilder.Build"/>.</param>
public delegate IReadOnlyList<string> SegmentArgsFactory(CaptureBackend backend, bool includeMic, string segmentPath,
    double timestampOrigin);

/// <summary>
/// One recording from Start to Stop. Every Start/Resume records a new MKV segment (crash-safe: a segment stays
/// playable even if the app dies); Stop joins the segments into the final MP4 without re-encoding the video.
/// </summary>
public sealed class RecordingSession : IAsyncDisposable
{
    private static readonly TimeSpan StartupProbe = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly FfmpegPaths _ffmpeg;
    private readonly RecordingOptions _options;
    private readonly SegmentArgsFactory _argsFactory;
    private readonly List<Segment> _segments = [];
    private readonly Stopwatch _segmentClock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private FfmpegProcess? _current;
    private TimeSpan _completedDuration;

    public RecordingSession(FfmpegPaths ffmpeg, RecordingOptions options, string outputPath, SegmentArgsFactory? argsFactory = null)
    {
        _ffmpeg = ffmpeg;
        _options = options;
        OutputPath = outputPath;
        SegmentDirectory = outputPath + ".parts";
        _argsFactory = argsFactory ?? ((backend, mic, path, origin) => RecordArgsBuilder.Build(options, backend, mic, path, origin));
    }

    /// <summary>
    /// With a microphone, FFmpeg's audio path runs up to ~1.5 s behind the screen, and whatever is still in it when
    /// FFmpeg stops is lost. So capture continues this long after Stop/Pause; the join then cuts the segment off at
    /// the moment Stop/Pause was pressed.
    /// </summary>
    internal TimeSpan AudioDrainDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    public string OutputPath { get; }

    public string SegmentDirectory { get; }

    public RecordingState State { get; private set; } = RecordingState.Idle;

    public CaptureBackend Backend { get; private set; }

    public bool MicrophoneActive { get; private set; }

    /// <summary>Recorded time, excluding pauses.</summary>
    public TimeSpan Elapsed => _completedDuration + _segmentClock.Elapsed;

    /// <summary>Something degraded but recording continues (fallback capture, microphone missing).</summary>
    public event Action<string>? Warning;

    /// <summary>FFmpeg stopped on its own while recording. Segments recorded so far are kept; call StopAsync to save them.</summary>
    public event Action<FfmpegException>? Faulted;

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != RecordingState.Idle) throw new InvalidOperationException("The recording has already started.");
            Directory.CreateDirectory(SegmentDirectory);

            var backends = _options.DdagrabOutputIndex is null
                ? new[] { CaptureBackend.Gdigrab }
                : [CaptureBackend.Ddagrab, CaptureBackend.Gdigrab];
            var micChoices = string.IsNullOrWhiteSpace(_options.MicDevice) ? new[] { false } : [true, false];

            FfmpegResult? lastFailure = null;
            foreach (var mic in micChoices)
            {
                foreach (var backend in backends)
                {
                    lastFailure = await TryStartSegmentAsync(backend, mic);
                    if (lastFailure is not null) continue;

                    Backend = backend;
                    MicrophoneActive = mic;
                    State = RecordingState.Recording;
                    if (backend != backends[0])
                        Warning?.Invoke("Desktop Duplication capture is unavailable, so the slower GDI capture is being used.");
                    if (micChoices[0] && !mic)
                        Warning?.Invoke($"The microphone \"{_options.MicDevice}\" could not be opened, so this recording has no audio.");
                    return;
                }
            }

            TryDeleteDirectory();
            throw new FfmpegException("Screen capture could not be started.", lastFailure?.StderrText ?? "");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != RecordingState.Recording) return;
            await StopSegmentAsync();
            State = RecordingState.Paused;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != RecordingState.Paused) return;
            var failure = await TryStartSegmentAsync(Backend, MicrophoneActive);
            if (failure is not null) throw new FfmpegException("Recording could not be resumed.", failure.StderrText);
            State = RecordingState.Recording;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Finishes the recording and returns the path of the saved MP4.</summary>
    public async Task<string> StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State is RecordingState.Idle or RecordingState.Stopped)
                throw new InvalidOperationException("There is no recording in progress.");
            if (_current is not null) await StopSegmentAsync();

            var list = new StringBuilder();
            foreach (var segment in _segments.Where(s => File.Exists(s.Path) && new FileInfo(s.Path).Length > 1024))
            {
                if (await KeptSpanAsync(segment) is not { } span) continue;
                list.Append($"file '{Path.GetFileName(segment.Path)}'\n")
                    .Append($"inpoint {FfmpegTime.Format(span.Start)}\n")
                    .Append($"outpoint {FfmpegTime.Format(span.End)}\n");
            }

            if (list.Length == 0) throw new InvalidOperationException("Nothing was recorded.");
            var listPath = Path.Combine(SegmentDirectory, "segments.txt");
            await File.WriteAllTextAsync(listPath, list.ToString(), new UTF8Encoding(false));

            // Video is copied as-is. Audio is re-encoded so silence can fill a late microphone start and pause gaps.
            var args = new List<string> { "-y", "-hide_banner", "-f", "concat", "-safe", "0", "-i", listPath, "-map", "0:v", "-c:v", "copy" };
            if (MicrophoneActive)
                args.AddRange(["-map", "0:a", "-af", "aresample=async=1:first_pts=0", "-c:a", "aac", "-b:a", "160k"]);
            args.AddRange(["-movflags", "+faststart", OutputPath]);

            var result = await FfmpegProcess.RunAsync(_ffmpeg.Ffmpeg, args);
            result.EnsureSuccess("Saving the recording");

            State = RecordingState.Stopped;
            TryDeleteDirectory();
            return OutputPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The part of a segment to keep: from its first video frame to the earliest of the last video frame, the end of
    /// the audio, and the moment Stop/Pause was pressed. Audio keeps its offset relative to the video, so a microphone
    /// that started late becomes leading silence instead of shifting the sound.
    /// </summary>
    private async Task<TimeRange?> KeptSpanAsync(Segment segment)
    {
        if (await MediaProbe.GetStreamSpanAsync(_ffmpeg, segment.Path) is not { } video) return null;
        var end = video.End;

        if (MicrophoneActive && await MediaProbe.GetStreamSpanAsync(_ffmpeg, segment.Path, audio: true) is { } audio && audio.End < end)
            end = audio.End;

        if (segment.StopRequestedAt is { } stop && stop - segment.Origin < end)
            end = stop - segment.Origin;

        return end > video.Start ? new TimeRange(video.Start, end) : null;
    }

    /// <summary>Ends the recording without saving anything.</summary>
    public async Task DiscardAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_current is { } process)
            {
                _current = null;
                process.Kill();
                await process.Completion;
                process.Dispose();
            }

            _segmentClock.Reset();
            State = RecordingState.Stopped;
            TryDeleteDirectory();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts a segment; returns null on success or the failure if FFmpeg exits during start-up.</summary>
    private async Task<FfmpegResult?> TryStartSegmentAsync(CaptureBackend backend, bool includeMic)
    {
        var segment = new Segment(Path.Combine(SegmentDirectory, $"segment_{_segments.Count:D3}.mkv"), DateTime.UtcNow);
        var origin = (segment.Origin - DateTime.UnixEpoch).TotalSeconds;
        var process = FfmpegProcess.Start(_ffmpeg.Ffmpeg, _argsFactory(backend, includeMic, segment.Path, origin));
        _segmentClock.Restart();

        // Device errors (bad monitor, missing microphone, driver problems) make FFmpeg exit almost immediately.
        var first = await Task.WhenAny(process.Completion, Task.Delay(StartupProbe));
        if (first == process.Completion)
        {
            _segmentClock.Reset();
            var failure = await process.Completion;
            process.Dispose();
            TryDelete(segment.Path);
            return failure;
        }

        _segments.Add(segment);
        _current = process;
        _ = WatchForUnexpectedExitAsync(process);
        return null;
    }

    private async Task StopSegmentAsync()
    {
        var process = _current;
        _current = null;
        if (process is null) return;

        _segments[^1].StopRequestedAt = DateTime.UtcNow;
        _completedDuration += _segmentClock.Elapsed;
        _segmentClock.Reset();
        if (MicrophoneActive) await Task.WhenAny(process.Completion, Task.Delay(AudioDrainDelay));
        await process.StopGracefullyAsync(StopTimeout);
        process.Dispose();
    }

    private async Task WatchForUnexpectedExitAsync(FfmpegProcess process)
    {
        var result = await process.Completion;
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_current, process)) return; // stopped on purpose

            _current = null;
            _completedDuration += _segmentClock.Elapsed;
            _segmentClock.Reset();
            State = RecordingState.Paused;
            Faulted?.Invoke(new FfmpegException(
                $"Recording stopped unexpectedly (FFmpeg exit code {result.ExitCode}). What was recorded so far can still be saved.",
                result.StderrText));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryDeleteDirectory()
    {
        try
        {
            if (Directory.Exists(SegmentDirectory)) Directory.Delete(SegmentDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are harmless; never fail a finished recording because of them.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_current is { } process && State == RecordingState.Recording)
        {
            // App closing mid-recording: finish the segment cleanly so it stays playable in SegmentDirectory.
            _current = null;
            await process.StopGracefullyAsync(StopTimeout);
            process.Dispose();
        }

        _gate.Dispose();
    }

    private sealed class Segment(string path, DateTime origin)
    {
        public string Path { get; } = path;

        /// <summary>Wall-clock time the segment's timestamps are relative to.</summary>
        public DateTime Origin { get; } = origin;

        public DateTime? StopRequestedAt { get; set; }
    }
}
