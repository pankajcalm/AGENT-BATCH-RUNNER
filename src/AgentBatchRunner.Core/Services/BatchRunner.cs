using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class BatchRunner(
    PromptFileLoader promptFileLoader,
    GitCheckpointManager gitCheckpointManager,
    VerificationRunner verificationRunner,
    RunStateStore runStateStore,
    ReportGenerator reportGenerator,
    AgentAdapterFactory agentAdapterFactory,
    ConsoleLogger logger,
    IRunEventSink? runEventSink = null,
    AgentRateLimitDetector? rateLimitDetector = null,
    AgentRateLimitStateStore? rateLimitStateStore = null,
    EffectiveAgentPolicy? effectiveAgentPolicy = null,
    IAgentPreflightService? agentPreflightService = null,
    AgentToolchainFailureDetector? toolchainFailureDetector = null,
    RateLimitFallbackPolicy? fallbackPolicy = null,
    AgentOutcomeParser? agentOutcomeParser = null) : IBatchExecutionRunner
{
    private readonly AgentRateLimitDetector _rateLimitDetector = rateLimitDetector ?? new AgentRateLimitDetector();
    private readonly AgentRateLimitStateStore _rateLimitStateStore = rateLimitStateStore ?? new AgentRateLimitStateStore();
    private readonly EffectiveAgentPolicy _effectiveAgentPolicy = effectiveAgentPolicy ?? new EffectiveAgentPolicy();
    private readonly IAgentPreflightService _agentPreflightService = agentPreflightService ??
        new AgentPreflightService(new ProcessRunner(), new AgentExecutableResolver());
    private readonly AgentToolchainFailureDetector _toolchainFailureDetector = toolchainFailureDetector ?? new AgentToolchainFailureDetector();
    private readonly RateLimitFallbackPolicy _fallbackPolicy = fallbackPolicy ?? new RateLimitFallbackPolicy();
    private readonly AgentOutcomeParser _agentOutcomeParser = agentOutcomeParser ?? new AgentOutcomeParser();

    public async Task<RunResult> RunAsync(
        BatchConfig config,
        RunOptions options,
        CancellationToken cancellationToken = default)
    {
        var validation = promptFileLoader.Validate(config);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Invalid batch configuration: " + string.Join(" ", validation.Errors));
        }

        var agentOverride = EffectiveAgentPolicy.NormalizeOptional(options.AgentOverride);
        var selections = _effectiveAgentPolicy.ResolveAll(config, agentOverride);
        var selectionsByPrompt = selections.ToDictionary(
            selection => selection.PromptId,
            StringComparer.OrdinalIgnoreCase);

        gitCheckpointManager.EnsureRepository(config.RepoPath);

        var runId = options.RunId ?? DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var runDirectory = runStateStore.CreateRunDirectory(config.RepoPath, runId);
        var routingController = options.RoutingController;
        if (routingController is null)
        {
            var routingSnapshot = options.ExistingResult is null
                ? new RunRoutingSnapshot { RunId = runId }
                : await runStateStore.LoadRoutingAsync(runDirectory, cancellationToken);
            if (routingSnapshot.Changes.Count == 0 && options.ExistingResult?.RoutingChanges.Count > 0)
            {
                routingSnapshot.Changes = [.. options.ExistingResult.RoutingChanges];
            }

            routingController = new RunAgentRoutingController(routingSnapshot);
        }
        var result = options.ExistingResult ?? new RunResult
        {
            RunId = runId,
            Project = config.Project,
            RepoPath = config.RepoPath,
            StartedAt = DateTimeOffset.Now
        };

        result.RunId = runId;
        result.Project = config.Project;
        result.RepoPath = config.RepoPath;
        result.CompletedAt = null;
        result.DefaultAgent = EffectiveAgentPolicy.NormalizeOptional(config.DefaultAgent);
        result.AgentOverride = agentOverride;
        result.FailureKind = RunFailureKind.None;
        result.RunFailureReason = null;
        SynchronizeRoutingResult(result, routingController, runId);

        config.RunAgentOverride = agentOverride;
        foreach (var prompt in config.Prompts)
        {
            var selection = selectionsByPrompt[prompt.Id];
            prompt.EffectiveAgent = routingController.Resolve(
                prompt.Id,
                selection.BaseAgent,
                selection.BaseRoutingReason).EffectiveAgent;
        }

        logger.Info($"Run {runId} started for project {config.Project}.");
        await PublishAsync(
            new RunEvent
            {
                Kind = RunEventKind.RunStarted,
                RunId = runId,
                Message = $"Run {runId} started for project {config.Project}.",
                Path = runDirectory
            },
            cancellationToken);

        var activeSelections = selections
            .Where(selection => !options.SkipPromptIds.Contains(selection.PromptId))
            .ToList();
        var fallbackAgents = config.AutoSwitchOnRateLimit
            ? _fallbackPolicy.GetConfiguredFallbackAgents(config)
            : [];
        var requiredAgents = activeSelections
            .Select(selection => routingController.Resolve(
                selection.PromptId,
                selection.BaseAgent,
                selection.BaseRoutingReason).EffectiveAgent)
            .Concat(fallbackAgents)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var fallbackAgent in fallbackAgents)
        {
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.FallbackPreflightStarted,
                    RunId = runId,
                    Agent = fallbackAgent,
                    Message = $"Fallback preflight started: {fallbackAgent}.",
                    Path = runDirectory
                },
                cancellationToken);
        }
        await PublishAsync(
            new RunEvent
            {
                Kind = RunEventKind.PreflightStarted,
                RunId = runId,
                Message = "Agent toolchain preflight started.",
                Path = runDirectory
            },
            cancellationToken);

        var suppliedPreflightCoversRequiredAgents = options.PreflightResult is { Succeeded: true } supplied &&
            (supplied.Toolchains.Count == 0 || requiredAgents.All(agent => supplied.Find(agent) is not null));
        var preflight = suppliedPreflightCoversRequiredAgents
            ? options.PreflightResult!
            : await _agentPreflightService.RunAsync(
            config,
            requiredAgents,
            config.RepoPath,
            cancellationToken);
        ApplyPreflightMetadata(config, result, preflight);

        await runStateStore.SaveJsonAsync(
            Path.Combine(runDirectory, "run-config.normalized.json"),
            config,
            cancellationToken);

        if (!preflight.Succeeded)
        {
            var failureReason = preflight.FailureReason ?? "Agent toolchain preflight failed.";
            result.FailureKind = RunFailureKind.PreflightFailed;
            result.RunFailureReason = failureReason;
            await AddSkippedTasksAsync(
                result,
                config,
                activeSelections,
                runId,
                runDirectory,
                failureReason,
                cancellationToken);
            result.CompletedAt = DateTimeOffset.Now;
            await reportGenerator.GenerateAsync(runDirectory, result, cancellationToken);

            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.PreflightFailed,
                    RunId = runId,
                    Status = RunStatus.ToolchainFailure,
                    FailureReason = failureReason,
                    Message = failureReason,
                    Path = runDirectory
                },
                cancellationToken);
            var failedFallback = fallbackAgents.FirstOrDefault(agent =>
                preflight.Find(agent)?.Status == AgentPreflightStatus.Failed);
            if (failedFallback is not null)
            {
                await PublishAsync(
                    new RunEvent
                    {
                        Kind = RunEventKind.FallbackPreflightFailed,
                        RunId = runId,
                        Agent = failedFallback,
                        Status = RunStatus.ToolchainFailure,
                        FailureReason = failureReason,
                        Message = $"Fallback preflight failed for {failedFallback}: {failureReason}",
                        Path = runDirectory
                    },
                    cancellationToken);
            }
            await PublishReportGeneratedAsync(runId, runDirectory, cancellationToken);
            logger.Error($"Run {runId} stopped during agent preflight: {failureReason}");
            return result;
        }

        foreach (var toolchain in preflight.Toolchains)
        {
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.AgentPreflightSucceeded,
                    RunId = runId,
                    Agent = toolchain.AgentName,
                    Message = toolchain.Status == AgentPreflightStatus.NotRequired
                        ? $"{toolchain.AgentName} uses the built-in adapter; no executable preflight is required."
                        : $"{toolchain.AgentName} preflight passed: {toolchain.ExecutablePath} ({toolchain.Version}).",
                    Path = toolchain.ExecutablePath
                },
                cancellationToken);
        }

        foreach (var fallbackAgent in fallbackAgents)
        {
            if (IsPreflighted(preflight, fallbackAgent))
            {
                await PublishAsync(
                    new RunEvent
                    {
                        Kind = RunEventKind.FallbackPreflightPassed,
                        RunId = runId,
                        Agent = fallbackAgent,
                        Message = $"Fallback preflight passed: {fallbackAgent}.",
                        Path = preflight.Find(fallbackAgent)?.ExecutablePath
                    },
                    cancellationToken);
            }
        }

        foreach (var prompt in config.Prompts)
        {
            var selection = selectionsByPrompt[prompt.Id];
            var decision = routingController.Resolve(prompt.Id, selection.BaseAgent, selection.BaseRoutingReason);
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.TaskPending,
                    RunId = runId,
                    PromptId = prompt.Id,
                    Title = prompt.Title,
                    Agent = decision.EffectiveAgent,
                    BaseAgent = decision.BaseAgent,
                    EffectiveAgent = decision.EffectiveAgent,
                    RoutingReason = decision.RoutingReason,
                    MaxAttempts = prompt.MaxRetries ?? config.DefaultMaxRetries,
                    Status = RunStatus.Pending,
                    Message = $"Task {prompt.Id} is pending."
                },
                cancellationToken);
        }

        try
        {
            for (var promptIndex = 0; promptIndex < config.Prompts.Count; promptIndex++)
            {
                var pendingCandidates = BuildPendingCandidates(
                    config,
                    selectionsByPrompt,
                    promptIndex,
                    options.SkipPromptIds);
                var queuedSwitchFailure = await ApplyQueuedSwitchesAsync(
                    config,
                    preflight,
                    routingController,
                    pendingCandidates,
                    result,
                    runId,
                    runDirectory,
                    cancellationToken);
                if (queuedSwitchFailure is not null)
                {
                    result.FailureKind = RunFailureKind.PreflightFailed;
                    result.RunFailureReason = queuedSwitchFailure;
                    await AddSkippedTasksAsync(
                        result,
                        config,
                        pendingCandidates.Select(candidate => selectionsByPrompt[candidate.PromptId]),
                        runId,
                        runDirectory,
                        queuedSwitchFailure,
                        cancellationToken);
                    break;
                }

                var prompt = config.Prompts[promptIndex];
                if (options.SkipPromptIds.Contains(prompt.Id))
                {
                    logger.Info($"Skipping {prompt.Id}; previous result succeeded.");
                    continue;
                }

                var selection = selectionsByPrompt[prompt.Id];
                var existingTask = result.Tasks.FirstOrDefault(t =>
                    string.Equals(t.Id, prompt.Id, StringComparison.OrdinalIgnoreCase));
                var decision = routingController.Resolve(
                    prompt.Id,
                    selection.BaseAgent,
                    selection.BaseRoutingReason);

                if (_rateLimitStateStore.TryGetBlocked(decision.EffectiveAgent, out var preBlockedInfo) &&
                    config.AutoSwitchOnRateLimit &&
                    CountSwitchesForPrompt(result, prompt.Id) < config.MaxRateLimitAgentSwitchesPerTask &&
                    TrySelectFallback(
                        config,
                        decision.EffectiveAgent,
                        existingTask?.Attempts.Select(attempt => attempt.AttemptAgent) ?? [],
                        preflight,
                        out var preBlockedFallback))
                {
                    var switchRequest = new AgentSwitchRequest
                    {
                        SourceAgent = decision.EffectiveAgent,
                        ReplacementAgent = preBlockedFallback,
                        Reason = AgentRoutingReason.RateLimitFallback,
                        IsAutomatic = true,
                        StartingPromptId = prompt.Id,
                        RateLimitResetAt = preBlockedInfo.BlockedUntil,
                        RetryRateLimitedTask = true
                    };
                    await ApplyRoutingSwitchAsync(
                        routingController,
                        switchRequest,
                        pendingCandidates,
                        result,
                        runId,
                        runDirectory,
                        prompt.Id,
                        cancellationToken);
                    decision = routingController.Resolve(
                        prompt.Id,
                        selection.BaseAgent,
                        selection.BaseRoutingReason);
                }

                var taskResult = await RunPromptTaskAsync(
                    config,
                    prompt,
                    selection,
                    decision,
                    preflight,
                    routingController,
                    pendingCandidates,
                    result,
                    existingTask,
                    runId,
                    runDirectory,
                    cancellationToken);
                if (existingTask is null)
                {
                    result.Tasks.Add(taskResult);
                }

                SynchronizeRoutingResult(result, routingController, runId);
                await runStateStore.SaveRoutingAsync(
                    runDirectory,
                    routingController.CreateSnapshot(runId),
                    cancellationToken);

                await runStateStore.SaveJsonAsync(
                    Path.Combine(runDirectory, "run-summary.json"),
                    result,
                    cancellationToken);

                if (taskResult.Status == RunStatus.RateLimited)
                {
                    logger.Warning($"Run {runId} stopped because {taskResult.Agent} is rate-limited.");
                    break;
                }

                if (IsBlockingAgentOutcomeStatus(taskResult.Status))
                {
                    var failureReason = taskResult.LastFailureReason ??
                                        $"Task {taskResult.Id} reported {taskResult.Status}.";
                    result.FailureKind = RunFailureKind.AgentOutcomeBlocked;
                    result.RunFailureReason = failureReason;
                    var untouchedSelections = config.Prompts
                        .Skip(promptIndex + 1)
                        .Where(remaining => !options.SkipPromptIds.Contains(remaining.Id))
                        .Select(remaining => selectionsByPrompt[remaining.Id])
                        .ToList();
                    await AddSkippedTasksAsync(
                        result,
                        config,
                        untouchedSelections,
                        runId,
                        runDirectory,
                        failureReason,
                        cancellationToken);
                    logger.Warning($"Run {runId} stopped because task {taskResult.Id} reported {taskResult.Status}.");
                    break;
                }

                if (taskResult.Status == RunStatus.ToolchainFailure)
                {
                    var failureReason = taskResult.LastFailureReason ?? $"{taskResult.Agent} toolchain failed.";
                    result.FailureKind = RunFailureKind.ToolchainFailure;
                    result.RunFailureReason = failureReason;
                    var untouchedSelections = config.Prompts
                        .Skip(promptIndex + 1)
                        .Where(remaining => !options.SkipPromptIds.Contains(remaining.Id))
                        .Select(remaining => selectionsByPrompt[remaining.Id])
                        .ToList();
                    await AddSkippedTasksAsync(
                        result,
                        config,
                        untouchedSelections,
                        runId,
                        runDirectory,
                        failureReason,
                        cancellationToken);
                    logger.Error($"Run {runId} stopped because the {taskResult.Agent} toolchain is unusable: {failureReason}");
                    await PublishAsync(
                        new RunEvent
                        {
                            Kind = RunEventKind.RunToolchainFailed,
                            RunId = runId,
                            Agent = taskResult.Agent,
                            Status = RunStatus.ToolchainFailure,
                            FailureReason = failureReason,
                            Message = failureReason,
                            Path = runDirectory
                        },
                        cancellationToken);
                    break;
                }
            }

            result.CompletedAt = DateTimeOffset.Now;
            SynchronizeRoutingResult(result, routingController, runId);
            await runStateStore.SaveRoutingAsync(
                runDirectory,
                routingController.CreateSnapshot(runId),
                cancellationToken);
            await reportGenerator.GenerateAsync(runDirectory, result, cancellationToken);
            var reportPath = await PublishReportGeneratedAsync(runId, runDirectory, cancellationToken);
            await PublishAsync(
                new RunEvent
                {
                    Kind = result.FailureKind == RunFailureKind.ToolchainFailure
                        ? RunEventKind.RunToolchainFailed
                        : result.FailureKind == RunFailureKind.AgentOutcomeBlocked
                        ? RunEventKind.RunBlocked
                        : result.RateLimited > 0 ? RunEventKind.RunRateLimited : RunEventKind.RunCompleted,
                    RunId = runId,
                    Status = result.FailureKind == RunFailureKind.ToolchainFailure
                        ? RunStatus.ToolchainFailure
                        : result.FailureKind == RunFailureKind.AgentOutcomeBlocked
                        ? result.Tasks.LastOrDefault(task => IsBlockingAgentOutcomeStatus(task.Status))?.Status
                        : result.RateLimited > 0 ? RunStatus.RateLimited : null,
                    Message = result.FailureKind == RunFailureKind.ToolchainFailure
                        ? $"Run {runId} stopped because an agent toolchain failed."
                        : result.FailureKind == RunFailureKind.AgentOutcomeBlocked
                        ? $"Run {runId} stopped on an explicit agent outcome."
                        : result.RateLimited > 0
                        ? $"Run {runId} stopped because an agent is rate-limited."
                        : $"Run {runId} completed.",
                    Path = runDirectory
                },
                cancellationToken);
            logger.Info($"Run {runId} completed. Report: {reportPath}");

            return result;
        }
        catch (OperationCanceledException)
        {
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.RunCanceled,
                    RunId = runId,
                    Message = $"Run {runId} canceled.",
                    Path = runDirectory
                },
                CancellationToken.None);
            throw;
        }
    }

    private async Task<TaskRunResult> RunPromptTaskAsync(
        BatchConfig config,
        PromptTask prompt,
        EffectiveAgentSelection selection,
        AgentRoutingDecision initialDecision,
        AgentPreflightResult preflight,
        IRunAgentRoutingController routingController,
        IReadOnlyCollection<AgentRoutingCandidate> pendingCandidates,
        RunResult runResult,
        TaskRunResult? existingTask,
        string runId,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        gitCheckpointManager.EnsureRepository(config.RepoPath);

        var maxAttempts = prompt.MaxRetries ?? config.DefaultMaxRetries;
        var agentTimeoutSeconds = prompt.AgentTimeoutSeconds ?? config.DefaultAgentTimeoutSeconds;
        var verifyTimeoutSeconds = prompt.VerifyTimeoutSeconds ?? config.DefaultVerifyTimeoutSeconds;
        var verifyTimeout = TimeSpan.FromSeconds(verifyTimeoutSeconds);
        var agentOptions = new AgentInvocationOptions
        {
            TimeoutSeconds = agentTimeoutSeconds,
            ClaudePermissionMode = config.ClaudePermissionMode,
            ClaudeDangerouslySkipPermissions = config.ClaudeDangerouslySkipPermissions,
            CodexSandbox = config.CodexSandbox,
            CodexFullAuto = config.CodexFullAuto
        };
        var taskDirectory = !string.IsNullOrWhiteSpace(existingTask?.TaskDirectory)
            ? existingTask.TaskDirectory
            : Path.Combine(runDirectory, "tasks", FileNameSanitizer.Sanitize(prompt.Id));
        Directory.CreateDirectory(taskDirectory);

        var priorTaskStatus = existingTask?.Status;
        var priorEffectiveAgent = existingTask?.EffectiveAgent;
        if (string.IsNullOrWhiteSpace(priorEffectiveAgent))
        {
            priorEffectiveAgent = existingTask?.Agent;
        }

        var taskResult = existingTask ?? new TaskRunResult
        {
            Id = prompt.Id,
            Title = prompt.Title,
            StartedAt = DateTimeOffset.Now,
            TaskDirectory = taskDirectory
        };
        var decision = routingController.Resolve(
            prompt.Id,
            selection.BaseAgent,
            selection.BaseRoutingReason);
        var agentName = decision.EffectiveAgent;
        taskResult.Id = prompt.Id;
        taskResult.Title = prompt.Title;
        taskResult.Agent = agentName;
        taskResult.BaseAgent = decision.BaseAgent;
        taskResult.EffectiveAgent = agentName;
        taskResult.RoutingReason = decision.RoutingReason;
        taskResult.ConfiguredAgent = selection.ConfiguredAgent;
        taskResult.DefaultAgent = selection.DefaultAgent;
        taskResult.AgentOverride = selection.RunOverride;
        taskResult.Status = RunStatus.Running;
        taskResult.CompletedAt = null;
        taskResult.TaskDirectory = taskDirectory;
        taskResult.AgentSwitchCount = CountSwitchesForPrompt(runResult, prompt.Id);
        var latestRoutingChange = runResult.RoutingChanges.LastOrDefault(change =>
            change.AffectedPromptIds.Contains(prompt.Id, StringComparer.OrdinalIgnoreCase));
        if (latestRoutingChange?.Reason == AgentRoutingReason.RateLimitFallback)
        {
            taskResult.RateLimitedSourceAgent = latestRoutingChange.SourceAgent;
            taskResult.FallbackAgent = latestRoutingChange.ReplacementAgent;
            taskResult.RateLimitResetAt ??= latestRoutingChange.RateLimitResetAt;
        }

        var normalAttemptsUsed = priorTaskStatus == RunStatus.RateLimited
            ? taskResult.RetryAttemptsConsumed
            : 0;
        taskResult.RetryAttemptsConsumed = normalAttemptsUsed;

        var priorRateLimitedAttempt = taskResult.Attempts
            .OrderByDescending(attempt => attempt.AttemptNumber)
            .FirstOrDefault(attempt => attempt.Status == RunStatus.RateLimited);
        var priorAttemptAgent = priorRateLimitedAttempt?.AttemptAgent;
        if (string.IsNullOrWhiteSpace(priorAttemptAgent))
        {
            priorAttemptAgent = priorRateLimitedAttempt?.AgentResult?.AgentName ?? priorEffectiveAgent;
        }

        var activePrompt = priorRateLimitedAttempt is not null &&
                           !string.IsNullOrWhiteSpace(priorAttemptAgent) &&
                           !string.Equals(priorAttemptAgent, agentName, StringComparison.OrdinalIgnoreCase)
            ? RetryPromptBuilder.BuildRateLimitFallback(
                prompt.Prompt,
                priorAttemptAgent,
                priorRateLimitedAttempt.AgentResult?.CombinedOutput ?? taskResult.LastFailureReason ?? string.Empty)
            : prompt.Prompt;
        string? previousAgentForAttempt = priorRateLimitedAttempt is not null &&
                                          !string.Equals(priorAttemptAgent, agentName, StringComparison.OrdinalIgnoreCase)
            ? priorAttemptAgent
            : null;

        await Utf8File.WriteAllTextAsync(Path.Combine(taskDirectory, "prompt.md"), prompt.Prompt, cancellationToken);
        await runStateStore.SaveJsonAsync(Path.Combine(taskDirectory, "status.json"), taskResult, cancellationToken);

        logger.Info($"[{prompt.Id}] Starting '{prompt.Title}' with {agentName}; max retry-consuming attempts: {maxAttempts}.");
        await PublishAsync(
            new RunEvent
            {
                Kind = RunEventKind.TaskStarted,
                RunId = runId,
                PromptId = prompt.Id,
                Title = prompt.Title,
                Agent = agentName,
                BaseAgent = decision.BaseAgent,
                EffectiveAgent = agentName,
                RoutingReason = decision.RoutingReason,
                MaxAttempts = maxAttempts,
                Status = RunStatus.Running,
                Message = $"Task {prompt.Id} started with {agentName}.",
                Path = taskDirectory
            },
            cancellationToken);

        if (string.IsNullOrWhiteSpace(taskResult.CheckpointId))
        {
            taskResult.CheckpointId = await gitCheckpointManager.CreateCheckpointAsync(
                config.RepoPath,
                taskDirectory,
                prompt.Id,
                cancellationToken);
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.CheckpointCreated,
                    RunId = runId,
                    PromptId = prompt.Id,
                    Title = prompt.Title,
                    Agent = agentName,
                    BaseAgent = decision.BaseAgent,
                    EffectiveAgent = agentName,
                    RoutingReason = decision.RoutingReason,
                    MaxAttempts = maxAttempts,
                    Status = RunStatus.Running,
                    Message = $"Checkpoint branch created: {taskResult.CheckpointId}",
                    Path = taskResult.CheckpointId
                },
                cancellationToken);
        }
        else
        {
            logger.Info($"[{prompt.Id}] Continuing with existing checkpoint {taskResult.CheckpointId}.");
        }

        var adapter = agentAdapterFactory.Create(agentName);
        string? sessionId = null;
        var resumeCurrentAgentSession = false;
        var invocationNumber = taskResult.Attempts.Count == 0
            ? 1
            : taskResult.Attempts.Max(attempt => attempt.AttemptNumber) + 1;
        var attemptedAgents = taskResult.Attempts
            .Select(attempt => string.IsNullOrWhiteSpace(attempt.AttemptAgent)
                ? attempt.AgentResult?.AgentName
                : attempt.AttemptAgent)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        while (normalAttemptsUsed < maxAttempts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_rateLimitStateStore.TryGetBlocked(agentName, out var guardedBlock) &&
                config.AutoSwitchOnRateLimit &&
                taskResult.AgentSwitchCount < config.MaxRateLimitAgentSwitchesPerTask &&
                TrySelectFallback(config, agentName, attemptedAgents, preflight, out var guardedFallback))
            {
                attemptedAgents.Add(agentName);
                var guardedRequest = new AgentSwitchRequest
                {
                    SourceAgent = agentName,
                    ReplacementAgent = guardedFallback,
                    Reason = AgentRoutingReason.RateLimitFallback,
                    IsAutomatic = true,
                    StartingPromptId = prompt.Id,
                    RateLimitResetAt = guardedBlock.BlockedUntil,
                    RetryRateLimitedTask = true
                };
                await ApplyRoutingSwitchAsync(
                    routingController,
                    guardedRequest,
                    pendingCandidates,
                    runResult,
                    runId,
                    runDirectory,
                    prompt.Id,
                    cancellationToken);
                decision = routingController.Resolve(prompt.Id, selection.BaseAgent, selection.BaseRoutingReason);
                previousAgentForAttempt = agentName;
                agentName = decision.EffectiveAgent;
                taskResult.Agent = agentName;
                taskResult.EffectiveAgent = agentName;
                taskResult.RoutingReason = decision.RoutingReason;
                taskResult.AgentSwitchCount = CountSwitchesForPrompt(runResult, prompt.Id);
                taskResult.RateLimitedSourceAgent = previousAgentForAttempt;
                taskResult.FallbackAgent = agentName;
                activePrompt = RetryPromptBuilder.BuildRateLimitFallback(
                    prompt.Prompt,
                    previousAgentForAttempt,
                    AgentRateLimitDisplay.BlockedMessage(previousAgentForAttempt, guardedBlock));
                sessionId = null;
                resumeCurrentAgentSession = false;
                adapter = agentAdapterFactory.Create(agentName);
                continue;
            }

            var currentInvocation = invocationNumber++;
            var attemptDirectory = Path.Combine(taskDirectory, "attempts", $"attempt-{currentInvocation}");
            Directory.CreateDirectory(attemptDirectory);

            var attemptResult = new AttemptResult
            {
                AttemptNumber = currentInvocation,
                AttemptAgent = agentName,
                ConsumesRetry = true,
                AgentSwitchNumber = taskResult.AgentSwitchCount,
                RoutingReason = decision.RoutingReason,
                PreviousAgent = previousAgentForAttempt,
                AttemptDirectory = attemptDirectory,
                Status = RunStatus.Running,
                StartedAt = DateTimeOffset.Now
            };
            previousAgentForAttempt = null;
            taskResult.Attempts.Add(attemptResult);
            taskResult.LatestAttemptAgent = agentName;

            logger.Info($"[{prompt.Id}] Invocation {currentInvocation}; retry budget {normalAttemptsUsed + 1}/{maxAttempts}; agent {agentName}.");
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.AttemptStarted,
                    RunId = runId,
                    PromptId = prompt.Id,
                    Title = prompt.Title,
                    Agent = agentName,
                    BaseAgent = decision.BaseAgent,
                    EffectiveAgent = decision.EffectiveAgent,
                    AttemptAgent = agentName,
                    RoutingReason = decision.RoutingReason,
                    AttemptNumber = currentInvocation,
                    MaxAttempts = maxAttempts,
                    Status = RunStatus.Running,
                    Message = $"Invocation {currentInvocation} started with {agentName}; retry budget {normalAttemptsUsed + 1}/{maxAttempts}.",
                    Path = attemptDirectory
                },
                cancellationToken);

            AgentExecutionResult agentResult;
            if (_rateLimitStateStore.TryGetBlocked(agentName, out var blockedInfo))
            {
                agentResult = CreateRateLimitedAgentResult(agentName, blockedInfo);
            }
            else
            {
                await PublishAsync(
                    new RunEvent
                    {
                        Kind = RunEventKind.AgentStarted,
                        RunId = runId,
                        PromptId = prompt.Id,
                        Title = prompt.Title,
                        Agent = agentName,
                        BaseAgent = decision.BaseAgent,
                        EffectiveAgent = decision.EffectiveAgent,
                        AttemptAgent = agentName,
                        RoutingReason = decision.RoutingReason,
                        AttemptNumber = currentInvocation,
                        MaxAttempts = maxAttempts,
                        Status = RunStatus.Running,
                        Command = agentName,
                        WorkingDirectory = config.RepoPath,
                        Message = $"Agent command started: {agentName}.",
                        Path = attemptDirectory
                    },
                    cancellationToken);

                agentResult = await adapter.ExecuteAsync(
                    new AgentExecutionRequest
                    {
                        RepoPath = config.RepoPath,
                        PromptId = prompt.Id,
                        Prompt = activePrompt,
                        AttemptNumber = currentInvocation,
                        ResumeSession = resumeCurrentAgentSession,
                        SessionId = sessionId,
                        AttemptDirectory = attemptDirectory,
                        ExecutablePath = preflight.Find(agentName)?.ExecutablePath,
                        Options = agentOptions
                    },
                    cancellationToken);

                ApplyDetectedRateLimit(agentName, agentResult);
                var toolchainFailureReason = _toolchainFailureDetector.Detect(agentName, agentResult);
                if (!string.IsNullOrWhiteSpace(toolchainFailureReason))
                {
                    agentResult.IsToolchainFailure = true;
                    agentResult.ToolchainFailureReason = toolchainFailureReason;
                }
            }

            agentResult.Outcome ??= _agentOutcomeParser.Parse(agentResult.CombinedOutput);
            ApplyStructuredRateLimit(agentName, agentResult);

            attemptResult.AgentResult = agentResult;
            attemptResult.AgentOutcome = agentResult.Outcome;
            taskResult.AgentOutcome = agentResult.Outcome;
            taskResult.RecommendedNextFile = agentResult.Outcome?.RecommendedNext;
            sessionId = agentResult.SessionId ?? sessionId;
            attemptedAgents.Add(agentName);

            await WriteAgentOutputAsync(attemptDirectory, config.RepoPath, agentResult, cancellationToken);
            await PublishAsync(
                new RunEvent
                {
                    Kind = agentResult.IsRateLimited
                        ? RunEventKind.AgentRateLimited
                        : agentResult.IsToolchainFailure
                        ? RunEventKind.AgentToolchainFailed
                        : agentResult.TimedOut
                        ? RunEventKind.AgentTimedOut
                        : agentResult.Succeeded
                            ? RunEventKind.AgentCompleted
                            : RunEventKind.AgentFailed,
                    RunId = runId,
                    PromptId = prompt.Id,
                    Title = prompt.Title,
                    Agent = agentName,
                    BaseAgent = decision.BaseAgent,
                    EffectiveAgent = decision.EffectiveAgent,
                    AttemptAgent = agentName,
                    RoutingReason = decision.RoutingReason,
                    AttemptNumber = currentInvocation,
                    MaxAttempts = maxAttempts,
                    Status = agentResult.IsRateLimited
                        ? RunStatus.RateLimited
                        : agentResult.IsToolchainFailure
                        ? RunStatus.ToolchainFailure
                        : agentResult.Succeeded ? RunStatus.Running : RunStatus.Failed,
                    Command = agentResult.Command,
                    WorkingDirectory = config.RepoPath,
                    ExitCode = agentResult.ExitCode,
                    Duration = agentResult.Duration,
                    TimedOut = agentResult.TimedOut,
                    Timeout = agentResult.Timeout,
                    StandardOutput = agentResult.StandardOutput,
                    StandardError = agentResult.StandardError,
                    CombinedOutput = agentResult.CombinedOutput,
                    RateLimitResetAt = agentResult.RateLimitResetAt,
                    RateLimitReason = agentResult.RateLimitReason,
                    FailureReason = agentResult.ToolchainFailureReason,
                    Message = BuildAgentResultMessage(agentName, agentResult),
                    Path = Path.Combine(attemptDirectory, "agent-output.txt")
                },
                cancellationToken);

            if (agentResult.Outcome is not null)
            {
                await PublishAsync(
                    new RunEvent
                    {
                        Kind = RunEventKind.AgentOutcomeReported,
                        RunId = runId,
                        PromptId = prompt.Id,
                        Title = prompt.Title,
                        Agent = agentName,
                        AttemptAgent = agentName,
                        AttemptNumber = currentInvocation,
                        Status = MapAgentOutcomeStatus(agentResult.Outcome.AgentOutcome),
                        AgentOutcome = agentResult.Outcome,
                        FailureReason = agentResult.Outcome.Blocker,
                        Message = $"Agent reported outcome {agentResult.Outcome.AgentOutcome}. Accepted as workflow control data.",
                        Path = Path.Combine(attemptDirectory, "status.json")
                    },
                    cancellationToken);
            }

            if (agentResult.IsRateLimited)
            {
                var rateLimitMessage = AgentRateLimitDisplay.BlockedMessage(agentName, new AgentRateLimitInfo
                {
                    AgentName = agentName,
                    IsBlocked = true,
                    BlockedUntil = agentResult.RateLimitResetAt,
                    Reason = agentResult.RateLimitReason ?? string.Empty
                });
                attemptResult.ConsumesRetry = false;
                attemptResult.Status = RunStatus.RateLimited;
                attemptResult.CompletedAt = DateTimeOffset.Now;
                taskResult.Status = RunStatus.RateLimited;
                taskResult.CompletedAt = DateTimeOffset.Now;
                taskResult.LastFailedVerificationCommand = $"agent:{adapter.Name}";
                taskResult.LastFailedExitCode = agentResult.ExitCode;
                taskResult.LastFailedLogPath = Path.Combine(attemptDirectory, "agent-output.txt");
                taskResult.LastFailureReason = rateLimitMessage;
                taskResult.RateLimitResetAt = agentResult.RateLimitResetAt;
                taskResult.RateLimitReason = agentResult.RateLimitReason;
                taskResult.RateLimitedSourceAgent = agentName;
                taskResult.AgentOutcome = agentResult.Outcome;
                await runStateStore.SaveJsonAsync(Path.Combine(attemptDirectory, "status.json"), attemptResult, cancellationToken);
                await runStateStore.SaveJsonAsync(Path.Combine(taskDirectory, "status.json"), taskResult, cancellationToken);

                if (config.AutoSwitchOnRateLimit &&
                    taskResult.AgentSwitchCount < config.MaxRateLimitAgentSwitchesPerTask &&
                    TrySelectFallback(config, agentName, attemptedAgents, preflight, out var fallbackAgent))
                {
                    var previousAgent = agentName;
                    var fallbackRequest = new AgentSwitchRequest
                    {
                        SourceAgent = previousAgent,
                        ReplacementAgent = fallbackAgent,
                        Reason = AgentRoutingReason.RateLimitFallback,
                        IsAutomatic = true,
                        StartingPromptId = prompt.Id,
                        RateLimitResetAt = agentResult.RateLimitResetAt,
                        RetryRateLimitedTask = true
                    };
                    await ApplyRoutingSwitchAsync(
                        routingController,
                        fallbackRequest,
                        pendingCandidates,
                        runResult,
                        runId,
                        runDirectory,
                        prompt.Id,
                        cancellationToken);
                    decision = routingController.Resolve(prompt.Id, selection.BaseAgent, selection.BaseRoutingReason);
                    agentName = decision.EffectiveAgent;
                    taskResult.Agent = agentName;
                    taskResult.EffectiveAgent = agentName;
                    taskResult.RoutingReason = decision.RoutingReason;
                    taskResult.AgentSwitchCount = CountSwitchesForPrompt(runResult, prompt.Id);
                    taskResult.FallbackAgent = agentName;
                    taskResult.Status = RunStatus.Running;
                    taskResult.CompletedAt = null;
                    activePrompt = RetryPromptBuilder.BuildRateLimitFallback(
                        prompt.Prompt,
                        previousAgent,
                        agentResult.CombinedOutput);
                    previousAgentForAttempt = previousAgent;
                    sessionId = null;
                    resumeCurrentAgentSession = false;
                    adapter = agentAdapterFactory.Create(agentName);
                    await PublishAsync(
                        new RunEvent
                        {
                            Kind = RunEventKind.RateLimitedTaskContinuing,
                            RunId = runId,
                            PromptId = prompt.Id,
                            Title = prompt.Title,
                            Agent = agentName,
                            BaseAgent = decision.BaseAgent,
                            EffectiveAgent = agentName,
                            AttemptAgent = agentName,
                            RoutingReason = decision.RoutingReason,
                            Status = RunStatus.Running,
                            SourceAgent = previousAgent,
                            ReplacementAgent = agentName,
                            Message = $"Rate-limited task {prompt.Id} continuing with {agentName}.",
                            Path = taskDirectory
                        },
                        cancellationToken);
                    continue;
                }

                break;
            }

            if (agentResult.Outcome is { StopsWithoutRetry: true } structuredOutcome)
            {
                var outcomeStatus = MapAgentOutcomeStatus(structuredOutcome.AgentOutcome);
                attemptResult.ConsumesRetry = false;
                attemptResult.Status = outcomeStatus;
                attemptResult.CompletedAt = DateTimeOffset.Now;
                taskResult.Status = outcomeStatus;
                taskResult.CompletedAt = DateTimeOffset.Now;
                taskResult.AgentOutcome = structuredOutcome;
                taskResult.RecommendedNextFile = structuredOutcome.RecommendedNext;
                taskResult.LastFailedVerificationCommand = $"agent:{adapter.Name}";
                taskResult.LastFailedExitCode = agentResult.ExitCode;
                taskResult.LastFailedLogPath = Path.Combine(attemptDirectory, "agent-output.txt");
                taskResult.LastFailureReason = structuredOutcome.Blocker ??
                                               $"Agent reported {structuredOutcome.AgentOutcome}.";
                await runStateStore.SaveJsonAsync(Path.Combine(attemptDirectory, "status.json"), attemptResult, cancellationToken);
                await runStateStore.SaveJsonAsync(Path.Combine(taskDirectory, "status.json"), taskResult, cancellationToken);
                break;
            }

            normalAttemptsUsed++;
            taskResult.RetryAttemptsConsumed = normalAttemptsUsed;

            if (agentResult.IsToolchainFailure)
            {
                var toolchainFailureReason = agentResult.ToolchainFailureReason ?? "Agent toolchain failed.";
                attemptResult.Status = RunStatus.ToolchainFailure;
                attemptResult.CompletedAt = DateTimeOffset.Now;
                taskResult.Status = RunStatus.ToolchainFailure;
                taskResult.CompletedAt = DateTimeOffset.Now;
                taskResult.LastFailedVerificationCommand = $"agent:{adapter.Name}";
                taskResult.LastFailedExitCode = agentResult.ExitCode;
                taskResult.LastFailedLogPath = Path.Combine(attemptDirectory, "agent-output.txt");
                taskResult.LastFailureReason = toolchainFailureReason;
                await runStateStore.SaveJsonAsync(Path.Combine(attemptDirectory, "status.json"), attemptResult, cancellationToken);
                await runStateStore.SaveJsonAsync(Path.Combine(taskDirectory, "status.json"), taskResult, cancellationToken);
                break;
            }

            if (!agentResult.Succeeded)
            {
                attemptResult.Status = RunStatus.Failed;
                attemptResult.CompletedAt = DateTimeOffset.Now;
                attemptResult.TimedOut = agentResult.TimedOut;
                attemptResult.TimeoutReason = agentResult.TimedOut
                    ? $"Agent command timed out after {agentResult.Timeout?.TotalSeconds:0}s."
                    : null;
                taskResult.TimedOut |= agentResult.TimedOut;
                taskResult.LastFailedVerificationCommand = $"agent:{adapter.Name}";
                taskResult.LastFailedExitCode = agentResult.ExitCode;
                taskResult.LastFailedLogPath = Path.Combine(attemptDirectory, "agent-output.txt");
                taskResult.LastFailureReason = agentResult.TimedOut
                    ? attemptResult.TimeoutReason
                    : "Agent command failed.";
                activePrompt = RetryPromptBuilder.Build(
                    prompt.Prompt,
                    $"agent:{adapter.Name}",
                    agentResult.ExitCode,
                    agentResult.CombinedOutput,
                    agentResult.TimedOut,
                    agentResult.Timeout);
                await runStateStore.SaveJsonAsync(Path.Combine(attemptDirectory, "status.json"), attemptResult, cancellationToken);
                await runStateStore.SaveJsonAsync(Path.Combine(taskDirectory, "status.json"), taskResult, cancellationToken);
                if (normalAttemptsUsed < maxAttempts)
                {
                    resumeCurrentAgentSession = true;
                    await PublishRetryAsync(
                        runId,
                        prompt,
                        agentName,
                        invocationNumber,
                        maxAttempts,
                        taskResult.LastFailureReason,
                        cancellationToken);
                }

                continue;
            }

            var verificationResult = await verificationRunner.RunAsync(
                prompt.Verify,
                config.RepoPath,
                attemptDirectory,
                cancellationToken,
                verifyTimeout,
                runId,
                prompt.Id,
                prompt.Title,
                agentName,
                currentInvocation,
                maxAttempts);
            attemptResult.VerificationResult = verificationResult;

            if (verificationResult.Unverified)
            {
                attemptResult.Status = RunStatus.Succeeded;
                attemptResult.CompletedAt = DateTimeOffset.Now;
                taskResult.Status = RunStatus.UnverifiedSuccess;
                taskResult.CompletedAt = DateTimeOffset.Now;
                logger.Warning($"[{prompt.Id}] Agent succeeded but no verification commands were configured; marking UnverifiedSuccess.");
                await runStateStore.SaveJsonAsync(Path.Combine(attemptDirectory, "status.json"), attemptResult, cancellationToken);
                break;
            }

            if (verificationResult.Succeeded)
            {
                attemptResult.Status = RunStatus.Succeeded;
                attemptResult.CompletedAt = DateTimeOffset.Now;
                taskResult.Status = RunStatus.Succeeded;
                taskResult.CompletedAt = DateTimeOffset.Now;
                await runStateStore.SaveJsonAsync(Path.Combine(attemptDirectory, "status.json"), attemptResult, cancellationToken);
                break;
            }

            attemptResult.Status = RunStatus.Failed;
            attemptResult.CompletedAt = DateTimeOffset.Now;
            attemptResult.TimedOut = verificationResult.TimedOut;
            attemptResult.TimeoutReason = verificationResult.TimedOut
                ? $"Verification command timed out after {verificationResult.Timeout?.TotalSeconds:0}s."
                : null;
            taskResult.TimedOut |= verificationResult.TimedOut;
            var failedCommand = verificationResult.Commands.FirstOrDefault(command => !command.Succeeded);
            taskResult.LastFailedVerificationCommand =
                verificationResult.FailedCommand ?? (verificationResult.Unverified ? "(no verification commands)" : null);
            taskResult.LastFailedExitCode = verificationResult.FailedExitCode;
            taskResult.LastFailedLogPath = verificationResult.LogPath;
            taskResult.LastFailureReason = verificationResult.TimedOut
                ? attemptResult.TimeoutReason
                : "Verification failed.";

            activePrompt = RetryPromptBuilder.Build(
                prompt.Prompt,
                failedCommand?.Command ?? taskResult.LastFailedVerificationCommand ?? "verification",
                failedCommand?.ExitCode ?? verificationResult.FailedExitCode ?? -1,
                failedCommand?.CombinedOutput ?? "No verification commands were configured for this prompt.",
                failedCommand?.TimedOut ?? verificationResult.TimedOut,
                failedCommand?.Timeout ?? verificationResult.Timeout);

            await runStateStore.SaveJsonAsync(Path.Combine(attemptDirectory, "status.json"), attemptResult, cancellationToken);
            await runStateStore.SaveJsonAsync(Path.Combine(taskDirectory, "status.json"), taskResult, cancellationToken);
            if (normalAttemptsUsed < maxAttempts)
            {
                resumeCurrentAgentSession = true;
                await PublishRetryAsync(
                    runId,
                    prompt,
                    agentName,
                    invocationNumber,
                    maxAttempts,
                    taskResult.LastFailureReason,
                    cancellationToken);
            }
        }

        if (taskResult.Status is not (
            RunStatus.Succeeded or
            RunStatus.UnverifiedSuccess or
            RunStatus.RateLimited or
            RunStatus.ToolchainFailure or
            RunStatus.Blocked or
            RunStatus.NeedsHumanDecision or
            RunStatus.PrerequisiteMissing or
            RunStatus.Canceled))
        {
            taskResult.Status = RunStatus.NeedsHumanReview;
            taskResult.CompletedAt = DateTimeOffset.Now;
            logger.Warning($"[{prompt.Id}] Needs human review after {taskResult.Attempts.Count} invocation(s).");
        }
        else if (taskResult.Status == RunStatus.RateLimited)
        {
            logger.Warning($"[{prompt.Id}] Rate-limited after {taskResult.Attempts.Count} invocation(s).");
        }
        else if (taskResult.Status == RunStatus.ToolchainFailure)
        {
            logger.Error($"[{prompt.Id}] Toolchain failure; the batch will stop without retrying this invocation.");
        }
        else
        {
            logger.Info($"[{prompt.Id}] {taskResult.Status} after {taskResult.Attempts.Count} invocation(s).");
        }

        taskResult.Agent = taskResult.EffectiveAgent;
        await gitCheckpointManager.SaveDiffAfterAsync(config.RepoPath, taskDirectory, cancellationToken);
        await runStateStore.SaveJsonAsync(Path.Combine(taskDirectory, "status.json"), taskResult, cancellationToken);
        await PublishAsync(
            new RunEvent
            {
                Kind = taskResult.Status == RunStatus.RateLimited
                    ? RunEventKind.TaskRateLimited
                    : IsBlockingAgentOutcomeStatus(taskResult.Status)
                    ? RunEventKind.TaskBlocked
                    : taskResult.Status == RunStatus.ToolchainFailure
                    ? RunEventKind.TaskToolchainFailed
                    : taskResult.Status is RunStatus.Succeeded or RunStatus.UnverifiedSuccess
                    ? RunEventKind.TaskSucceeded
                    : RunEventKind.TaskFailed,
                RunId = runId,
                PromptId = prompt.Id,
                Title = prompt.Title,
                Agent = taskResult.EffectiveAgent,
                BaseAgent = taskResult.BaseAgent,
                EffectiveAgent = taskResult.EffectiveAgent,
                AttemptAgent = taskResult.LatestAttemptAgent,
                RoutingReason = taskResult.RoutingReason,
                AttemptNumber = taskResult.Attempts.LastOrDefault()?.AttemptNumber,
                MaxAttempts = maxAttempts,
                Status = taskResult.Status,
                TimedOut = taskResult.TimedOut,
                FailureReason = taskResult.LastFailureReason,
                RateLimitResetAt = taskResult.RateLimitResetAt,
                RateLimitReason = taskResult.RateLimitReason,
                AgentOutcome = taskResult.AgentOutcome,
                Message = taskResult.Status == RunStatus.RateLimited
                    ? $"Task {prompt.Id} stopped because {taskResult.LatestAttemptAgent ?? taskResult.EffectiveAgent} is rate-limited."
                    : IsBlockingAgentOutcomeStatus(taskResult.Status)
                    ? $"Task {prompt.Id} stopped with explicit outcome {taskResult.Status}."
                    : taskResult.Status == RunStatus.ToolchainFailure
                    ? $"Task {prompt.Id} stopped because the {taskResult.LatestAttemptAgent ?? taskResult.EffectiveAgent} toolchain failed."
                    : taskResult.Status is RunStatus.Succeeded or RunStatus.UnverifiedSuccess
                    ? $"Task {prompt.Id} finished with status {taskResult.Status}."
                    : $"Task {prompt.Id} needs human review.",
                Path = taskDirectory
            },
            cancellationToken);
        return taskResult;
    }

    private static IReadOnlyList<AgentRoutingCandidate> BuildPendingCandidates(
        BatchConfig config,
        IReadOnlyDictionary<string, EffectiveAgentSelection> selectionsByPrompt,
        int startingIndex,
        ISet<string> skipPromptIds)
    {
        return config.Prompts
            .Skip(startingIndex)
            .Where(prompt => !skipPromptIds.Contains(prompt.Id))
            .Select(prompt =>
            {
                var selection = selectionsByPrompt[prompt.Id];
                return new AgentRoutingCandidate
                {
                    PromptId = prompt.Id,
                    BaseAgent = selection.BaseAgent,
                    BaseRoutingReason = selection.BaseRoutingReason
                };
            })
            .ToList();
    }

    private async Task<string?> ApplyQueuedSwitchesAsync(
        BatchConfig config,
        AgentPreflightResult preflight,
        IRunAgentRoutingController routingController,
        IReadOnlyCollection<AgentRoutingCandidate> pendingCandidates,
        RunResult result,
        string runId,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var request in routingController.DequeueSwitches())
        {
            if (_rateLimitStateStore.TryGetBlocked(request.ReplacementAgent, out var blockedInfo))
            {
                return $"Queued agent switch cannot use {request.ReplacementAgent}: " +
                       AgentRateLimitDisplay.BlockedMessage(request.ReplacementAgent, blockedInfo);
            }

            if (!IsPreflighted(preflight, request.ReplacementAgent))
            {
                await PublishAsync(
                    new RunEvent
                    {
                        Kind = RunEventKind.FallbackPreflightStarted,
                        RunId = runId,
                        Agent = request.ReplacementAgent,
                        Message = $"Fallback preflight started: {request.ReplacementAgent}.",
                        Path = runDirectory
                    },
                    cancellationToken);
                var additionalPreflight = await _agentPreflightService.RunAsync(
                    config,
                    [request.ReplacementAgent],
                    config.RepoPath,
                    cancellationToken);
                MergePreflight(preflight, additionalPreflight);
                ApplyPreflightMetadata(config, result, preflight);
                await runStateStore.SaveJsonAsync(
                    Path.Combine(runDirectory, "run-config.normalized.json"),
                    config,
                    cancellationToken);
                if (!additionalPreflight.Succeeded)
                {
                    var reason = additionalPreflight.FailureReason ??
                                 $"Fallback preflight failed for {request.ReplacementAgent}.";
                    await PublishAsync(
                        new RunEvent
                        {
                            Kind = RunEventKind.FallbackPreflightFailed,
                            RunId = runId,
                            Agent = request.ReplacementAgent,
                            Status = RunStatus.ToolchainFailure,
                            FailureReason = reason,
                            Message = $"Fallback preflight failed for {request.ReplacementAgent}: {reason}",
                            Path = runDirectory
                        },
                        cancellationToken);
                    return reason;
                }

                await PublishAsync(
                    new RunEvent
                    {
                        Kind = RunEventKind.FallbackPreflightPassed,
                        RunId = runId,
                        Agent = request.ReplacementAgent,
                        Message = $"Fallback preflight passed: {request.ReplacementAgent}.",
                        Path = preflight.Find(request.ReplacementAgent)?.ExecutablePath
                    },
                    cancellationToken);
            }

            await ApplyRoutingSwitchAsync(
                routingController,
                request,
                pendingCandidates,
                result,
                runId,
                runDirectory,
                request.StartingPromptId ?? pendingCandidates.FirstOrDefault()?.PromptId ?? string.Empty,
                cancellationToken);
        }

        return null;
    }

    private async Task<AgentRoutingChange?> ApplyRoutingSwitchAsync(
        IRunAgentRoutingController routingController,
        AgentSwitchRequest request,
        IReadOnlyCollection<AgentRoutingCandidate> pendingCandidates,
        RunResult result,
        string runId,
        string runDirectory,
        string startingPromptId,
        CancellationToken cancellationToken)
    {
        var change = routingController.ApplySwitch(request, pendingCandidates);
        if (change is null)
        {
            return null;
        }

        SynchronizeRoutingResult(result, routingController, runId);
        foreach (var task in result.Tasks.Where(task =>
                     change.AffectedPromptIds.Contains(task.Id, StringComparer.OrdinalIgnoreCase) &&
                     task.Status is not (RunStatus.Succeeded or RunStatus.UnverifiedSuccess)))
        {
            task.Agent = change.ReplacementAgent;
            task.EffectiveAgent = change.ReplacementAgent;
            task.RoutingReason = change.Reason;
            task.AgentSwitchCount = CountSwitchesForPrompt(result, task.Id);
            if (change.Reason == AgentRoutingReason.RateLimitFallback)
            {
                task.RateLimitedSourceAgent = change.SourceAgent;
                task.FallbackAgent = change.ReplacementAgent;
            }
        }

        await runStateStore.SaveRoutingAsync(
            runDirectory,
            routingController.CreateSnapshot(runId),
            cancellationToken);

        if (change.IsAutomatic)
        {
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.RateLimitFallbackSelected,
                    RunId = runId,
                    PromptId = startingPromptId,
                    Agent = change.ReplacementAgent,
                    EffectiveAgent = change.ReplacementAgent,
                    RoutingReason = change.Reason,
                    SourceAgent = change.SourceAgent,
                    ReplacementAgent = change.ReplacementAgent,
                    AffectedPromptIds = change.AffectedPromptIds,
                    IsAutomaticRoutingChange = true,
                    RateLimitResetAt = change.RateLimitResetAt,
                    Message = $"Rate-limit fallback selected: {change.SourceAgent} to {change.ReplacementAgent}.",
                    Path = runDirectory
                },
                cancellationToken);
        }

        await PublishAsync(
            new RunEvent
            {
                Kind = RunEventKind.AgentSwitchApplied,
                RunId = runId,
                PromptId = startingPromptId,
                Agent = change.ReplacementAgent,
                EffectiveAgent = change.ReplacementAgent,
                RoutingReason = change.Reason,
                SourceAgent = change.SourceAgent,
                ReplacementAgent = change.ReplacementAgent,
                AffectedPromptIds = change.AffectedPromptIds,
                IsAutomaticRoutingChange = change.IsAutomatic,
                RateLimitResetAt = change.RateLimitResetAt,
                Message = $"Agent switch applied: {change.SourceAgent} to {change.ReplacementAgent} for {change.AffectedPromptIds.Count} pending prompt(s).",
                Path = runDirectory
            },
            cancellationToken);

        foreach (var affectedPromptId in change.AffectedPromptIds)
        {
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.PendingPromptRerouted,
                    RunId = runId,
                    PromptId = affectedPromptId,
                    Agent = change.ReplacementAgent,
                    EffectiveAgent = change.ReplacementAgent,
                    RoutingReason = change.Reason,
                    SourceAgent = change.SourceAgent,
                    ReplacementAgent = change.ReplacementAgent,
                    IsAutomaticRoutingChange = change.IsAutomatic,
                    Message = $"Pending prompt {affectedPromptId} rerouted from {change.SourceAgent} to {change.ReplacementAgent}.",
                    Path = runDirectory
                },
                cancellationToken);
        }

        return change;
    }

    private bool TrySelectFallback(
        BatchConfig config,
        string sourceAgent,
        IEnumerable<string> excludedAgents,
        AgentPreflightResult preflight,
        out string fallbackAgent)
    {
        var excluded = excludedAgents
            .Where(agent => !string.IsNullOrWhiteSpace(agent))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        excluded.Add(sourceAgent);

        foreach (var candidate in _fallbackPolicy.GetFallbacks(config, sourceAgent))
        {
            if (excluded.Contains(candidate) ||
                _rateLimitStateStore.TryGetBlocked(candidate, out _) ||
                !IsPreflighted(preflight, candidate))
            {
                continue;
            }

            fallbackAgent = candidate;
            return true;
        }

        fallbackAgent = string.Empty;
        return false;
    }

    private static bool IsPreflighted(AgentPreflightResult preflight, string agentName)
    {
        if (!preflight.Succeeded)
        {
            return false;
        }

        if (preflight.Toolchains.Count == 0)
        {
            return true;
        }

        return preflight.Find(agentName)?.Status is
            AgentPreflightStatus.Succeeded or AgentPreflightStatus.NotRequired;
    }

    private static void MergePreflight(AgentPreflightResult target, AgentPreflightResult additional)
    {
        foreach (var toolchain in additional.Toolchains)
        {
            target.Toolchains.RemoveAll(existing =>
                string.Equals(existing.AgentName, toolchain.AgentName, StringComparison.OrdinalIgnoreCase));
            target.Toolchains.Add(toolchain);
        }

        target.Succeeded = target.Succeeded && additional.Succeeded;
        if (!additional.Succeeded)
        {
            target.FailureReason = additional.FailureReason;
        }
    }

    private static int CountSwitchesForPrompt(RunResult result, string promptId)
    {
        return result.RoutingChanges.Count(change =>
            change.AffectedPromptIds.Contains(promptId, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsBlockingAgentOutcomeStatus(RunStatus status)
    {
        return status is
            RunStatus.Blocked or
            RunStatus.NeedsHumanDecision or
            RunStatus.PrerequisiteMissing or
            RunStatus.Canceled;
    }

    private static RunStatus MapAgentOutcomeStatus(AgentOutcome outcome)
    {
        return outcome switch
        {
            AgentOutcome.Succeeded => RunStatus.Running,
            AgentOutcome.Blocked => RunStatus.Blocked,
            AgentOutcome.NeedsHumanDecision => RunStatus.NeedsHumanDecision,
            AgentOutcome.PrerequisiteMissing => RunStatus.PrerequisiteMissing,
            AgentOutcome.Failed => RunStatus.Failed,
            AgentOutcome.RateLimited => RunStatus.RateLimited,
            AgentOutcome.Canceled => RunStatus.Canceled,
            AgentOutcome.TimedOut => RunStatus.TimedOut,
            _ => RunStatus.Running
        };
    }

    private void ApplyStructuredRateLimit(string agentName, AgentExecutionResult agentResult)
    {
        if (agentResult.IsRateLimited || agentResult.Outcome?.AgentOutcome != AgentOutcome.RateLimited)
        {
            return;
        }

        var reason = agentResult.Outcome.Blocker ?? "Agent reported a rate limit.";
        var info = new AgentRateLimitInfo
        {
            AgentName = agentName,
            IsBlocked = true,
            LastDetectedAt = _rateLimitStateStore.Now,
            Reason = reason,
            RawMessage = agentResult.Outcome.RawMessage
        };
        _rateLimitStateStore.SetBlocked(info);
        agentResult.IsRateLimited = true;
        agentResult.RateLimitReason = reason;
    }

    private static void SynchronizeRoutingResult(
        RunResult result,
        IRunAgentRoutingController routingController,
        string runId)
    {
        result.RoutingChanges = routingController.CreateSnapshot(runId).Changes;
    }

    private static string BuildAgentResultMessage(string agentName, AgentExecutionResult agentResult)
    {
        if (agentResult.IsRateLimited)
        {
            return AgentRateLimitDisplay.BlockedMessage(agentName, new AgentRateLimitInfo
            {
                AgentName = agentName,
                IsBlocked = true,
                BlockedUntil = agentResult.RateLimitResetAt,
                Reason = agentResult.RateLimitReason ?? string.Empty
            });
        }

        if (agentResult.IsToolchainFailure)
        {
            return agentResult.ToolchainFailureReason ?? "Agent toolchain failed.";
        }

        if (agentResult.TimedOut)
        {
            return $"Agent timed out after {agentResult.Timeout?.TotalSeconds:0}s.";
        }

        return agentResult.Succeeded
            ? "Agent completed."
            : $"Agent failed with exit code {agentResult.ExitCode}.";
    }

    private Task PublishRetryAsync(
        string runId,
        PromptTask prompt,
        string agentName,
        int nextInvocationNumber,
        int maxAttempts,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        return PublishAsync(
            new RunEvent
            {
                Kind = RunEventKind.RetryStarted,
                RunId = runId,
                PromptId = prompt.Id,
                Title = prompt.Title,
                Agent = agentName,
                AttemptAgent = agentName,
                AttemptNumber = nextInvocationNumber,
                MaxAttempts = maxAttempts,
                Status = RunStatus.Running,
                Message = $"Retry invocation {nextInvocationNumber} will use failure feedback with {agentName}.",
                FailureReason = failureReason
            },
            cancellationToken);
    }

    private Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        return (runEventSink ?? NullRunEventSink.Instance).OnRunEventAsync(runEvent, cancellationToken);
    }

    private static void ApplyPreflightMetadata(
        BatchConfig config,
        RunResult result,
        AgentPreflightResult preflight)
    {
        config.ResolvedAgentToolchains = preflight.Toolchains;
        result.Toolchains = preflight.Toolchains;

        foreach (var toolchain in preflight.Toolchains.Where(item =>
                     item.Status == AgentPreflightStatus.Succeeded &&
                     !string.IsNullOrWhiteSpace(item.ExecutablePath)))
        {
            if (string.Equals(toolchain.AgentName, "codex", StringComparison.OrdinalIgnoreCase))
            {
                config.CodexExecutablePath = toolchain.ExecutablePath;
            }
            else if (string.Equals(toolchain.AgentName, "claude", StringComparison.OrdinalIgnoreCase))
            {
                config.ClaudeExecutablePath = toolchain.ExecutablePath;
            }
        }
    }

    private async Task AddSkippedTasksAsync(
        RunResult result,
        BatchConfig config,
        IEnumerable<EffectiveAgentSelection> selections,
        string runId,
        string runDirectory,
        string failureReason,
        CancellationToken cancellationToken)
    {
        foreach (var selection in selections)
        {
            var prompt = config.Prompts.First(item =>
                string.Equals(item.Id, selection.PromptId, StringComparison.OrdinalIgnoreCase));
            if (result.Tasks.Any(task =>
                    string.Equals(task.Id, selection.PromptId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var now = DateTimeOffset.Now;
            result.Tasks.Add(new TaskRunResult
            {
                Id = prompt.Id,
                Title = prompt.Title,
                Agent = selection.EffectiveAgent,
                BaseAgent = selection.BaseAgent,
                EffectiveAgent = selection.EffectiveAgent,
                RoutingReason = selection.BaseRoutingReason,
                ConfiguredAgent = selection.ConfiguredAgent,
                DefaultAgent = selection.DefaultAgent,
                AgentOverride = selection.RunOverride,
                Status = RunStatus.Skipped,
                StartedAt = now,
                CompletedAt = now,
                TaskDirectory = Path.Combine(runDirectory, "tasks", FileNameSanitizer.Sanitize(prompt.Id)),
                LastFailureReason = $"Not started because the run was blocked: {failureReason}"
            });

            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.TaskSkipped,
                    RunId = runId,
                    PromptId = prompt.Id,
                    Title = prompt.Title,
                    Agent = selection.EffectiveAgent,
                    Status = RunStatus.Skipped,
                    FailureReason = failureReason,
                    Message = $"Task {prompt.Id} was not started because the run was blocked."
                },
                cancellationToken);
        }
    }

    private async Task<string> PublishReportGeneratedAsync(
        string runId,
        string runDirectory,
        CancellationToken cancellationToken)
    {
        var reportPath = Path.Combine(runDirectory, "final-report.md");
        await PublishAsync(
            new RunEvent
            {
                Kind = RunEventKind.ReportGenerated,
                RunId = runId,
                Message = $"Final report generated: {reportPath}",
                Path = reportPath
            },
            cancellationToken);
        return reportPath;
    }

    private void ApplyDetectedRateLimit(string agentName, AgentExecutionResult agentResult)
    {
        var rateLimitInfo = _rateLimitDetector.Detect(
            agentName,
            agentResult.StandardOutput,
            agentResult.StandardError,
            _rateLimitStateStore.Now);
        if (rateLimitInfo is null)
        {
            return;
        }

        _rateLimitStateStore.SetBlocked(rateLimitInfo);
        agentResult.IsRateLimited = true;
        agentResult.RateLimitResetAt = rateLimitInfo.BlockedUntil;
        agentResult.RateLimitReason = rateLimitInfo.Reason;
    }

    private static AgentExecutionResult CreateRateLimitedAgentResult(
        string agentName,
        AgentRateLimitInfo rateLimitInfo)
    {
        var message = AgentRateLimitDisplay.BlockedMessage(agentName, rateLimitInfo);
        return new AgentExecutionResult
        {
            AgentName = agentName,
            Command = $"{agentName} (rate-limit guard)",
            ExitCode = 75,
            Duration = TimeSpan.Zero,
            IsRateLimited = true,
            RateLimitResetAt = rateLimitInfo.BlockedUntil,
            RateLimitReason = rateLimitInfo.Reason,
            StandardOutput = string.Empty,
            StandardError = message
        };
    }

    private static async Task WriteAgentOutputAsync(
        string attemptDirectory,
        string workingDirectory,
        AgentExecutionResult agentResult,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(attemptDirectory, "agent-output.txt");
        var content =
            $"""
            Command: {agentResult.Command}
            Working directory: {workingDirectory}
            Exit code: {agentResult.ExitCode}
            Duration: {agentResult.Duration}
            Timed out: {agentResult.TimedOut}
            Timeout: {(agentResult.Timeout.HasValue ? $"{agentResult.Timeout.Value.TotalSeconds:0}s" : "(none)")}
            Rate limited: {agentResult.IsRateLimited}
            Rate limit reset at: {(agentResult.RateLimitResetAt.HasValue ? agentResult.RateLimitResetAt.Value.ToString("O") : "(unknown)")}
            Rate limit reason: {agentResult.RateLimitReason ?? "(none)"}
            Toolchain failure: {agentResult.IsToolchainFailure}
            Toolchain failure reason: {agentResult.ToolchainFailureReason ?? "(none)"}
            Session id: {agentResult.SessionId ?? "(none)"}

            STDOUT
            {agentResult.StandardOutput}

            STDERR
            {agentResult.StandardError}
            """;

        await Utf8File.WriteAllTextAsync(path, SensitiveDataRedactor.Redact(content), cancellationToken);
    }
}
