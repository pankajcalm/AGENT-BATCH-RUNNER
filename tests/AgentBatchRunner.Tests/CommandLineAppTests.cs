using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class CommandLineAppTests
{
    [Fact]
    public async Task LimitsCommand_PrintsAgentAvailability()
    {
        using var workspace = TestWorkspace.Create();
        var rateLimitStore = new AgentRateLimitStateStore(
            Path.Combine(workspace.Root, "agent-rate-limits.json"),
            () => DateTimeOffset.Parse("2026-06-26T12:00:00Z"));
        rateLimitStore.SetBlocked(new AgentRateLimitInfo
        {
            AgentName = "claude",
            IsBlocked = true,
            BlockedUntil = DateTimeOffset.Parse("2026-06-26T13:00:00Z"),
            LastDetectedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            Reason = "Usage limit reached.",
            RawMessage = "Usage limit reached."
        });
        var app = CreateApp(rateLimitStore);
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            var exitCode = await app.RunAsync(["limits"], CancellationToken.None);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        Assert.Contains("claude: Blocked until", output);
        Assert.Contains("codex: Available", output);
    }

    [Fact]
    public async Task LimitsSet_ManuallyBlocksAgent()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.Root, "agent-rate-limits.json");
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00-04:00");
        var rateLimitStore = new AgentRateLimitStateStore(path, () => now);
        var app = CreateApp(rateLimitStore);

        var exitCode = await app.RunAsync(
            ["limits", "set", "codex", "--until", "2026-06-26T13:27:00-04:00", "--reason", "Codex messages exhausted"],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var reloaded = new AgentRateLimitStateStore(path, () => now);
        Assert.True(reloaded.TryGetBlocked("codex", out var info));
        Assert.Equal(DateTimeOffset.Parse("2026-06-26T13:27:00-04:00"), info.BlockedUntil);
        Assert.Equal("Codex messages exhausted", info.Reason);
        // The other agent and dryrun are untouched.
        Assert.False(reloaded.TryGetBlocked("claude", out _));
        Assert.False(reloaded.TryGetBlocked("dryrun", out _));
    }

    [Fact]
    public async Task LimitsClear_RemovesBlockedState()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.Root, "agent-rate-limits.json");
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00-04:00");
        var rateLimitStore = new AgentRateLimitStateStore(path, () => now);
        rateLimitStore.SetBlocked(new AgentRateLimitInfo
        {
            AgentName = "codex",
            IsBlocked = true,
            BlockedUntil = DateTimeOffset.Parse("2026-06-26T13:27:00-04:00"),
            Reason = "Codex messages exhausted"
        });
        var app = CreateApp(rateLimitStore);

        var exitCode = await app.RunAsync(["limits", "clear", "codex"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        var reloaded = new AgentRateLimitStateStore(path, () => now);
        Assert.False(reloaded.TryGetBlocked("codex", out _));
    }

    [Fact]
    public async Task LimitsSet_RejectsUnsupportedAgent()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.Root, "agent-rate-limits.json");
        var rateLimitStore = new AgentRateLimitStateStore(path, () => DateTimeOffset.Parse("2026-06-26T12:00:00-04:00"));
        var app = CreateApp(rateLimitStore);

        var exitCode = await app.RunAsync(
            ["limits", "set", "dryrun", "--until", "2026-06-26T13:27:00-04:00"],
            CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    private static CommandLineApp CreateApp(AgentRateLimitStateStore rateLimitStore)
    {
        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var stateStore = new RunStateStore();
        var reportGenerator = new ReportGenerator(stateStore);
        var loader = new PromptFileLoader();
        var batchRunner = new BatchRunner(
            loader,
            new GitCheckpointManager(processRunner, logger),
            new VerificationRunner(processRunner, logger),
            stateStore,
            reportGenerator,
            new AgentAdapterFactory(processRunner, logger),
            logger,
            rateLimitStateStore: rateLimitStore);

        return new CommandLineApp(
            loader,
            batchRunner,
            stateStore,
            reportGenerator,
            rateLimitStore,
            logger,
            isElevated: () => false);
    }
}
