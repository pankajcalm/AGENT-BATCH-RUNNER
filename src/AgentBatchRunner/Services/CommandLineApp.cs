using System.Globalization;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class CommandLineApp(
    PromptFileLoader promptFileLoader,
    BatchRunner batchRunner,
    RunStateStore runStateStore,
    ReportGenerator reportGenerator,
    AgentRateLimitStateStore rateLimitStateStore,
    ConsoleLogger logger,
    Func<bool>? isElevated = null,
    EffectiveAgentPolicy? effectiveAgentPolicy = null,
    RateLimitFallbackPolicy? fallbackPolicy = null,
    IPipelineFolderRunner? pipelineRunner = null,
    PipelineStateStore? pipelineStateStore = null,
    PipelineReportGenerator? pipelineReportGenerator = null)
{
    private readonly EffectiveAgentPolicy _effectiveAgentPolicy = effectiveAgentPolicy ?? new EffectiveAgentPolicy();
    private readonly RateLimitFallbackPolicy _fallbackPolicy = fallbackPolicy ?? new RateLimitFallbackPolicy();
    private readonly PipelineStateStore _pipelineStateStore = pipelineStateStore ?? new PipelineStateStore();
    private readonly PipelineReportGenerator _pipelineReportGenerator = pipelineReportGenerator ??
        new PipelineReportGenerator(pipelineStateStore ?? new PipelineStateStore());

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0].ToLowerInvariant();
        try
        {
            if ((isElevated ?? PrivilegeGuard.IsElevated)())
            {
                logger.Error("AgentBatchRunner refuses to run with administrator/root privileges.");
                return 1;
            }

            return command switch
            {
                "validate" => await ValidateAsync(args, cancellationToken),
                "run" => await RunBatchAsync(args, cancellationToken),
                "resume" => await ResumeAsync(args, cancellationToken),
                "report" => await ReportAsync(args, cancellationToken),
                "limits" => HandleLimits(args),
                "folder" => await HandleFolderAsync(args, cancellationToken),
                _ => UnknownCommand(command)
            };
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Operation canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            logger.Error(ex.Message);
            return 1;
        }
    }

    private async Task<int> ValidateAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            logger.Error("Missing YAML path.");
            PrintUsage();
            return 1;
        }

        var config = await promptFileLoader.LoadAsync(args[1], cancellationToken);
        var validation = promptFileLoader.Validate(config);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                logger.Error(error);
            }

            return 1;
        }

        logger.Info("Prompt file is valid.");
        return 0;
    }

    private async Task<int> RunBatchAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            logger.Error("Missing YAML path.");
            PrintUsage();
            return 1;
        }

        var agentOverride = TryReadOption(args, "--agent");
        var config = await promptFileLoader.LoadAsync(args[1], cancellationToken);
        var validation = promptFileLoader.Validate(config);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                logger.Error(error);
            }

            return 1;
        }

        var blockedExitCode = CheckBlockedAgents(config, agentOverride);
        if (blockedExitCode != 0)
        {
            return blockedExitCode;
        }

        var result = await batchRunner.RunAsync(
            config,
            new RunOptions { AgentOverride = agentOverride },
            cancellationToken);

        return result.FailureKind != RunFailureKind.None
            ? 4
            : result.RateLimited > 0
            ? 3
            : result.NeedsHumanReview == 0 && result.Failed == 0 ? 0 : 2;
    }

    private async Task<int> ResumeAsync(string[] args, CancellationToken cancellationToken)
    {
        var agentOverride = TryReadOption(args, "--agent");
        var runDirectory = ResolveRunDirectory(args);
        if (runDirectory is null)
        {
            return 1;
        }

        var config = await runStateStore.LoadConfigAsync(runDirectory, cancellationToken);
        agentOverride ??= config.RunAgentOverride;
        var previousResult = await runStateStore.LoadRunResultAsync(runDirectory, cancellationToken);
        var routingController = new RunAgentRoutingController(
            await runStateStore.LoadRoutingAsync(runDirectory, cancellationToken));
        var succeededPromptIds = previousResult.Tasks
            .Where(t => t.Status is RunStatus.Succeeded or RunStatus.UnverifiedSuccess)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var blockedExitCode = CheckBlockedAgents(
            config,
            agentOverride,
            succeededPromptIds,
            routingController);
        if (blockedExitCode != 0)
        {
            return blockedExitCode;
        }

        logger.Info($"Resuming run {previousResult.RunId}. Succeeded tasks will be skipped.");
        var result = await batchRunner.RunAsync(
            config,
            new RunOptions
            {
                RunId = previousResult.RunId,
                AgentOverride = agentOverride,
                ExistingResult = previousResult,
                SkipPromptIds = succeededPromptIds,
                RoutingController = routingController
            },
            cancellationToken);

        return result.FailureKind != RunFailureKind.None
            ? 4
            : result.RateLimited > 0
            ? 3
            : result.NeedsHumanReview == 0 && result.Failed == 0 ? 0 : 2;
    }

    private int HandleLimits(string[] args)
    {
        if (args.Length < 2)
        {
            return ShowLimits();
        }

        var subcommand = args[1].ToLowerInvariant();
        return subcommand switch
        {
            "set" => SetLimit(args),
            "clear" => ClearLimit(args),
            "show" or "list" => ShowLimits(),
            _ => UnknownLimitsSubcommand(subcommand)
        };
    }

    private async Task<int> HandleFolderAsync(string[] args, CancellationToken cancellationToken)
    {
        if (pipelineRunner is null)
        {
            logger.Error("Folder pipeline services are not configured.");
            return 1;
        }

        if (args.Length < 2 || IsHelp(args[1]))
        {
            PrintFolderUsage();
            return args.Length < 2 ? 1 : 0;
        }

        return args[1].ToLowerInvariant() switch
        {
            "validate" => await ValidateFolderAsync(args, cancellationToken),
            "plan" => await PlanFolderAsync(args, cancellationToken),
            "run" => await RunFolderAsync(args, cancellationToken),
            "run-next" => await RunNextFolderAsync(args, cancellationToken),
            "resume" => await ResumeFolderAsync(args, cancellationToken),
            "status" => await StatusFolderAsync(args, cancellationToken),
            "report" => await ReportFolderAsync(args, cancellationToken),
            "skip" => await SkipFolderFileAsync(args, cancellationToken),
            "complete-manually" => await CompleteFolderFileManuallyAsync(args, cancellationToken),
            "start-from" => await StartFolderFromFileAsync(args, cancellationToken),
            "undo-status" => await UndoFolderFileStatusAsync(args, cancellationToken),
            _ => UnknownFolderSubcommand(args[1])
        };
    }

    private async Task<int> ValidateFolderAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryReadFolderPath(args, out var folderPath))
        {
            return 1;
        }

        var plan = await pipelineRunner!.PlanAsync(folderPath, cancellationToken);
        foreach (var warning in plan.Warnings)
        {
            logger.Warning(warning);
        }

        if (!plan.IsValid)
        {
            foreach (var error in plan.Errors)
            {
                logger.Error(error);
            }

            return 1;
        }

        logger.Info($"Pipeline folder is valid: {plan.Files.Count} eligible file(s).");
        return 0;
    }

    private async Task<int> PlanFolderAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryReadFolderPath(args, out var folderPath))
        {
            return 1;
        }

        var plan = await pipelineRunner!.PlanAsync(folderPath, cancellationToken);
        if (!plan.IsValid)
        {
            foreach (var error in plan.Errors)
            {
                logger.Error(error);
            }

            return 1;
        }

        Console.WriteLine("Order | Pipeline ID | Phase | File | Dependencies | Review");
        foreach (var file in plan.Files.Select((file, index) => (file, index)))
        {
            Console.WriteLine(
                $"{file.index + 1,5} | {file.file.PipelineId} | {file.file.Phase} | " +
                $"{file.file.RelativePath} | {string.Join(",", file.file.Dependencies)} | " +
                $"{(file.file.Metadata?.Review.Required == true ? file.file.Metadata.Review.Agent ?? "from YAML" : "optional")}");
        }

        return 0;
    }

    private async Task<int> RunFolderAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryReadFolderPath(args, out var folderPath) ||
            !TryParsePipelineOptions(args, out var options))
        {
            return 1;
        }

        var state = await pipelineRunner!.CreateAsync(folderPath, options, cancellationToken);
        var selectedFile = TryReadOption(args, "--file");
        state = await pipelineRunner.RunPipelineAsync(state, selectedFile, cancellationToken: cancellationToken);
        PrintPipelineStatus(state);
        return PipelineExitCode(state);
    }

    private async Task<int> RunNextFolderAsync(string[] args, CancellationToken cancellationToken)
    {
        var directory = ResolvePipelineDirectory(args);
        if (directory is null)
        {
            return 1;
        }

        var state = await pipelineRunner!.ResumeAsync(directory, cancellationToken);
        state = await pipelineRunner.RunRecommendedNextAsync(
            state,
            userConfirmed: true,
            cancellationToken: cancellationToken);
        PrintPipelineStatus(state);
        return PipelineExitCode(state);
    }

    private async Task<int> ResumeFolderAsync(string[] args, CancellationToken cancellationToken)
    {
        var directory = ResolvePipelineDirectory(args);
        if (directory is null)
        {
            return 1;
        }

        var state = await pipelineRunner!.ResumeAsync(directory, cancellationToken);
        if (state.Status == PipelineRunStatus.Paused && state.NextDecision?.FilePath is not null)
        {
            state = await pipelineRunner.RunRecommendedNextAsync(
                state,
                userConfirmed: true,
                cancellationToken: cancellationToken);
        }
        else if (state.Files.Any(file => file.Status is PipelineFileStatus.Pending or PipelineFileStatus.Eligible) &&
                 state.Status is not (
                     PipelineRunStatus.Blocked or
                     PipelineRunStatus.NeedsHumanDecision or
                     PipelineRunStatus.RateLimited or
                     PipelineRunStatus.Canceled or
                     PipelineRunStatus.Failed))
        {
            state = await pipelineRunner.RunPipelineAsync(state, cancellationToken: cancellationToken);
        }

        PrintPipelineStatus(state);
        return PipelineExitCode(state);
    }

    private async Task<int> StatusFolderAsync(string[] args, CancellationToken cancellationToken)
    {
        var directory = ResolvePipelineDirectory(args);
        if (directory is null)
        {
            return 1;
        }

        var state = await _pipelineStateStore.LoadStateAsync(directory, cancellationToken);
        PrintPipelineStatus(state);
        foreach (var file in state.Files.OrderBy(file => file.QueueOrder))
        {
            Console.WriteLine(
                $"{file.QueueOrder,3} {file.PipelineId,-20} {file.Status,-24} " +
                $"execution={file.ExecutionStatus?.ToString() ?? "pending"} review={file.ReviewVerdict?.ToString() ?? "pending"}");
        }

        return PipelineExitCode(state);
    }

    private async Task<int> ReportFolderAsync(string[] args, CancellationToken cancellationToken)
    {
        var directory = ResolvePipelineDirectory(args);
        if (directory is null)
        {
            return 1;
        }

        var state = await _pipelineStateStore.LoadStateAsync(directory, cancellationToken);
        await _pipelineReportGenerator.GenerateAsync(state, cancellationToken);
        var reportPath = Path.Combine(directory, "pipeline-report.md");
        logger.Info($"Pipeline report generated: {reportPath}");
        Console.WriteLine(await Utf8File.ReadAllTextAsync(reportPath, cancellationToken));
        return 0;
    }

    private async Task<int> SkipFolderFileAsync(string[] args, CancellationToken cancellationToken)
    {
        var directory = ResolvePipelineDirectory(args);
        if (directory is null || !TryReadRequiredOption(args, "--file", out var fileReference) ||
            !TryReadRequiredOption(args, "--reason", out var reason))
        {
            return 1;
        }

        var state = await _pipelineStateStore.LoadStateAsync(directory, cancellationToken);
        state = await pipelineRunner!.SkipAsync(
            state,
            fileReference,
            new PipelineManualActionRequest
            {
                Reason = reason,
                EvidencePath = TryReadOption(args, "--evidence"),
                Notes = TryReadOption(args, "--notes"),
                Actor = Environment.UserName,
                OverrideSource = "CLI folder skip"
            },
            cancellationToken);
        PrintPipelineStatus(state);
        return PipelineExitCode(state);
    }

    private async Task<int> CompleteFolderFileManuallyAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var directory = ResolvePipelineDirectory(args);
        if (directory is null || !TryReadRequiredOption(args, "--file", out var fileReference) ||
            !TryReadRequiredOption(args, "--reason", out var reason))
        {
            return 1;
        }

        var approveGate = HasOption(args, "--approve-gate");
        var evidence = TryReadOption(args, "--evidence");
        var overrideSource = TryReadOption(args, "--override-source");
        if (approveGate &&
            (!HasOption(args, "--confirm-gate-override") ||
             string.IsNullOrWhiteSpace(evidence) ||
             string.IsNullOrWhiteSpace(overrideSource)))
        {
            logger.Error(
                "--approve-gate requires --confirm-gate-override, --evidence, and --override-source so the override is explicit and auditable.");
            return 1;
        }

        var state = await _pipelineStateStore.LoadStateAsync(directory, cancellationToken);
        state = await pipelineRunner!.CompleteManuallyAsync(
            state,
            fileReference,
            new PipelineManualActionRequest
            {
                Reason = reason,
                EvidencePath = evidence,
                Notes = TryReadOption(args, "--notes"),
                SatisfiesDependencies = HasOption(args, "--satisfy-dependencies"),
                GateApproved = approveGate,
                Actor = Environment.UserName,
                OverrideSource = string.IsNullOrWhiteSpace(overrideSource)
                    ? "CLI folder complete-manually"
                    : overrideSource
            },
            cancellationToken);
        PrintPipelineStatus(state);
        return PipelineExitCode(state);
    }

    private async Task<int> StartFolderFromFileAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var directory = ResolvePipelineDirectory(args);
        if (directory is null || !TryReadRequiredOption(args, "--file", out var fileReference))
        {
            return 1;
        }

        var state = await _pipelineStateStore.LoadStateAsync(directory, cancellationToken);
        var plan = pipelineRunner!.PlanStartFrom(state, fileReference);
        Console.WriteLine($"Start from: {plan.TargetFilePath}");
        foreach (var impact in plan.EarlierFiles)
        {
            Console.WriteLine($"- {Path.GetFileName(impact.FilePath)}: {impact.Description}");
        }

        if (!plan.CanStart)
        {
            logger.Error(plan.Reason);
            return 5;
        }

        state = await pipelineRunner.StartFromSelectedAsync(
            state,
            fileReference,
            new PipelineStartFromRequest
            {
                Reason = TryReadOption(args, "--reason") ??
                         $"Explicit CLI request to start from {Path.GetFileName(plan.TargetFilePath)}.",
                Actor = Environment.UserName,
                OverrideSource = "CLI folder start-from",
                Confirmed = true
            },
            cancellationToken: cancellationToken);
        PrintPipelineStatus(state);
        return PipelineExitCode(state);
    }

    private async Task<int> UndoFolderFileStatusAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var directory = ResolvePipelineDirectory(args);
        if (directory is null || !TryReadRequiredOption(args, "--file", out var fileReference))
        {
            return 1;
        }

        var state = await _pipelineStateStore.LoadStateAsync(directory, cancellationToken);
        state = await pipelineRunner!.UndoManualStatusAsync(
            state,
            fileReference,
            Environment.UserName,
            "CLI folder undo-status",
            cancellationToken);
        PrintPipelineStatus(state);
        return PipelineExitCode(state);
    }

    private bool TryReadFolderPath(string[] args, out string folderPath)
    {
        folderPath = string.Empty;
        if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
        {
            logger.Error("Missing pipeline folder path.");
            PrintFolderUsage();
            return false;
        }

        folderPath = args[2];
        return true;
    }

    private bool TryParsePipelineOptions(string[] args, out PipelineRunOptions options)
    {
        options = new PipelineRunOptions
        {
            ExecutionAgentOverride = TryReadOption(args, "--agent"),
            ReviewAgentOverride = TryReadOption(args, "--review-agent"),
            RequireReviewForLegacyFiles = HasOption(args, "--require-review"),
            AutoAdvanceApprovedWithWarnings = HasOption(args, "--allow-warning-auto-advance")
        };
        var mode = TryReadOption(args, "--mode") ?? "confirm";
        options.ExecutionMode = mode.ToLowerInvariant() switch
        {
            "manual" => PipelineExecutionMode.Manual,
            "confirm" or "confirm-each" => PipelineExecutionMode.ConfirmEach,
            "auto" or "auto-advance" => PipelineExecutionMode.AutoAdvance,
            _ => (PipelineExecutionMode)(-1)
        };
        if (!Enum.IsDefined(options.ExecutionMode))
        {
            logger.Error($"Invalid --mode '{mode}'. Use manual, confirm, or auto.");
            return false;
        }

        var maximumTransitions = TryReadOption(args, "--max-transitions");
        if (!string.IsNullOrWhiteSpace(maximumTransitions) &&
            (!int.TryParse(maximumTransitions, out var parsed) || parsed < 1))
        {
            logger.Error("--max-transitions must be an integer greater than zero.");
            return false;
        }

        if (int.TryParse(maximumTransitions, out var maximum))
        {
            options.MaximumAutomaticTransitions = maximum;
        }

        return true;
    }

    private string? ResolvePipelineDirectory(string[] args)
    {
        var explicitDirectory = TryReadOption(args, "--pipeline-run-directory");
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            var fullPath = Path.GetFullPath(explicitDirectory);
            if (File.Exists(Path.Combine(fullPath, "pipeline-state.json")))
            {
                return fullPath;
            }

            logger.Error($"Pipeline run directory does not contain pipeline-state.json: {fullPath}");
            return null;
        }

        var startDirectory = Directory.GetCurrentDirectory();
        var runId = TryReadOption(args, "--pipeline-run-id");
        var directory = string.IsNullOrWhiteSpace(runId)
            ? _pipelineStateStore.FindLatestPipelineDirectory(startDirectory)
            : _pipelineStateStore.FindPipelineDirectory(startDirectory, runId);
        if (directory is null)
        {
            logger.Error(string.IsNullOrWhiteSpace(runId)
                ? "No previous pipeline run found under .agentbatchrunner/pipelines (run from the repo root)."
                : $"No pipeline run with id '{runId}' found under .agentbatchrunner/pipelines.");
        }

        return directory;
    }

    private static int PipelineExitCode(PipelineRunState state)
    {
        return state.Status switch
        {
            PipelineRunStatus.Completed or PipelineRunStatus.Paused => 0,
            PipelineRunStatus.RateLimited => 3,
            PipelineRunStatus.Blocked => 5,
            PipelineRunStatus.NeedsHumanDecision => 6,
            PipelineRunStatus.Canceled => 130,
            _ => 2
        };
    }

    private static void PrintPipelineStatus(PipelineRunState state)
    {
        Console.WriteLine($"Pipeline run: {state.PipelineRunId}");
        Console.WriteLine($"Status: {state.Status}");
        Console.WriteLine($"Current file: {state.CurrentFileId ?? "(none)"}");
        Console.WriteLine($"Recommended next: {state.RecommendedNextFile ?? "(none)"}");
        Console.WriteLine($"Reason: {state.StopReason ?? state.NextDecision?.Reason ?? "(none)"}");
        Console.WriteLine($"Report: {Path.Combine(state.PipelineRunDirectory, "pipeline-report.md")}");
    }

    private int UnknownFolderSubcommand(string subcommand)
    {
        logger.Error($"Unknown folder subcommand: {subcommand}");
        PrintFolderUsage();
        return 1;
    }

    private int ShowLimits()
    {
        foreach (var agentName in new[] { "claude", "codex" })
        {
            var info = rateLimitStateStore.Get(agentName);
            Console.WriteLine($"{agentName}: {AgentRateLimitDisplay.Availability(agentName, info)}");
        }

        return 0;
    }

    private int SetLimit(string[] args)
    {
        if (!TryReadLimitAgent(args, "set", out var agentName))
        {
            return 1;
        }

        var untilText = TryReadOption(args, "--until");
        if (string.IsNullOrWhiteSpace(untilText) ||
            !DateTimeOffset.TryParse(
                untilText,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out var until))
        {
            logger.Error("Missing or invalid --until value. Use an ISO-8601 timestamp, e.g. 2026-06-26T13:27:00-04:00.");
            return 1;
        }

        var reason = TryReadOption(args, "--reason");
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "Usage limit reached";
        }

        rateLimitStateStore.SetBlocked(new AgentRateLimitInfo
        {
            AgentName = agentName,
            IsBlocked = true,
            BlockedUntil = until,
            Reason = reason,
            RawMessage = "Manually set via CLI."
        });

        logger.Info($"Blocked {agentName} until {AgentRateLimitDisplay.FormatLocal(until)}. Reason: {reason}");
        return 0;
    }

    private int ClearLimit(string[] args)
    {
        if (!TryReadLimitAgent(args, "clear", out var agentName))
        {
            return 1;
        }

        rateLimitStateStore.Clear(agentName);
        logger.Info($"Cleared saved rate-limit state for {agentName}.");
        return 0;
    }

    private bool TryReadLimitAgent(string[] args, string subcommand, out string agentName)
    {
        agentName = string.Empty;
        if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
        {
            logger.Error($"Usage: limits {subcommand} <claude|codex>" +
                (subcommand == "set" ? " --until <ISO-8601> [--reason <text>]" : string.Empty));
            return false;
        }

        var candidate = args[2].Trim().ToLowerInvariant();
        if (candidate is not ("claude" or "codex"))
        {
            logger.Error($"Cannot {subcommand} a limit for agent '{args[2]}'. Only claude and codex are supported.");
            return false;
        }

        agentName = candidate;
        return true;
    }

    private int UnknownLimitsSubcommand(string subcommand)
    {
        logger.Error($"Unknown limits subcommand: {subcommand}");
        PrintUsage();
        return 1;
    }

    private int CheckBlockedAgents(
        BatchConfig config,
        string? agentOverride,
        IReadOnlySet<string>? skipPromptIds = null,
        IRunAgentRoutingController? routingController = null)
    {
        var agentNames = routingController is null
            ? _effectiveAgentPolicy.ResolveDistinctAgents(config, agentOverride, skipPromptIds)
            : _effectiveAgentPolicy.ResolveAll(config, agentOverride, skipPromptIds)
                .Select(selection => routingController.Resolve(
                    selection.PromptId,
                    selection.BaseAgent,
                    selection.BaseRoutingReason).EffectiveAgent)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        foreach (var agentName in agentNames)
        {
            if (string.Equals(agentName, "dryrun", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rateLimitStateStore.TryGetBlocked(agentName, out var info))
            {
                if (config.AutoSwitchOnRateLimit &&
                    config.MaxRateLimitAgentSwitchesPerTask > 0 &&
                    _fallbackPolicy.GetFallbacks(config, agentName).Any(fallback =>
                        !rateLimitStateStore.TryGetBlocked(fallback, out _)))
                {
                    logger.Warning(
                        $"{AgentRateLimitDisplay.BlockedMessage(agentName, info)} " +
                        "The run will evaluate its configured fallback after toolchain preflight.");
                    continue;
                }

                logger.Error(AgentRateLimitDisplay.BlockedMessage(agentName, info));
                return 3;
            }
        }

        return 0;
    }

    private async Task<int> ReportAsync(string[] args, CancellationToken cancellationToken)
    {
        var runDirectory = ResolveRunDirectory(args);
        if (runDirectory is null)
        {
            return 1;
        }

        var result = await runStateStore.LoadRunResultAsync(runDirectory, cancellationToken);
        await reportGenerator.GenerateAsync(runDirectory, result, cancellationToken);
        var reportPath = Path.Combine(runDirectory, "final-report.md");
        logger.Info($"Report generated: {reportPath}");
        Console.WriteLine(await Utf8File.ReadAllTextAsync(reportPath, cancellationToken));
        return 0;
    }

    /// <summary>
    /// Resolves which run directory <c>resume</c>/<c>report</c> should act on. With <c>--run-id</c>
    /// the exact run is selected; otherwise the most recent run is used. Discovery walks up from the
    /// current working directory, so these commands must be run from the repo root (or an ancestor).
    /// </summary>
    private string? ResolveRunDirectory(string[] args)
    {
        var startDirectory = Directory.GetCurrentDirectory();
        var runId = TryReadOption(args, "--run-id");
        if (!string.IsNullOrWhiteSpace(runId))
        {
            var runDirectory = runStateStore.FindRunDirectory(startDirectory, runId);
            if (runDirectory is null)
            {
                logger.Error($"No run with id '{runId}' found under .agentbatchrunner/runs.");
            }

            return runDirectory;
        }

        var latest = runStateStore.FindLatestRunDirectory(startDirectory);
        if (latest is null)
        {
            logger.Error("No previous run found under .agentbatchrunner/runs (run from the repo root).");
        }

        return latest;
    }

    private static string? TryReadOption(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            var prefix = optionName + "=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[prefix.Length..];
            }
        }

        return null;
    }

    private bool TryReadRequiredOption(
        string[] args,
        string optionName,
        out string value)
    {
        value = TryReadOption(args, optionName) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        logger.Error($"Missing required option {optionName}.");
        PrintFolderUsage();
        return false;
    }

    private static bool HasOption(string[] args, string optionName)
    {
        return args.Any(arg => string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHelp(string arg)
    {
        return arg is "-h" or "--help" or "help";
    }

    private int UnknownCommand(string command)
    {
        logger.Error($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            AgentBatchRunner

            Usage:
              AgentBatchRunner validate <prompts.yaml>
              AgentBatchRunner run <prompts.yaml> [--agent claude|codex|dryrun]
              AgentBatchRunner resume [--run-id <id>] [--agent claude|codex|dryrun]
              AgentBatchRunner report [--run-id <id>]
              AgentBatchRunner limits
              AgentBatchRunner limits set <claude|codex> --until <ISO-8601> [--reason <text>]
              AgentBatchRunner limits clear <claude|codex>
              AgentBatchRunner folder validate <folder>
              AgentBatchRunner folder plan <folder>
              AgentBatchRunner folder run <folder> [--mode manual|confirm|auto]
              AgentBatchRunner folder run-next [--pipeline-run-id <id>]
              AgentBatchRunner folder resume [--pipeline-run-id <id>]
              AgentBatchRunner folder status [--pipeline-run-id <id>]
              AgentBatchRunner folder report [--pipeline-run-id <id>]
              AgentBatchRunner folder skip --pipeline-run-id <id> --file <file> --reason <text>
              AgentBatchRunner folder complete-manually --pipeline-run-id <id> --file <file> --reason <text>
              AgentBatchRunner folder start-from --pipeline-run-id <id> --file <file>
              AgentBatchRunner folder undo-status --pipeline-run-id <id> --file <file>

            resume/report act on the latest run by default; pass --run-id to target a
            specific run. Run them from the repo root so the run history can be found.

            limits set/clear manually block or unblock an agent (for example when Codex or
            Claude Desktop shows a usage-limit reset time that the CLI did not capture).
            """);
    }

    private static void PrintFolderUsage()
    {
        Console.WriteLine(
            """
            Folder Pipeline

            Usage:
              AgentBatchRunner folder validate <folder>
              AgentBatchRunner folder plan <folder>
              AgentBatchRunner folder run <folder> [--mode manual|confirm|auto]
                  [--file <pipeline-id|file>] [--agent claude|codex|dryrun]
                  [--review-agent claude|codex|dryrun] [--require-review]
                  [--max-transitions <count>] [--allow-warning-auto-advance]
              AgentBatchRunner folder run-next [--pipeline-run-id <id>]
              AgentBatchRunner folder resume [--pipeline-run-id <id>]
              AgentBatchRunner folder status [--pipeline-run-id <id>]
              AgentBatchRunner folder report [--pipeline-run-id <id>]
              AgentBatchRunner folder skip --pipeline-run-id <id> --file <file> --reason <text>
                  [--evidence <path>] [--notes <text>]
              AgentBatchRunner folder complete-manually --pipeline-run-id <id> --file <file>
                  --reason <text> [--evidence <path>] [--notes <text>]
                  [--satisfy-dependencies]
                  [--approve-gate --confirm-gate-override --override-source <source>]
              AgentBatchRunner folder start-from --pipeline-run-id <id> --file <file>
                  [--reason <text>]
              AgentBatchRunner folder undo-status --pipeline-run-id <id> --file <file>

            Confirm Each is the default. Auto Advance is opt-in and advances only on an
            Approved machine-readable review with canAutoAdvance=true and one eligible target.
            Run run-next/resume/status/report from the repository root.
            """);
    }
}
