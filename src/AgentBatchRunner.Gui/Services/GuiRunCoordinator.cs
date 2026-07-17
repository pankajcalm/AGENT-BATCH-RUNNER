using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Gui.Services;

public sealed class GuiRunCoordinator
{
    private readonly GuiLogger _eventSink;
    private readonly EffectiveAgentPolicy _effectiveAgentPolicy;
    private readonly PromptFileLoader _loader;
    private readonly AgentRateLimitStateStore _rateLimitStateStore;
    private readonly IAgentPreflightService _preflightService;

    public GuiRunCoordinator(
        GuiLogger eventSink,
        AgentRateLimitStateStore? rateLimitStateStore = null,
        EffectiveAgentPolicy? effectiveAgentPolicy = null,
        IAgentPreflightService? preflightService = null)
    {
        _eventSink = eventSink;
        _effectiveAgentPolicy = effectiveAgentPolicy ?? new EffectiveAgentPolicy();
        _loader = new PromptFileLoader(_effectiveAgentPolicy);
        _rateLimitStateStore = rateLimitStateStore ?? new AgentRateLimitStateStore();
        _preflightService = preflightService ?? new AgentPreflightService(
            new ProcessRunner(),
            new AgentExecutableResolver());
    }

    public Task<BatchConfig> LoadConfigAsync(string promptFilePath, CancellationToken cancellationToken)
    {
        return _loader.LoadAsync(promptFilePath, cancellationToken);
    }

    public BatchValidationResult Validate(BatchConfig config)
    {
        return _loader.Validate(config);
    }

    public IReadOnlyList<EffectiveAgentSelection> ResolveAgents(BatchConfig config, string? agentOverride)
    {
        return _effectiveAgentPolicy.ResolveAll(config, agentOverride);
    }

    public Task<AgentPreflightResult> PreflightAsync(
        BatchConfig config,
        string? agentOverride,
        CancellationToken cancellationToken)
    {
        var effectiveAgents = _effectiveAgentPolicy.ResolveDistinctAgents(config, agentOverride);
        return _preflightService.RunAsync(config, effectiveAgents, config.RepoPath, cancellationToken);
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
        string? agentOverride,
        AgentPreflightResult? preflightResult,
        CancellationToken cancellationToken)
    {
        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var stateStore = new RunStateStore();
        var reportGenerator = new ReportGenerator(stateStore);
        var gitCheckpointManager = new GitCheckpointManager(processRunner, logger);
        var verificationRunner = new VerificationRunner(processRunner, logger, _eventSink);
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
            _eventSink,
            rateLimitDetector,
            _rateLimitStateStore,
            _effectiveAgentPolicy,
            _preflightService);

        return runner.RunAsync(
            config,
            new RunOptions
            {
                AgentOverride = agentOverride,
                PreflightResult = preflightResult
            },
            cancellationToken);
    }

    public Task<RunResult> RunAsync(
        BatchConfig config,
        string? agentOverride,
        CancellationToken cancellationToken)
    {
        return RunAsync(config, agentOverride, null, cancellationToken);
    }
}
