using System.Diagnostics;
using System.Runtime.InteropServices;
using AgentBatchRunner.Infrastructure;

namespace AgentBatchRunner.Tests;

public sealed class ProcessRunnerTests
{
    private static string SleepCommand(int seconds)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"Start-Sleep -Seconds {seconds}"
            : $"sleep {seconds}";
    }

    private static string OutputThenSleepCommand(int seconds)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"[Console]::Out.WriteLine('before-timeout'); [Console]::Out.Flush(); [Console]::Error.WriteLine('stderr-before-timeout'); [Console]::Error.Flush(); Start-Sleep -Seconds {seconds}"
            : $"echo before-timeout; echo stderr-before-timeout 1>&2; sleep {seconds}";
    }

    [Fact]
    public async Task RunShellCommand_KillsAndReportsTimeout_WhenCommandExceedsLimit()
    {
        using var temp = TestWorkspace.Create();
        var runner = new ProcessRunner();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunShellCommandAsync(
            OutputThenSleepCommand(30),
            temp.Root,
            CancellationToken.None,
            TimeSpan.FromSeconds(3));

        stopwatch.Stop();
        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.Equal(124, result.ExitCode);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Timeout);
        Assert.Contains("before-timeout", result.StandardOutput);
        Assert.Contains("stderr-before-timeout", result.StandardError);
        Assert.Contains("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);
        // It must terminate promptly, not wait out the full 30s sleep.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunShellCommand_CompletesNormally_WhenWithinTimeout()
    {
        using var temp = TestWorkspace.Create();
        var runner = new ProcessRunner();

        var result = await runner.RunShellCommandAsync(
            SleepCommand(0),
            temp.Root,
            CancellationToken.None,
            TimeSpan.FromSeconds(30));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunShellCommand_ExternalCancellation_StillThrows()
    {
        using var temp = TestWorkspace.Create();
        var runner = new ProcessRunner();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunShellCommandAsync(SleepCommand(30), temp.Root, cts.Token));
    }

    [Fact]
    public async Task RunShellCommand_CapturesUtf8StdoutAndStderrWithoutMojibake()
    {
        using var temp = TestWorkspace.Create();
        var expected = "Product charter \u2014 10\u201320 teams \u201cquoted text\u201d";
        var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); Write-Output '{expected}'; [Console]::Error.WriteLine('{expected}')"
            : $"printf '%s\\n' '{expected}'; printf '%s\\n' '{expected}' 1>&2";

        var result = await new ProcessRunner().RunShellCommandAsync(command, temp.Root);

        Assert.True(result.Succeeded, result.CombinedOutput);
        Assert.Contains(expected, result.StandardOutput);
        Assert.Contains(expected, result.StandardError);
        Assert.DoesNotContain("\u00c3", result.CombinedOutput);
    }
}
