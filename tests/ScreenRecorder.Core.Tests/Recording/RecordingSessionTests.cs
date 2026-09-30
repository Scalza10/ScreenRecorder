using ScreenRecorder.Core.Ffmpeg;
using ScreenRecorder.Core.Media;
using ScreenRecorder.Core.Recording;
using ScreenRecorder.Core.Tests.Infrastructure;

namespace ScreenRecorder.Core.Tests.Recording;

public class RecordingSessionTests
{
    private static readonly RecordingOptions Options =
        new(new PixelRect(0, 0, 320, 240)) { DdagrabOutputIndex = 0, MicDevice = "Test Mic" };

    /// <summary>
    /// Stands in for the screen: an endless real-time test pattern (+ tone when the mic is on). Note that a -re lavfi
    /// source runs up to ~0.6 s ahead of the wall clock per segment, so durations are compared with a tolerance.
    /// </summary>
    private static List<string> FakeCapture(string segmentPath, bool withAudio)
    {
        var args = new List<string> { "-y", "-hide_banner", "-re", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30" };
        if (withAudio) args.AddRange(["-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"]);
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        if (withAudio) args.AddRange(["-c:a", "aac"]);
        args.Add(segmentPath);
        return args;
    }

    private static readonly List<string> Broken = ["-hide_banner", "-f", "lavfi", "-i", "nosuchsource", "x.mkv"];

    private static string NewOutput() => Path.Combine(TestMedia.NewTempDir(), "Recording.mp4");

    [Fact]
    public async Task Pause_and_resume_produce_one_mp4_without_the_paused_gap()
    {
        var output = NewOutput();
        await using var session = new RecordingSession(TestMedia.Ffmpeg, Options, output,
            (backend, mic, path, _) => FakeCapture(path, mic));

        await session.StartAsync();
        Assert.Equal(RecordingState.Recording, session.State);
        await Task.Delay(2000);
        await session.PauseAsync();
        Assert.Equal(RecordingState.Paused, session.State);
        var pausedAt = session.Elapsed;
        await Task.Delay(3000);
        Assert.Equal(pausedAt, session.Elapsed);
        await session.ResumeAsync();
        await Task.Delay(2000);
        var final = await session.StopAsync();

        Assert.Equal(output, final);
        Assert.Equal(RecordingState.Stopped, session.State);
        var info = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, final);
        // Including the 3 s pause would push the file well past Elapsed + 3 s.
        var elapsed = session.Elapsed.TotalSeconds;
        Assert.InRange(elapsed, 6.5, 8.5); // 2 x (1.5 s start-up check + 2 s)
        Assert.InRange(info.Duration.TotalSeconds, elapsed - 0.5, elapsed + 1.5);
        Assert.True(info.HasAudio);
        Assert.False(Directory.Exists(session.SegmentDirectory));
    }

    [Fact]
    public async Task Keeps_late_microphone_audio_in_sync_across_segments()
    {
        // Real microphones start delivering audio ~0.5-0.7 s after the screen. Simulate audio that begins 0.7 s late.
        static List<string> LateAudio(string path) =>
        [
            "-y", "-hide_banner", "-re", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30",
            "-itsoffset", "0.7", "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-c:v", "libx264", "-preset", "ultrafast", "-bf", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", path,
        ];
        var output = NewOutput();
        await using var session = new RecordingSession(TestMedia.Ffmpeg, Options, output, (_, _, path, _) => LateAudio(path));

        await session.StartAsync();
        await Task.Delay(1000);
        await session.PauseAsync();
        await session.ResumeAsync();
        await Task.Delay(1000);
        var final = await session.StopAsync();

        var silences = await DetectSilenceAsync(final);
        Assert.True(silences.Count >= 2, "expected leading silence and a silent gap at the segment join");
        Assert.InRange(silences[0].Start, 0.0, 0.05);
        Assert.InRange(silences[0].End, 0.6, 0.8);
    }

    [Fact]
    public async Task Cuts_each_segment_where_both_audio_and_video_exist()
    {
        // FFmpeg discards audio still in its pipeline when it stops, so audio can end before the video.
        // Simulate audio that starts 0.5 s late and lasts only 1 s.
        static List<string> ShortAudio(string path) =>
        [
            "-y", "-hide_banner", "-re", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30",
            "-itsoffset", "0.5", "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
            "-c:v", "libx264", "-preset", "ultrafast", "-bf", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", path,
        ];
        await using var session = new RecordingSession(TestMedia.Ffmpeg, Options, NewOutput(), (_, _, path, _) => ShortAudio(path));

        await session.StartAsync();
        await Task.Delay(1500);
        var final = await session.StopAsync();

        var info = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, final);
        Assert.InRange(info.Duration.TotalSeconds, 1.3, 1.7); // audio end: 0.5 s offset + 1 s
    }

    private static async Task<List<(double Start, double End)>> DetectSilenceAsync(string path)
    {
        var lines = new List<string>();
        await Core.Ffmpeg.FfmpegProcess.RunAsync(TestMedia.Ffmpeg.Ffmpeg,
            ["-hide_banner", "-i", path, "-map", "0:a", "-af", "silencedetect=noise=-50dB:d=0.2", "-f", "null", "-"],
            onStderrLine: l => { lock (lines) lines.Add(l); });
        var result = new List<(double, double)>();
        double? start = null;
        foreach (var line in lines)
        {
            var s = System.Text.RegularExpressions.Regex.Match(line, @"silence_start: (-?[\d.]+)");
            var e = System.Text.RegularExpressions.Regex.Match(line, @"silence_end: (-?[\d.]+)");
            if (s.Success) start = double.Parse(s.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (e.Success && start is { } st)
                result.Add((st, double.Parse(e.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        return result;
    }

    [Fact]
    public async Task Discard_deletes_everything_recorded()
    {
        var output = NewOutput();
        await using var session = new RecordingSession(TestMedia.Ffmpeg, Options, output,
            (backend, mic, path, _) => FakeCapture(path, mic));

        await session.StartAsync();
        await Task.Delay(500);
        await session.DiscardAsync();

        Assert.Equal(RecordingState.Stopped, session.State);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(session.SegmentDirectory));
    }

    [Fact]
    public async Task Stop_while_paused_finishes_the_recording()
    {
        var output = NewOutput();
        await using var session = new RecordingSession(TestMedia.Ffmpeg, Options, output,
            (backend, mic, path, _) => FakeCapture(path, mic));

        await session.StartAsync();
        await Task.Delay(2000);
        await session.PauseAsync();
        var final = await session.StopAsync();

        var info = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, final);
        var elapsed = session.Elapsed.TotalSeconds;
        Assert.InRange(info.Duration.TotalSeconds, elapsed - 0.5, elapsed + 1.0);
    }

    [Fact]
    public async Task Falls_back_to_gdigrab_when_ddagrab_fails()
    {
        var warnings = new List<string>();
        await using var session = new RecordingSession(TestMedia.Ffmpeg, Options, NewOutput(),
            (backend, mic, path, _) => backend == CaptureBackend.Ddagrab ? Broken : FakeCapture(path, mic));
        session.Warning += warnings.Add;

        await session.StartAsync();
        await Task.Delay(1000);
        await session.StopAsync();

        Assert.Equal(CaptureBackend.Gdigrab, session.Backend);
        Assert.True(session.MicrophoneActive);
        Assert.Contains(warnings, w => w.Contains("GDI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Continues_without_microphone_when_it_cannot_be_opened()
    {
        var warnings = new List<string>();
        await using var session = new RecordingSession(TestMedia.Ffmpeg, Options, NewOutput(),
            (backend, mic, path, _) => mic ? Broken : FakeCapture(path, withAudio: false));
        session.Warning += warnings.Add;

        await session.StartAsync();
        await Task.Delay(1000);
        var final = await session.StopAsync();

        Assert.Equal(CaptureBackend.Ddagrab, session.Backend);
        Assert.False(session.MicrophoneActive);
        Assert.Contains(warnings, w => w.Contains("microphone", StringComparison.OrdinalIgnoreCase));
        Assert.False((await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, final)).HasAudio);
    }

    [Fact]
    public async Task Start_throws_when_every_capture_method_fails()
    {
        await using var session = new RecordingSession(TestMedia.Ffmpeg, Options, NewOutput(), (_, _, _, _) => Broken);

        await Assert.ThrowsAsync<FfmpegException>(session.StartAsync);
        Assert.Equal(RecordingState.Idle, session.State);
    }

    [Fact]
    public async Task Records_the_real_screen()
    {
        if (Environment.GetEnvironmentVariable("SCREENRECORDER_SKIP_SCREEN") == "1") return; // headless CI

        var output = NewOutput();
        var options = new RecordingOptions(new PixelRect(0, 0, 640, 480))
            { DdagrabOutputIndex = 0, Region = new PixelRect(0, 0, 640, 480) };
        await using var session = new RecordingSession(TestMedia.Ffmpeg, options, output);

        await session.StartAsync();
        await Task.Delay(2000);
        var final = await session.StopAsync();

        var info = await MediaProbe.ProbeAsync(TestMedia.Ffmpeg, final);
        Assert.Equal((640, 480), (info.Width, info.Height));
        var elapsed = session.Elapsed.TotalSeconds;
        Assert.InRange(elapsed, 3.0, 4.5); // 1.5 s start-up check + 2 s
        // The file starts at the first captured frame (GDI capture can take ~1 s to deliver it) and ends when Stop
        // was pressed, so it is never longer than the time spent recording.
        Assert.InRange(info.Duration.TotalSeconds, 1.5, elapsed + 0.2);
    }
}
