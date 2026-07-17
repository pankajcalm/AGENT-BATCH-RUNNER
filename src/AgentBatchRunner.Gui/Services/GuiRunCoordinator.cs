using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Gui.Services;

public sealed class GuiRunCoordinator(GuiLogger eventSink, AgentRateLimitStateStore? rateLimitStateStore = null)
{
    private readonly PromptFileLoader _loader = new();
    private readonly AgentRateLimitStateStore _rateLimitStateStore = rateLimitStateStore ?? new AgentRateLimitStateStore();

    public Task<BatchConfig> LoadConfigAsync(string promptFilePath, CancellationToken cancellationToken)
    {
        return _loader.LoadAsync(promptFilePath, cancellationToken);
    }

    public BatchValidationResult Validate(BatchConfig config)
    {
        return _loader.Validate(config);
    }

    public AgentRateLimitInfo GetAgentLimit(string agentName)
    {
        return _rateLimitStateStore.Get(agentName);
    }

    public IReadOnlyList<AgentRateLimitInfo> GetAgentLimits()
    {
        return new[] { "dryrun", "claude", "codex" }
            .Select(_rateLimitStateStore.Get)
            .ToList();
    }

    public void ClearAgentLimit(string agentName)
    {
        _rateLimitStateStore.Clear(agentName);
    }

    public void SetAgentLimit(string agentName, DateTimeOffset? blockedUntil, string reason)
    {
        _rateLimitStateStore.SetBlocked(new AgentRateLimitInfo
        {
            AgentName = agentName,
            IsBlocked = true,
            BlockedUntil = blockedUntil,
            Reason = string.IsNullOrWhiteSpace(reason) ? "Usage limit reached" : reason.Trim(),
            RawMessage = "Manually set via AgentBatchRunner GUI."
        });
    }

    public Task<RunResult> RunAsync(
        BatchConfig config,
        string agentOverride,
        CancellationToken cancellationToken)
    {
        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var stateStore = new RunStateStore();
        var reportGenerator = new ReportGenerator(stateStore);
        var gitCheckpointManager = new GitCheckpointManager(processRunner, logger);
        var verificationRunner = new VerificationRunner(processRunner, logger, eventSink);
        var agentFactory = new AgentAdapterFactory(processRunner, logger);
        var rateLimitDetector = new AgentRateLimitDetector();
        var runner = new BatchRunner(
            _loader,
            gitCheckpointManager,
            verificationRunner,
            stateStore,
            reportGenerator,
            agentFactory,
            logger,
            eventSink,
            rateLimitDetector,
            _rateLimitStateStore);

        return runner.RunAsync(
            config,
            new RunOptions { AgentOverride = agentOverride },
            cancellationToken);
    }
}
