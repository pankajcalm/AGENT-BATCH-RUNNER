using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Gui.Services;

public sealed class GuiRunCoordinator
{
    private readonly object _gate = new();
    private readonly GuiLogger _eventSink;
    private readonly EffectiveAgentPolicy _effectiveAgentPolicy;
    private readonly RateLimitFallbackPolicy _fallbackPolicy;
    private readonly PromptFileLoader _loader;
    private readonly AgentRateLimitStateStore _rateLimitStateStore;
    private readonly IAgentPreflightService _preflightService;
    private IRunAgentRoutingController? _routingController;
    private RunResult? _lastResult;
    private AgentPreflightResult? _lastPreflightResult;
    private BatchConfig? _lastConfig;
    private string? _lastAgentOverride;
    private bool _isExecuting;

    public GuiRunCoordinator(
        GuiLogger eventSink,
        AgentRateLimitStateStore? rateLimitStateStore = null,
        EffectiveAgentPolicy? effectiveAgentPolicy = null,
        IAgentPreflightService? preflightService = null,
        RateLimitFallbackPolicy? fallbackPolicy = null)
    {
        _eventSink = eventSink;
        _effectiveAgentPolicy = effectiveAgentPolicy ?? new EffectiveAgentPolicy();
        _fallbackPolicy = fallbackPolicy ?? new RateLimitFallbackPolicy();
        _loader = new PromptFileLoader(_effectiveAgentPolicy, _fallbackPolicy);
        _rateLimitStateStore = rateLimitStateStore ?? new AgentRateLimitStateStore();
        _preflightService = preflightService ?? new AgentPreflightService(
            new ProcessRunner(),
            new AgentExecutableResolver());
    }

    public bool IsExecuting
    {
        get
        {
            lock (_gate)
            {
                return _isExecuting;
            }
        }
    }

    public RunResult? LastResult
    {
        get
        {
            lock (_gate)
            {
                return _lastResult;
            }
        }
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
        var effectiveAgents = _effectiveAgentPolicy.ResolveDistinctAgents(config, agentOverride).ToList();
        if (config.AutoSwitchOnRateLimit)
        {
            effectiveAgents.AddRange(_fallbackPolicy.GetConfiguredFallbackAgents(config));
        }

        return _preflightService.RunAsync(
            config,
            effectiveAgents.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            config.RepoPath,
            cancellationToken);
    }

    public bool HasAvailableConfiguredFallback(BatchConfig config, string sourceAgent)
    {
        return config.AutoSwitchOnRateLimit &&
               config.MaxRateLimitAgentSwitchesPerTask > 0 &&
               _fallbackPolicy.GetFallbacks(config, sourceAgent).Any(fallback =>
                   !_rateLimitStateStore.TryGetBlocked(fallback, out _));
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

    public async Task<RunResult> RunAsync(
        BatchConfig config,
        string? agentOverride,
        AgentPreflightResult? preflightResult,
        CancellationToken cancellationToken)
    {
        var routingController = new RunAgentRoutingController();
        lock (_gate)
        {
            _routingController = routingController;
            _lastConfig = config;
            _lastAgentOverride = agentOverride;
            _lastPreflightResult = preflightResult;
            _lastResult = null;
            _isExecuting = true;
        }

        try
        {
            var result = await CreateRunner().RunAsync(
                config,
                new RunOptions
                {
                    AgentOverride = agentOverride,
                    PreflightResult = preflightResult,
                    RoutingController = routingController
                },
                cancellationToken);
            lock (_gate)
            {
                _lastResult = result;
            }

            return result;
        }
        finally
        {
            lock (_gate)
            {
                _isExecuting = false;
            }
        }
    }

    public Task<RunResult> RunAsync(
        BatchConfig config,
        string? agentOverride,
        CancellationToken cancellationToken)
    {
        return RunAsync(config, agentOverride, null, cancellationToken);
    }

    public async Task QueueSwitchAsync(
        AgentSwitchRequest request,
        CancellationToken cancellationToken)
    {
        IRunAgentRoutingController controller;
        string? runId;
        lock (_gate)
        {
            controller = _routingController ??
                         throw new InvalidOperationException("No run is available for pending-agent switching.");
            runId = _lastResult?.RunId;
        }

        await controller.QueueSwitchAsync(request, cancellationToken);
        await _eventSink.OnRunEventAsync(
            new RunEvent
            {
                Kind = RunEventKind.AgentSwitchQueued,
                RunId = runId,
                PromptId = request.StartingPromptId,
                Agent = request.ReplacementAgent,
                EffectiveAgent = request.ReplacementAgent,
                RoutingReason = request.Reason,
                SourceAgent = request.SourceAgent,
                ReplacementAgent = request.ReplacementAgent,
                AffectedPromptIds = request.AffectedPromptIds,
                IsAutomaticRoutingChange = request.IsAutomatic,
                Message = $"Agent switch queued: {request.SourceAgent} to {request.ReplacementAgent}. It will apply at the next prompt boundary."
            },
            cancellationToken);
    }

    public async Task<RunResult> ContinueAsync(
        bool retryRateLimitedTask,
        CancellationToken cancellationToken)
    {
        BatchConfig config;
        RunResult previousResult;
        AgentPreflightResult? preflight;
        IRunAgentRoutingController routingController;
        string? agentOverride;
        lock (_gate)
        {
            if (_isExecuting)
            {
                throw new InvalidOperationException("The run is already executing.");
            }

            config = _lastConfig ?? throw new InvalidOperationException("No stopped run is available to continue.");
            previousResult = _lastResult ?? throw new InvalidOperationException("No stopped run result is available to continue.");
            routingController = _routingController ??
                                throw new InvalidOperationException("No routing state is available for the stopped run.");
            preflight = _lastPreflightResult;
            agentOverride = _lastAgentOverride;
            _isExecuting = true;
        }

        var skipPromptIds = previousResult.Tasks
            .Where(task => task.Status is RunStatus.Succeeded or RunStatus.UnverifiedSuccess ||
                           (!retryRateLimitedTask && task.Status == RunStatus.RateLimited))
            .Select(task => task.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var result = await CreateRunner().RunAsync(
                config,
                new RunOptions
                {
                    RunId = previousResult.RunId,
                    AgentOverride = agentOverride,
                    ExistingResult = previousResult,
                    SkipPromptIds = skipPromptIds,
                    PreflightResult = preflight,
                    RoutingController = routingController
                },
                cancellationToken);
            lock (_gate)
            {
                _lastResult = result;
                _lastPreflightResult = new AgentPreflightResult
                {
                    Succeeded = result.FailureKind is not RunFailureKind.PreflightFailed,
                    FailureReason = result.RunFailureReason,
                    Toolchains = [.. result.Toolchains]
                };
            }

            return result;
        }
        finally
        {
            lock (_gate)
            {
                _isExecuting = false;
            }
        }
    }

    private BatchRunner CreateRunner()
    {
        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var stateStore = new RunStateStore();
        var reportGenerator = new ReportGenerator(stateStore);
        var gitCheckpointManager = new GitCheckpointManager(processRunner, logger);
        var verificationRunner = new VerificationRunner(processRunner, logger, _eventSink);
        var agentFactory = new AgentAdapterFactory(processRunner, logger);
        return new BatchRunner(
            _loader,
            gitCheckpointManager,
            verificationRunner,
            stateStore,
            reportGenerator,
            agentFactory,
            logger,
            _eventSink,
            new AgentRateLimitDetector(),
            _rateLimitStateStore,
            _effectiveAgentPolicy,
            _preflightService,
            fallbackPolicy: _fallbackPolicy);
    }
}
