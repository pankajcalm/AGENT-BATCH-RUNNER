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
    AgentRateLimitStateStore? rateLimitStateStore = null)
{
    private readonly AgentRateLimitDetector _rateLimitDetector = rateLimitDetector ?? new AgentRateLimitDetector();
    private readonly AgentRateLimitStateStore _rateLimitStateStore = rateLimitStateStore ?? new AgentRateLimitStateStore();

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

        if (!string.IsNullOrWhiteSpace(options.AgentOverride) &&
            !AgentAdapterFactory.IsSupportedAgent(options.AgentOverride))
        {
            throw new InvalidOperationException($"Agent override '{options.AgentOverride}' is not supported. Use claude, codex, or dryrun.");
        }

        var runId = options.RunId ?? DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var runDirectory = runStateStore.CreateRunDirectory(config.RepoPath, runId);
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

        await runStateStore.SaveJsonAsync(
            Path.Combine(runDirectory, "run-config.normalized.json"),
            config,
            cancellationToken);

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

        foreach (var prompt in config.Prompts)
        {
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.TaskPending,
                    RunId = runId,
                    PromptId = prompt.Id,
                    Title = prompt.Title,
                    Agent = options.AgentOverride ?? prompt.Agent ?? config.DefaultAgent,
                    MaxAttempts = prompt.MaxRetries ?? config.DefaultMaxRetries,
                    Status = RunStatus.Pending,
                    Message = $"Task {prompt.Id} is pending."
                },
                cancellationToken);
        }

        try
        {
            foreach (var prompt in config.Prompts)
            {
                if (options.SkipPromptIds.Contains(prompt.Id))
                {
                    logger.Info($"Skipping {prompt.Id}; previous result succeeded.");
                    continue;
                }

                result.Tasks.RemoveAll(t => string.Equals(t.Id, prompt.Id, StringComparison.OrdinalIgnoreCase));
                var taskResult = await RunPromptTaskAsync(config, prompt, runId, runDirectory, options, cancellationToken);
                result.Tasks.Add(taskResult);

                await runStateStore.SaveJsonAsync(
                    Path.Combine(runDirectory, "run-summary.json"),
                    result,
                    cancellationToken);

                if (taskResult.Status == RunStatus.RateLimited)
                {
                    logger.Warning($"Run {runId} stopped because {taskResult.Agent} is rate-limited.");
                    break;
                }
            }

            result.CompletedAt = DateTimeOffset.Now;
            await reportGenerator.GenerateAsync(runDirectory, result, cancellationToken);
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
            await PublishAsync(
                new RunEvent
                {
                    Kind = result.RateLimited > 0 ? RunEventKind.RunRateLimited : RunEventKind.RunCompleted,
                    RunId = runId,
                    Status = result.RateLimited > 0 ? RunStatus.RateLimited : null,
                    Message = result.RateLimited > 0
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
        string runId,
        string runDirectory,
        RunOptions options,
        CancellationToken cancellationToken)
    {
        gitCheckpointManager.EnsureRepository(config.RepoPath);

        var agentName = (options.AgentOverride ?? prompt.Agent ?? config.DefaultAgent).Trim().ToLowerInvariant();
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
        var taskDirectory = Path.Combine(runDirectory, "tasks", FileNameSanitizer.Sanitize(prompt.Id));
        Directory.CreateDirectory(taskDirectory);

        var taskResult = new TaskRunResult
        {
            Id = prompt.Id,
            Title = prompt.Title,
            Agent = agentName,
            Status = RunStatus.Running,
            StartedAt = DateTimeOffset.Now,
            TaskDirectory = taskDirectory
        };

        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "prompt.md"), prompt.Prompt, cancellationToken);
        await runStateStore.SaveJsonAsync(Path.Combine(taskDirectory, "status.json"), taskResult, cancellationToken);

        logger.Info($"[{prompt.Id}] Starting '{prompt.Title}' with {agentName}; max attempts: {maxAttempts}.");
        await PublishAsync(
            new RunEvent
            {
                Kind = RunEventKind.TaskStarted,
                RunId = runId,
                PromptId = prompt.Id,
                Title = prompt.Title,
                Agent = agentName,
                MaxAttempts = maxAttempts,
                Status = RunStatus.Running,
                Message = $"Task {prompt.Id} started with {agentName}.",
                Path = taskDirectory
            },
            cancellationToken);
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
                MaxAttempts = maxAttempts,
                Status = RunStatus.Running,
                Message = $"Checkpoint branch created: {taskResult.CheckpointId}",
                Path = taskResult.CheckpointId
            },
            cancellationToken);

        var adapter = agentAdapterFactory.Create(agentName);
        string? sessionId = null;
        var activePrompt = prompt.Prompt;

        for (var attemptNumber = 1; attemptNumber <= maxAttempts; attemptNumber++)
        {
            var attemptDirectory = Path.Combine(taskDirectory, "attempts", $"attempt-{attemptNumber}");
            Directory.CreateDirectory(attemptDirectory);

            var attemptResult = new AttemptResult
            {
                AttemptNumber = attemptNumber,
                AttemptDirectory = attemptDirectory,
                Status = RunStatus.Running,
                StartedAt = DateTimeOffset.Now
            };
            taskResult.Attempts.Add(attemptResult);

            logger.Info($"[{prompt.Id}] Attempt {attemptNumber}/{maxAttempts}.");
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.AttemptStarted,
                    RunId = runId,
                    PromptId = prompt.Id,
                    Title = prompt.Title,
                    Agent = agentName,
                    AttemptNumber = attemptNumber,
                    MaxAttempts = maxAttempts,
                    Status = RunStatus.Running,
                    Message = $"Attempt {attemptNumber}/{maxAttempts} started.",
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
                        AttemptNumber = attemptNumber,
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
                        AttemptNumber = attemptNumber,
                        SessionId = sessionId,
                        AttemptDirectory = attemptDirectory,
                        Options = agentOptions
                    },
                    cancellationToken);

                ApplyDetectedRateLimit(agentName, agentResult);
            }

            attemptResult.AgentResult = agentResult;
            sessionId = agentResult.SessionId ?? sessionId;

            await WriteAgentOutputAsync(attemptDirectory, config.RepoPath, agentResult, cancellationToken);
            await PublishAsync(
                new RunEvent
                {
                    Kind = agentResult.IsRateLimited
                        ? RunEventKind.AgentRateLimited
                        : agentResult.TimedOut
                        ? RunEventKind.AgentTimedOut
                        : agentResult.Succeeded
                            ? RunEventKind.AgentCompleted
                            : RunEventKind.AgentFailed,
                    RunId = runId,
                    PromptId = prompt.Id,
                    Title = prompt.Title,
                    Agent = agentName,
                    AttemptNumber = attemptNumber,
                    MaxAttempts = maxAttempts,
                    Status = agentResult.IsRateLimited
                        ? RunStatus.RateLimited
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
                    Message = agentResult.IsRateLimited
                        ? AgentRateLimitDisplay.BlockedMessage(agentName, new AgentRateLimitInfo
                        {
                            AgentName = agentName,
                            IsBlocked = true,
                            BlockedUntil = agentResult.RateLimitResetAt,
                            Reason = agentResult.RateLimitReason ?? string.Empty
                        })
                        : agentResult.TimedOut
                        ? $"Agent timed out after {agentResult.Timeout?.TotalSeconds:0}s."
                        : agentResult.Succeeded
                            ? "Agent completed."
                            : $"Agent failed with exit code {agentResult.ExitCode}.",
                    Path = Path.Combine(attemptDirectory, "agent-output.txt")
                },
                cancellationToken);

            if (agentResult.IsRateLimited)
            {
                var rateLimitMessage = AgentRateLimitDisplay.BlockedMessage(agentName, new AgentRateLimitInfo
                {
                    AgentName = agentName,
                    IsBlocked = true,
                    BlockedUntil = agentResult.RateLimitResetAt,
                    Reason = agentResult.RateLimitReason ?? string.Empty
                });
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
                if (attemptNumber < maxAttempts)
                {
                    await PublishAsync(
                        new RunEvent
                        {
                            Kind = RunEventKind.RetryStarted,
                            RunId = runId,
                            PromptId = prompt.Id,
                            Title = prompt.Title,
                            Agent = agentName,
                            AttemptNumber = attemptNumber + 1,
                            MaxAttempts = maxAttempts,
                            Status = RunStatus.Running,
                            Message = $"Retry {attemptNumber + 1}/{maxAttempts} will use failure feedback.",
                            FailureReason = taskResult.LastFailureReason
                        },
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
                attemptNumber,
                maxAttempts);
            attemptResult.VerificationResult = verificationResult;

            if (verificationResult.Unverified)
            {
                // The agent succeeded but there were no verification commands to confirm it.
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
            var failedCommand = verificationResult.Commands.FirstOrDefault(c => !c.Succeeded);
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
            if (attemptNumber < maxAttempts)
            {
                await PublishAsync(
                    new RunEvent
                    {
                        Kind = RunEventKind.RetryStarted,
                        RunId = runId,
                        PromptId = prompt.Id,
                        Title = prompt.Title,
                        Agent = agentName,
                        AttemptNumber = attemptNumber + 1,
                        MaxAttempts = maxAttempts,
                        Status = RunStatus.Running,
                        Message = $"Retry {attemptNumber + 1}/{maxAttempts} will use failure feedback.",
                        FailureReason = taskResult.LastFailureReason
                    },
                    cancellationToken);
            }
        }

        if (taskResult.Status is not (RunStatus.Succeeded or RunStatus.UnverifiedSuccess or RunStatus.RateLimited))
        {
            taskResult.Status = RunStatus.NeedsHumanReview;
            taskResult.CompletedAt = DateTimeOffset.Now;
            logger.Warning($"[{prompt.Id}] Needs human review after {taskResult.Attempts.Count} attempt(s).");
        }
        else if (taskResult.Status == RunStatus.RateLimited)
        {
            logger.Warning($"[{prompt.Id}] Rate-limited after {taskResult.Attempts.Count} attempt(s).");
        }
        else
        {
            logger.Info($"[{prompt.Id}] {taskResult.Status} after {taskResult.Attempts.Count} attempt(s).");
        }

        await gitCheckpointManager.SaveDiffAfterAsync(config.RepoPath, taskDirectory, cancellationToken);
        await runStateStore.SaveJsonAsync(Path.Combine(taskDirectory, "status.json"), taskResult, cancellationToken);
        await PublishAsync(
            new RunEvent
            {
                Kind = taskResult.Status == RunStatus.RateLimited
                    ? RunEventKind.TaskRateLimited
                    : taskResult.Status is RunStatus.Succeeded or RunStatus.UnverifiedSuccess
                    ? RunEventKind.TaskSucceeded
                    : RunEventKind.TaskFailed,
                RunId = runId,
                PromptId = prompt.Id,
                Title = prompt.Title,
                Agent = agentName,
                AttemptNumber = taskResult.Attempts.Count,
                MaxAttempts = maxAttempts,
                Status = taskResult.Status,
                TimedOut = taskResult.TimedOut,
                FailureReason = taskResult.LastFailureReason,
                RateLimitResetAt = taskResult.RateLimitResetAt,
                RateLimitReason = taskResult.RateLimitReason,
                Message = taskResult.Status == RunStatus.RateLimited
                    ? $"Task {prompt.Id} stopped because {agentName} is rate-limited."
                    : taskResult.Status is RunStatus.Succeeded or RunStatus.UnverifiedSuccess
                    ? $"Task {prompt.Id} finished with status {taskResult.Status}."
                    : $"Task {prompt.Id} needs human review.",
                Path = taskDirectory
            },
            cancellationToken);
        return taskResult;
    }

    private Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        return (runEventSink ?? NullRunEventSink.Instance).OnRunEventAsync(runEvent, cancellationToken);
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
            Session id: {agentResult.SessionId ?? "(none)"}

            STDOUT
            {agentResult.StandardOutput}

            STDERR
            {agentResult.StandardError}
            """;

        await File.WriteAllTextAsync(path, SensitiveDataRedactor.Redact(content), cancellationToken);
    }
}
