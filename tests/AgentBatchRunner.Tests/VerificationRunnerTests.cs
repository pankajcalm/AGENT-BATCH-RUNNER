using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Services;
using System.Runtime.InteropServices;

namespace AgentBatchRunner.Tests;

public sealed class VerificationRunnerTests
{
    private static string OutputThenSleepCommand(int seconds)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"[Console]::Out.WriteLine('verify-before-timeout'); [Console]::Out.Flush(); [Console]::Error.WriteLine('verify-stderr-before-timeout'); [Console]::Error.Flush(); Start-Sleep -Seconds {seconds}"
            : $"echo verify-before-timeout; echo verify-stderr-before-timeout 1>&2; sleep {seconds}";
    }

    [Fact]
    public async Task RunAsync_ReturnsSuccessWhenAllCommandsPass()
    {
        using var temp = TestWorkspace.Create();
        var runner = new VerificationRunner(new ProcessRunner(), new ConsoleLogger());

        var result = await runner.RunAsync(["dotnet --version"], temp.Root, temp.Root);

        Assert.True(result.Succeeded);
        Assert.Single(result.Commands);
        Assert.Equal(0, result.Commands[0].ExitCode);
        Assert.True(File.Exists(result.LogPath));
    }

    [Fact]
    public async Task RunAsync_StopsAtFirstFailure()
    {
        using var temp = TestWorkspace.Create();
        var runner = new VerificationRunner(new ProcessRunner(), new ConsoleLogger());

        var result = await runner.RunAsync(["exit 7", "dotnet --version"], temp.Root, temp.Root);

        Assert.False(result.Succeeded);
        Assert.Single(result.Commands);
        Assert.Equal(7, result.Commands[0].ExitCode);
        Assert.Equal("exit 7", result.FailedCommand);
    }

    [Fact]
    public async Task RunAsync_EmptyCommandList_IsUnverifiedNotASilentPass()
    {
        using var temp = TestWorkspace.Create();
        var runner = new VerificationRunner(new ProcessRunner(), new ConsoleLogger());

        var result = await runner.RunAsync([], temp.Root, temp.Root);

        Assert.False(result.Succeeded);
        Assert.True(result.Unverified);
        Assert.Empty(result.Commands);
        Assert.True(File.Exists(result.LogPath));
    }

    [Fact]
    public async Task RunAsync_CapturesCommandMetadataInLog()
    {
        using var temp = TestWorkspace.Create();
        var runner = new VerificationRunner(new ProcessRunner(), new ConsoleLogger());

        var result = await runner.RunAsync(["exit 3"], temp.Root, temp.Root);

        var command = Assert.Single(result.Commands);
        Assert.Equal("exit 3", command.Command);
        Assert.Equal(3, command.ExitCode);
        var log = await File.ReadAllTextAsync(result.LogPath);
        Assert.Contains("$ exit 3", log);
        Assert.Contains("Exit code: 3", log);
    }

    [Fact]
    public async Task RunAsync_TimeoutStopsRemainingVerificationCommands()
    {
        using var temp = TestWorkspace.Create();
        var runner = new VerificationRunner(new ProcessRunner(), new ConsoleLogger());

        var result = await runner.RunAsync(
            [OutputThenSleepCommand(30), "exit 0"],
            temp.Root,
            temp.Root,
            CancellationToken.None,
            TimeSpan.FromSeconds(3));

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.Single(result.Commands);

        var command = result.Commands[0];
        Assert.True(command.TimedOut);
        Assert.Equal(124, command.ExitCode);
        Assert.Equal(TimeSpan.FromSeconds(3), command.Timeout);
        Assert.Contains("verify-before-timeout", command.StandardOutput);
        Assert.Contains("verify-stderr-before-timeout", command.StandardError);
        Assert.Contains("timed out", command.StandardError, StringComparison.OrdinalIgnoreCase);

        var log = await File.ReadAllTextAsync(result.LogPath);
        Assert.Contains("Timed out: True", log);
        Assert.Contains("Timeout: 3s", log);
    }
}
