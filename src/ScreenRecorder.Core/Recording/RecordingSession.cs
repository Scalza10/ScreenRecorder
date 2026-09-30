using System.Diagnostics;
using ScreenRecorder.Core.Ffmpeg;
using ScreenRecorder.Core.Media;

namespace ScreenRecorder.Core.Recording;

public enum RecordingState { Idle, Recording, Paused, Stopped }

/// <summary>Produces the FFmpeg arguments for one recording segment. Replaceable so tests can fake the screen.</summary>
/// <param name="timestampOrigin">Unix time (seconds) just before FFmpeg starts; see <see cref="RecordArgsBuilder.Build"/>.</param>
public delegate IReadOnlyList<string> SegmentArgsFactory(CaptureBackend backend, AudioSource audio, string segmentPath,
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
    private readonly List<RecordedSegment> _segments = [];
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
        _argsFactory = argsFactory ?? ((backend, audio, path, origin) => RecordArgsBuilder.Build(options, backend, audio, path, origin));
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
            var audioChoices = string.IsNullOrWhiteSpace(_options.MicDevice)
                ? new[] { AudioSource.None }
                : [AudioSource.Microphone, AudioSource.None];

            FfmpegResult? lastFailure = null;
            foreach (var audio in audioChoices)
            {
                foreach (var backend in backends)
                {
                    lastFailure = await TryStartSegmentAsync(backend, audio);
                    if (lastFailure is not null) continue;

                    Backend = backend;
                    MicrophoneActive = audio == AudioSource.Microphone;
                    State = RecordingState.Recording;
                    if (backend != backends[0])
                        Warning?.Invoke("Desktop Duplication capture is unavailable, so the slower GDI capture is being used.");
                    if (audioChoices[0] == AudioSource.Microphone && !MicrophoneActive)
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
            var failure = await TryStartSegmentAsync(Backend, MicrophoneActive ? AudioSource.Microphone : AudioSource.None);
            if (failure is not null && MicrophoneActive && await TryStartSegmentAsync(Backend, AudioSource.Silence) is null)
            {
                // Keep going without sound rather than refusing to resume (the segment gets a silent track).
                failure = null;
                Warning?.Invoke($"The microphone \"{_options.MicDevice}\" stopped working, so the recording continues without sound.");
            }

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

            // On failure the state and segments are kept, so the caller can simply try StopAsync again.
            await SegmentJoiner.JoinAsync(_ffmpeg, _segments, MicrophoneActive, SegmentDirectory, OutputPath, w => Warning?.Invoke(w));

            State = RecordingState.Stopped;
            TryDeleteDirectory();
            return OutputPath;
        }
        finally
        {
            _gate.Release();
        }
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
    private async Task<FfmpegResult?> TryStartSegmentAsync(CaptureBackend backend, AudioSource audio)
    {
        var segment = new RecordedSegment(Path.Combine(SegmentDirectory, $"segment_{_segments.Count:D3}.mkv"), DateTime.UtcNow);
        var origin = (segment.Origin!.Value - DateTime.UnixEpoch).TotalSeconds;
        var process = FfmpegProcess.Start(_ffmpeg.Ffmpeg, _argsFactory(backend, audio, segment.Path, origin));
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
}
