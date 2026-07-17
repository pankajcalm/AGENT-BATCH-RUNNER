using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentBatchRunner.Infrastructure;

public class ProcessRunner
{
    public Task<ProcessResult> RunShellCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RunExecutableAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command],
                workingDirectory,
                cancellationToken,
                displayCommand: command,
                timeout: timeout);
        }

        return RunExecutableAsync(
            "/bin/sh",
            ["-lc", command],
            workingDirectory,
            cancellationToken,
            displayCommand: command,
            timeout: timeout);
    }

    public virtual async Task<ProcessResult> RunExecutableAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken = default,
        string? displayCommand = null,
        TimeSpan? timeout = null)
    {
        var argumentList = arguments.ToList();
        var command = displayCommand ?? BuildDisplayCommand(fileName, argumentList);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;

        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var hasTimeout = timeout.HasValue && timeout.Value > TimeSpan.Zero;
        using var timeoutCts = hasTimeout
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        timeoutCts?.CancelAfter(timeout!.Value);
        var effectiveToken = timeoutCts?.Token ?? cancellationToken;

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutClosed.TrySetResult();
            }
            else
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrClosed.TrySetResult();
            }
            else
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return ProcessResult.Failed(command, -1, "Process did not start.", stopwatch.Elapsed);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(effectiveToken);
            await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task);
            stopwatch.Stop();

            return new ProcessResult
            {
                Command = SensitiveDataRedactor.Redact(command),
                ExitCode = process.ExitCode,
                Duration = stopwatch.Elapsed,
                StandardOutput = SensitiveDataRedactor.Redact(stdout.ToString()),
                StandardError = SensitiveDataRedactor.Redact(stderr.ToString())
            };
        }
        catch (OperationCanceledException)
            when (hasTimeout && timeoutCts!.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await WaitForExitAndFlushReadersBestEffortAsync(process, stdoutClosed.Task, stderrClosed.Task);
            stopwatch.Stop();
            return ProcessResult.TimedOutResult(
                command,
                stdout.ToString(),
                stderr.ToString(),
                stopwatch.Elapsed,
                timeout!.Value);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProcessResult.Failed(command, -1, ex.Message, stopwatch.Elapsed);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static async Task WaitForExitAndFlushReadersBestEffortAsync(
        Process process,
        Task stdoutClosed,
        Task stderrClosed)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort cleanup only.
        }

        try
        {
            await Task.WhenAll(stdoutClosed, stderrClosed).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort capture of partial output only.
        }
    }

    private static string BuildDisplayCommand(string fileName, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return fileName;
        }

        return fileName + " " + string.Join(" ", arguments.Select(QuoteForDisplay));
    }

    private static string QuoteForDisplay(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"', StringComparison.Ordinal))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
