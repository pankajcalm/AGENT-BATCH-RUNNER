using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class AgentPreflightServiceTests
{
    [Theory]
    [InlineData("codex-cli 0.57.0", false)]
    [InlineData("codex-cli 0.144.5", true)]
    [InlineData("codex-cli 0.150.0", true)]
    public async Task RunAsync_EnforcesConfiguredMinimumCodexVersion(string output, bool expectedSuccess)
    {
        using var temp = TestWorkspace.Create();
        var executablePath = Path.Combine(temp.Root, "OpenAI Codex", "codex.exe");
        var processRunner = new StubProcessRunner(ProcessResultFor(output));
        var service = new AgentPreflightService(processRunner, new StubResolver(executablePath));
        var config = new BatchConfig { MinimumCodexVersion = "0.144.5" };

        var result = await service.RunAsync(config, ["codex"], temp.Root, CancellationToken.None);

        Assert.Equal(expectedSuccess, result.Succeeded);
        Assert.Equal(executablePath, Assert.Single(processRunner.Calls).FileName);
        Assert.Equal(["--version"], processRunner.Calls[0].Arguments);
        if (!expectedSuccess)
        {
            Assert.Contains("minimum required is 0.144.5", result.FailureReason);
            Assert.Contains("0.57.0", result.FailureReason);
        }
    }

    [Theory]
    [InlineData("Please install WSL (Windows Subsystem for Linux) to run Codex.", "WSL")]
    [InlineData("Codex version unknown", "parse")]
    public async Task RunAsync_RejectsGuidanceOnlyOrUnparseableVersionOutput(
        string output,
        string expectedReasonFragment)
    {
        using var temp = TestWorkspace.Create();
        var service = new AgentPreflightService(
            new StubProcessRunner(ProcessResultFor(output)),
            new StubResolver(Path.Combine(temp.Root, "codex.exe")));

        var result = await service.RunAsync(
            new BatchConfig(),
            ["codex"],
            temp.Root,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(expectedReasonFragment, result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentPreflightStatus.Failed, Assert.Single(result.Toolchains).Status);
    }

    [Fact]
    public async Task RunAsync_PathContainingSpacesIsPassedAsOneExecutableValue()
    {
        using var temp = TestWorkspace.Create();
        var executablePath = Path.Combine(temp.Root, "Programs With Spaces", "claude.exe");
        var processRunner = new StubProcessRunner(ProcessResultFor("2.4.1 (Claude Code)"));
        var service = new AgentPreflightService(processRunner, new StubResolver(executablePath));

        var result = await service.RunAsync(
            new BatchConfig(),
            ["claude"],
            temp.Root,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(executablePath, Assert.Single(processRunner.Calls).FileName);
        Assert.Equal("2.4.1", Assert.Single(result.Toolchains).Version);
    }

    private static ProcessResult ProcessResultFor(string output)
    {
        return new ProcessResult
        {
            Command = "agent --version",
            ExitCode = 0,
            StandardOutput = output
        };
    }

    private sealed class StubResolver(string executablePath) : IAgentExecutableResolver
    {
        public AgentExecutableResolution Resolve(string agentName, BatchConfig config)
        {
            return AgentExecutableResolution.Succeeded(agentName, executablePath, "test");
        }
    }

    private sealed class StubProcessRunner(ProcessResult result) : ProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public override Task<ProcessResult> RunExecutableAsync(
            string fileName,
            IEnumerable<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken = default,
            string? displayCommand = null,
            TimeSpan? timeout = null)
        {
            Calls.Add(new ProcessCall(fileName, arguments.ToList(), workingDirectory));
            return Task.FromResult(result);
        }
    }

    private sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);
}
