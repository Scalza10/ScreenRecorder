using System.Diagnostics;

namespace ScreenRecorder.Core.Ffmpeg;

public sealed record FfmpegResult(int ExitCode, IReadOnlyList<string> StderrTail)
{
    public string StderrText => string.Join(Environment.NewLine, StderrTail);

    public FfmpegResult EnsureSuccess(string operation)
    {
        if (ExitCode != 0) throw new FfmpegException($"{operation} failed (FFmpeg exit code {ExitCode}).", StderrText);
        return this;
    }
}

public sealed class FfmpegException(string message, string details) : Exception(message)
{
    /// <summary>The last lines FFmpeg wrote to stderr; shown to the user when something goes wrong.</summary>
    public string Details { get; } = details;
}

/// <summary>A running ffmpeg/ffprobe child process with stderr capture, progress parsing and graceful stop.</summary>
public sealed class FfmpegProcess : IDisposable
{
    private const int TailLines = 20;

    private readonly Process _process;
    private readonly Queue<string> _tail = new();
    private readonly TaskCompletionSource<FfmpegResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<TimeSpan>? _onProgress;
    private readonly Action<string>? _onStderrLine;
    private readonly System.Text.StringBuilder _stdout = new();

    private FfmpegProcess(Process process, Action<TimeSpan>? onProgress, Action<string>? onStderrLine)
    {
        _process = process;
        _onProgress = onProgress;
        _onStderrLine = onStderrLine;
    }

    public Task<FfmpegResult> Completion => _completion.Task;

    public bool HasExited => _completion.Task.IsCompleted;

    public static FfmpegProcess Start(string executable, IEnumerable<string> arguments, Action<TimeSpan>? onProgress = null,
        Action<string>? onStderrLine = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var wrapper = new FfmpegProcess(process, onProgress, onStderrLine);

        process.ErrorDataReceived += (_, e) => wrapper.OnStderr(e.Data);
        process.OutputDataReceived += (_, e) => wrapper.OnStdout(e.Data);

        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        _ = wrapper.WaitForExitAsync();
        return wrapper;
    }

    /// <summary>Runs to completion. Never throws on a non-zero exit; call <see cref="FfmpegResult.EnsureSuccess"/>.</summary>
    public static async Task<FfmpegResult> RunAsync(string executable, IEnumerable<string> arguments,
        Action<TimeSpan>? onProgress = null, CancellationToken cancellationToken = default, Action<string>? onStderrLine = null)
    {
        using var process = Start(executable, arguments, onProgress, onStderrLine);
        await using var registration = cancellationToken.Register(process.Kill);
        var result = await process.Completion.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Runs to completion and returns everything written to stdout (used for ffprobe JSON).</summary>
    public static async Task<(FfmpegResult Result, string Stdout)> RunWithOutputAsync(string executable,
        IEnumerable<string> arguments, CancellationToken cancellationToken = default)
    {
        using var process = Start(executable, arguments);
        await using var registration = cancellationToken.Register(process.Kill);
        var result = await process.Completion.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (process._stdout) return (result, process._stdout.ToString());
    }

    /// <summary>
    /// Asks FFmpeg to finish writing the file (the same as pressing 'q' in its console) and waits for it to exit.
    /// Kills it if it does not stop within <paramref name="timeout"/>.
    /// </summary>
    public async Task<FfmpegResult> StopGracefullyAsync(TimeSpan timeout)
    {
        if (!HasExited)
        {
            try
            {
                await _process.StandardInput.WriteAsync('q').ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                // Process already exiting; fall through to the wait.
            }

            var finished = await Task.WhenAny(Completion, Task.Delay(timeout)).ConfigureAwait(false);
            if (finished != Completion) Kill();
        }

        return await Completion.ConfigureAwait(false);
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private void OnStdout(string? line)
    {
        if (line is null) return;
        lock (_stdout) _stdout.AppendLine(line);
    }

    private void OnStderr(string? line)
    {
        if (line is null) return;
        _onStderrLine?.Invoke(line);
        if (FfmpegTime.TryParseProgress(line, out var time))
        {
            _onProgress?.Invoke(time);
            return; // progress lines are noise in an error report
        }

        lock (_tail)
        {
            _tail.Enqueue(line);
            while (_tail.Count > TailLines) _tail.Dequeue();
        }
    }

    private async Task WaitForExitAsync()
    {
        // WaitForExitAsync also waits for the redirected streams to be drained.
        await _process.WaitForExitAsync().ConfigureAwait(false);
        string[] tail;
        lock (_tail) tail = _tail.ToArray();
        _completion.TrySetResult(new FfmpegResult(_process.ExitCode, tail));
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}
