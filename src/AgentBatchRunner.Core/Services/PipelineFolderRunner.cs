using System.Text;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public interface IPipelineFolderRunner
{
    Task<PipelinePlan> PlanAsync(string folderPath, CancellationToken cancellationToken = default);

    Task<PipelineRunState> CreateAsync(
        string folderPath,
        PipelineRunOptions options,
        CancellationToken cancellationToken = default);

    Task<PipelineRunState> RunSelectedAsync(
        PipelineRunState state,
        string fileReference,
        PipelineRunControl? control = null,
        CancellationToken cancellationToken = default);

    Task<PipelineRunState> RunPipelineAsync(
        PipelineRunState state,
        string? initialFileReference = null,
        PipelineRunControl? control = null,
        CancellationToken cancellationToken = default);

    Task<PipelineRunState> RunRecommendedNextAsync(
        PipelineRunState state,
        bool userConfirmed,
        PipelineRunControl? control = null,
        CancellationToken cancellationToken = default);

    Task<PipelineRunState> SkipAsync(
        PipelineRunState state,
        string fileReference,
        PipelineManualActionRequest request,
        CancellationToken cancellationToken = default);

    Task<PipelineRunState> CompleteManuallyAsync(
        PipelineRunState state,
        string fileReference,
        PipelineManualActionRequest request,
        CancellationToken cancellationToken = default);

    PipelineStartFromPlan PlanStartFrom(
        PipelineRunState state,
        string fileReference);

    Task<PipelineRunState> StartFromSelectedAsync(
        PipelineRunState state,
        string fileReference,
        PipelineStartFromRequest request,
        PipelineRunControl? control = null,
        CancellationToken cancellationToken = default);

    Task<PipelineRunState> UndoManualStatusAsync(
        PipelineRunState state,
        string fileReference,
        string actor,
        string overrideSource,
        CancellationToken cancellationToken = default);

    Task<PipelineRunState> ResumeAsync(
        string pipelineRunDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class PipelineFolderRunner(
    PipelineFolderDiscovery discovery,
    PipelinePlanBuilder planBuilder,
    PromptFileLoader promptFileLoader,
    IBatchExecutionRunner batchRunner,
    RunStateStore runStateStore,
    PipelineReviewYamlGenerator reviewGenerator,
    IPipelineReviewRunner reviewRunner,
    INextPipelineFileSelector nextFileSelector,
    PipelineStateStore pipelineStateStore,
    PipelineReportGenerator pipelineReportGenerator,
    EffectiveAgentPolicy effectiveAgentPolicy,
    AgentRateLimitStateStore rateLimitStateStore,
    IPipelineEventSink? eventSink = null) : IPipelineFolderRunner
{
    private readonly IPipelineEventSink _eventSink = eventSink ?? NullPipelineEventSink.Instance;

    public async Task<PipelinePlan> PlanAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var discovered = await discovery.DiscoverAsync(folderPath, cancellationToken);
        return planBuilder.Build(discovered);
    }

    public async Task<PipelineRunState> CreateAsync(
        string folderPath,
        PipelineRunOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options.MaximumAutomaticTransitions < 1)
        {
            throw new InvalidOperationException("maximum automatic transitions must be 1 or greater.");
        }

        ValidateAgentOverride(options.ExecutionAgentOverride, "execution agent override");
        ValidateAgentOverride(options.ReviewAgentOverride, "review agent override");
        var plan = await PlanAsync(folderPath, cancellationToken);
        if (!plan.IsValid)
        {
            throw new InvalidOperationException(
                "Pipeline validation failed: " + string.Join(" ", plan.Errors));
        }

        if (plan.Files.Count == 0)
        {
            throw new InvalidOperationException("No eligible YAML files were found in the pipeline folder.");
        }

        var repoPaths = plan.Files.Select(file => Path.GetFullPath(file.Config.RepoPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (repoPaths.Count != 1)
        {
            throw new InvalidOperationException(
                "Every YAML file in one folder pipeline must use the same repoPath.");
        }

        var repoPath = repoPaths[0];
        if (!Directory.Exists(repoPath))
        {
            throw new DirectoryNotFoundException($"Pipeline repository was not found: {repoPath}");
        }

        var pipelineRunId = CreatePipelineRunId(repoPath);
        var pipelineRunDirectory = pipelineStateStore.CreatePipelineRunDirectory(repoPath, pipelineRunId);
        var state = new PipelineRunState
        {
            PipelineRunId = pipelineRunId,
            FolderPath = Path.GetFullPath(folderPath),
            RepoPath = repoPath,
            PipelineRunDirectory = pipelineRunDirectory,
            ExecutionMode = options.ExecutionMode,
            Status = PipelineRunStatus.Paused,
            ExecutionAgentOverride = EffectiveAgentPolicy.NormalizeOptional(options.ExecutionAgentOverride),
            ReviewAgentOverride = EffectiveAgentPolicy.NormalizeOptional(options.ReviewAgentOverride),
            RequireReviewForLegacyFiles = options.RequireReviewForLegacyFiles,
            AutoAdvanceApprovedWithWarnings = options.AutoAdvanceApprovedWithWarnings,
            MaximumAutomaticTransitions = options.MaximumAutomaticTransitions,
            StartedAt = DateTimeOffset.Now
        };

        BuildQueue(state, plan);
        RefreshAgentAvailability(state);
        RefreshEligibility(state);
        AddTransition(state, "FolderScanned", null, $"Discovered {state.Files.Count} eligible pipeline file(s).");
        AddTransition(state, "QueuePlanned", null, "Pipeline queue validated and ordered.");
        await pipelineStateStore.SaveDiscoveredFilesAsync(pipelineRunDirectory, plan, cancellationToken);
        await PersistAsync(state, cancellationToken);
        await PublishAsync(state, PipelineEventKind.FolderScanned, null, $"Scanned {state.FolderPath}.", state.FolderPath, cancellationToken);
        await PublishAsync(state, PipelineEventKind.QueuePlanned, null, $"Planned {state.Files.Count} pipeline file(s).", pipelineRunDirectory, cancellationToken);
        return state;
    }

    public async Task<PipelineRunState> RunSelectedAsync(
        PipelineRunState state,
        string fileReference,
        PipelineRunControl? control = null,
        CancellationToken cancellationToken = default)
    {
        var file = ResolveFile(state, fileReference)
                   ?? throw new InvalidOperationException($"Pipeline file '{fileReference}' was not found in this queue.");
        RefreshAgentAvailability(state);
        RefreshEligibility(state);
        if (!NextPipelineFileSelector.IsEligible(state, file))
        {
            throw new InvalidOperationException(
                $"Pipeline file '{file.RelativePath}' is not eligible. Check dependencies, gates, and agent availability.");
        }

        try
        {
            await ExecuteFileAsync(state, file, control, cancellationToken);
            return state;
        }
        catch (OperationCanceledException)
        {
            await MarkCanceledAsync(state, file, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(state, file, ex.Message, CancellationToken.None);
            return state;
        }
    }

    public async Task<PipelineRunState> RunPipelineAsync(
        PipelineRunState state,
        string? initialFileReference = null,
        PipelineRunControl? control = null,
        CancellationToken cancellationToken = default)
    {
        var next = string.IsNullOrWhiteSpace(initialFileReference)
            ? SelectInitialFile(state)
            : ResolveFile(state, initialFileReference);
        if (next is null)
        {
            state.Status = PipelineRunStatus.Paused;
            state.StopReason = "No unambiguous eligible initial file was available.";
            AddTransition(state, "PipelinePaused", null, state.StopReason);
            await PersistAsync(state, cancellationToken);
            return state;
        }

        while (next is not null)
        {
            await RunSelectedAsync(state, next.FilePath, control, cancellationToken);
            if (IsTerminalPipelineStatus(state.Status))
            {
                return state;
            }

            if (control?.StopRequested == true)
            {
                await StopAtBoundaryAsync(state, "Pipeline stop was requested after the current file.", cancellationToken);
                return state;
            }

            if (control?.PauseRequested == true || state.ExecutionMode != PipelineExecutionMode.AutoAdvance)
            {
                await PauseAtBoundaryAsync(
                    state,
                    control?.PauseRequested == true
                        ? "Pause after current file was requested."
                        : state.ExecutionMode == PipelineExecutionMode.Manual
                            ? "Manual mode completed the selected file."
                            : "Confirm Each mode is waiting for approval to run the recommendation.",
                    cancellationToken);
                return state;
            }

            if (state.NextDecision is not { CanAutoRun: true, FilePath: not null } decision)
            {
                await PauseAtBoundaryAsync(
                    state,
                    state.NextDecision?.Reason ?? "No next file can auto-run.",
                    cancellationToken);
                return state;
            }

            if (state.AutomaticTransitions >= state.MaximumAutomaticTransitions)
            {
                await StopAtBoundaryAsync(
                    state,
                    $"Maximum automatic transition count {state.MaximumAutomaticTransitions} was reached.",
                    cancellationToken,
                    PipelineRunStatus.Blocked);
                return state;
            }

            state.AutomaticTransitions++;
            AddTransition(
                state,
                "AutomaticTransition",
                state.CurrentFileId,
                $"Persisted transition {state.AutomaticTransitions} before launching {decision.FilePath}.");
            await PersistAsync(state, cancellationToken);
            if (decision.IsReReview)
            {
                var reviewTarget = ResolveFile(state, decision.FilePath)!;
                await ExecuteReviewOnlyAsync(state, reviewTarget, control, cancellationToken);
                next = null;
                if (state.NextDecision is { CanAutoRun: true, FilePath: not null } afterReview)
                {
                    next = ResolveFile(state, afterReview.FilePath);
                }
            }
            else
            {
                next = ResolveFile(state, decision.FilePath);
            }
        }

        return state;
    }

    public async Task<PipelineRunState> RunRecommendedNextAsync(
        PipelineRunState state,
        bool userConfirmed,
        PipelineRunControl? control = null,
        CancellationToken cancellationToken = default)
    {
        var decision = state.NextDecision
                       ?? throw new InvalidOperationException("No next-file recommendation is available.");
        if (string.IsNullOrWhiteSpace(decision.FilePath))
        {
            throw new InvalidOperationException(decision.Reason);
        }

        if (decision.RequiresHumanConfirmation && !userConfirmed)
        {
            throw new InvalidOperationException("The next pipeline file requires explicit user confirmation.");
        }

        var target = ResolveFile(state, decision.FilePath)
                     ?? throw new InvalidOperationException("The recommended pipeline file is no longer in the queue.");
        if (decision.IsReReview)
        {
            await ExecuteReviewOnlyAsync(state, target, control, cancellationToken);
            return state;
        }

        return await RunPipelineAsync(state, target.FilePath, control, cancellationToken);
    }

    public async Task<PipelineRunState> SkipAsync(
        PipelineRunState state,
        string fileReference,
        PipelineManualActionRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureManualActionBoundary(state);
        ValidateManualRequest(request, requireEvidenceForGateOverride: false);
        var file = ResolveFile(state, fileReference)
                   ?? throw new InvalidOperationException($"Pipeline file '{fileReference}' was not found in this queue.");
        if (file.Status is not (PipelineFileStatus.Pending or PipelineFileStatus.Eligible))
        {
            throw new InvalidOperationException(
                $"Only a pending or eligible file can be skipped; {file.RelativePath} is {file.Status}.");
        }

        ApplyManualStatus(
            state,
            file,
            PipelineManualActionKind.SkippedByUser,
            PipelineFileStatus.SkippedByUser,
            request,
            satisfiesDependencies: false,
            gateApproved: false);
        file.LastMessage = "Skipped by user; dependencies and gates are not satisfied.";
        await RecalculateAfterManualActionAsync(
            state,
            file,
            "Queue recalculated after a user skip.",
            cancellationToken);
        return state;
    }

    public async Task<PipelineRunState> CompleteManuallyAsync(
        PipelineRunState state,
        string fileReference,
        PipelineManualActionRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureManualActionBoundary(state);
        ValidateManualRequest(request, requireEvidenceForGateOverride: request.GateApproved);
        var file = ResolveFile(state, fileReference)
                   ?? throw new InvalidOperationException($"Pipeline file '{fileReference}' was not found in this queue.");
        if (!CanCompleteManually(file.Status))
        {
            throw new InvalidOperationException(
                $"Pipeline file {file.RelativePath} cannot be manually completed from status {file.Status}.");
        }

        if (request.GateApproved && file.Gate is null)
        {
            throw new InvalidOperationException(
                $"Pipeline file {file.RelativePath} does not declare a gate to approve.");
        }

        ApplyManualStatus(
            state,
            file,
            PipelineManualActionKind.ManuallyCompleted,
            PipelineFileStatus.ManuallyCompleted,
            request,
            request.SatisfiesDependencies,
            request.GateApproved);
        file.LastMessage = request.GateApproved
            ? "Manually completed; the declared gate was explicitly approved."
            : request.SatisfiesDependencies
                ? "Manually completed; dependency satisfaction was explicitly accepted, but no gate was approved."
                : "Manually completed; dependencies and gates are not satisfied.";
        await RecalculateAfterManualActionAsync(
            state,
            file,
            "Queue recalculated after manual completion.",
            cancellationToken);
        return state;
    }

    public PipelineStartFromPlan PlanStartFrom(
        PipelineRunState state,
        string fileReference)
    {
        EnsureManualActionBoundary(state);
        RefreshAgentAvailability(state);
        RefreshEligibility(state);
        var target = ResolveFile(state, fileReference)
                     ?? throw new InvalidOperationException($"Pipeline file '{fileReference}' was not found in this queue.");
        var unmetDependencies = NextPipelineFileSelector.GetUnmetDependencyIds(state, target);
        var unmetGates = NextPipelineFileSelector.GetUnmetGatePrerequisiteIds(state, target);
        var unmet = unmetDependencies.Concat(unmetGates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var plan = new PipelineStartFromPlan
        {
            TargetFileId = target.PipelineId,
            TargetFilePath = target.FilePath,
            UnmetPrerequisiteIds = unmet
        };

        foreach (var earlier in state.Files
                     .Where(file => file.QueueOrder < target.QueueOrder)
                     .Where(file => file.Status is PipelineFileStatus.Pending or PipelineFileStatus.Eligible)
                     .OrderBy(file => file.QueueOrder))
        {
            var required = unmet.Contains(earlier.PipelineId, StringComparer.OrdinalIgnoreCase);
            var canSkip = !required && earlier.Status == PipelineFileStatus.Eligible;
            plan.EarlierFiles.Add(new PipelineStartFromImpact
            {
                FileId = earlier.PipelineId,
                FilePath = earlier.FilePath,
                CurrentStatus = earlier.Status,
                IsRequiredPrerequisite = required,
                WillBeSkipped = canSkip,
                Description = required
                    ? "Required prerequisite - cannot skip."
                    : canSkip
                        ? "Independent earlier eligible file - will be skipped after confirmation."
                        : "Earlier file is currently ineligible - it will remain pending."
            });
        }

        plan.CanStart = NextPipelineFileSelector.IsEligible(state, target);
        plan.Reason = plan.CanStart
            ? plan.EarlierFiles.Any(impact => impact.WillBeSkipped)
                ? "The selected file is eligible; independent earlier pending files require confirmation before they are skipped."
                : "The selected file is eligible and no earlier pending file needs to be skipped."
            : unmet.Count > 0
                ? $"The selected file has unmet prerequisites: {string.Join(", ", unmet)}."
                : "The selected file is not eligible because an execution or review agent is unavailable, or its current status cannot run.";
        return plan;
    }

    public async Task<PipelineRunState> StartFromSelectedAsync(
        PipelineRunState state,
        string fileReference,
        PipelineStartFromRequest request,
        PipelineRunControl? control = null,
        CancellationToken cancellationToken = default)
    {
        var plan = PlanStartFrom(state, fileReference);
        if (!plan.CanStart)
        {
            throw new InvalidOperationException(plan.Reason);
        }

        var filesToSkip = plan.EarlierFiles.Where(impact => impact.WillBeSkipped).ToList();
        if (filesToSkip.Count > 0 && !request.Confirmed)
        {
            throw new InvalidOperationException(
                "Starting from the selected file would skip earlier independent files and requires explicit confirmation.");
        }

        var actor = NormalizeAuditValue(request.Actor, Environment.UserName);
        var source = NormalizeAuditValue(request.OverrideSource, "Start From Selected");
        var reason = NormalizeAuditValue(
            request.Reason,
            $"Started pipeline from {Path.GetFileName(plan.TargetFilePath)}.");
        foreach (var impact in filesToSkip)
        {
            var earlier = ResolveFile(state, impact.FileId)!;
            ApplyManualStatus(
                state,
                earlier,
                PipelineManualActionKind.SkippedByUser,
                PipelineFileStatus.SkippedByUser,
                new PipelineManualActionRequest
                {
                    Reason = reason,
                    Actor = actor,
                    OverrideSource = source,
                    Notes = $"Skipped by Start From Selected for {plan.TargetFileId}."
                },
                satisfiesDependencies: false,
                gateApproved: false);
            earlier.LastMessage =
                $"Skipped by user while starting from {plan.TargetFileId}; dependencies and gates are not satisfied.";
        }

        var target = ResolveFile(state, plan.TargetFileId)!;
        state.ManualActionHistory.Add(new PipelineManualActionRecord
        {
            FileId = target.PipelineId,
            Action = PipelineManualActionKind.StartFromSelected,
            PreviousStatus = target.Status,
            NewStatus = target.Status,
            Timestamp = DateTimeOffset.Now,
            Reason = reason,
            Actor = actor,
            OverrideSource = source,
            AffectedFileIds = filesToSkip.Select(impact => impact.FileId).ToList()
        });
        AddTransition(
            state,
            "StartFromSelected",
            target.PipelineId,
            filesToSkip.Count == 0
                ? $"Starting from {target.RelativePath}; no earlier file was skipped."
                : $"Starting from {target.RelativePath}; skipped {string.Join(", ", filesToSkip.Select(file => file.FileId))}.");
        RefreshEligibility(state);
        state.Status = PipelineRunStatus.Paused;
        state.StopReason = null;
        state.CurrentFileId = null;
        await PersistAsync(state, cancellationToken);
        await PublishAsync(
            state,
            PipelineEventKind.StartFromSelected,
            target,
            $"Starting pipeline from {target.RelativePath}.",
            target.FilePath,
            cancellationToken);
        return await RunPipelineAsync(state, target.FilePath, control, cancellationToken);
    }

    public async Task<PipelineRunState> UndoManualStatusAsync(
        PipelineRunState state,
        string fileReference,
        string actor,
        string overrideSource,
        CancellationToken cancellationToken = default)
    {
        EnsureManualActionBoundary(state);
        var file = ResolveFile(state, fileReference)
                   ?? throw new InvalidOperationException($"Pipeline file '{fileReference}' was not found in this queue.");
        if (file.Status is not (PipelineFileStatus.SkippedByUser or PipelineFileStatus.ManuallyCompleted))
        {
            throw new InvalidOperationException(
                $"Only SkippedByUser or ManuallyCompleted can be undone; {file.RelativePath} is {file.Status}.");
        }

        var original = state.ManualActionHistory.LastOrDefault(entry =>
            string.Equals(entry.AuditId, file.ActiveManualActionId, StringComparison.OrdinalIgnoreCase));
        if (original is null)
        {
            throw new InvalidOperationException(
                $"The original manual action for {file.RelativePath} is missing; its status was not changed.");
        }

        var progressedDependents = state.Files.Where(candidate =>
                candidate.DependencyIds.Contains(file.PipelineId, StringComparer.OrdinalIgnoreCase) ||
                candidate.GatePrerequisiteFileIds.Contains(file.PipelineId, StringComparer.OrdinalIgnoreCase))
            .Where(HasProgressedPastPending)
            .Select(candidate => candidate.PipelineId)
            .ToList();
        if (progressedDependents.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot undo {file.RelativePath} because dependent files have already progressed: " +
                string.Join(", ", progressedDependents));
        }

        var previousStatus = file.Status;
        file.Status = original.PreviousStatus;
        ClearManualStatus(file);
        state.ManualActionHistory.Add(new PipelineManualActionRecord
        {
            FileId = file.PipelineId,
            Action = PipelineManualActionKind.UndoManualStatus,
            PreviousStatus = previousStatus,
            NewStatus = original.PreviousStatus,
            Timestamp = DateTimeOffset.Now,
            Reason = $"Undid manual action {original.Action}.",
            Actor = NormalizeAuditValue(actor, Environment.UserName),
            OverrideSource = NormalizeAuditValue(overrideSource, "Undo Manual Status"),
            ReversesAuditId = original.AuditId
        });
        file.LastMessage = $"Manual status was undone; restored {original.PreviousStatus}.";
        await RecalculateAfterManualActionAsync(
            state,
            file,
            "Queue recalculated after undoing a manual status.",
            cancellationToken);
        return state;
    }

    public async Task<PipelineRunState> ResumeAsync(
        string pipelineRunDirectory,
        CancellationToken cancellationToken = default)
    {
        var state = await pipelineStateStore.LoadStateAsync(pipelineRunDirectory, cancellationToken);
        RefreshAgentAvailability(state);
        RefreshEligibility(state);
        if (state.Status == PipelineRunStatus.Running)
        {
            var current = state.Files.FirstOrDefault(file =>
                string.Equals(file.PipelineId, state.CurrentFileId, StringComparison.OrdinalIgnoreCase));
            if (current is not null &&
                !string.IsNullOrWhiteSpace(current.ExecutionRunDirectory) &&
                File.Exists(Path.Combine(current.ExecutionRunDirectory, "run-summary.json")))
            {
                var executionResult = await runStateStore.LoadRunResultAsync(
                    current.ExecutionRunDirectory,
                    cancellationToken);
                ApplyExecutionResult(current, executionResult);
                if (current.ReviewRequired && current.ReviewVerdict is null)
                {
                    await ExecuteReviewAsync(state, current, executionResult, cancellationToken);
                }
            }
            else
            {
                state.Status = PipelineRunStatus.Paused;
                state.StopReason = "The application stopped during an execution with no complete run summary. Inspect its artifacts before choosing a next action; the file was not rerun.";
                AddTransition(state, "ResumePaused", current?.PipelineId, state.StopReason);
            }
        }

        await PersistAsync(state, cancellationToken);
        return state;
    }

    private async Task ExecuteFileAsync(
        PipelineRunState state,
        PipelineFileRunState file,
        PipelineRunControl? control,
        CancellationToken cancellationToken)
    {
        if (control?.StopRequested == true)
        {
            await StopAtBoundaryAsync(state, "Pipeline stop was requested before the next file.", cancellationToken);
            return;
        }

        state.Status = PipelineRunStatus.Running;
        state.CurrentFileId = file.PipelineId;
        state.StopReason = null;
        file.Status = PipelineFileStatus.Running;
        file.StartedAt ??= DateTimeOffset.Now;
        file.LastMessage = "Execution started.";
        var executionRunId = CreateExecutionRunId(state, file);
        file.ExecutionRunId = executionRunId;
        file.ExecutionRunDirectory = Path.Combine(state.RepoPath, ".agentbatchrunner", "runs", executionRunId);
        file.ExecutionReportPath = Path.Combine(file.ExecutionRunDirectory, "final-report.md");
        AddTransition(state, "ExecutionStarted", file.PipelineId, $"Execution run {executionRunId} started.");
        await PersistAsync(state, cancellationToken);
        await PublishAsync(state, PipelineEventKind.FileSelected, file, $"Selected {file.RelativePath}.", file.FilePath, cancellationToken);
        await PublishAsync(state, PipelineEventKind.ExecutionStarted, file, $"Execution run {executionRunId} started.", file.ExecutionRunDirectory, cancellationToken);

        var config = await promptFileLoader.LoadAsync(file.FilePath, cancellationToken);
        var executionResult = await batchRunner.RunAsync(
            config,
            new RunOptions
            {
                RunId = executionRunId,
                AgentOverride = state.ExecutionAgentOverride
            },
            cancellationToken);
        ApplyExecutionResult(file, executionResult);
        file.GitDiffPath = await SaveAggregateDiffAsync(state, file, executionResult, cancellationToken);
        AddTransition(state, "ExecutionCompleted", file.PipelineId, $"Execution completed with status {file.ExecutionStatus}.");
        await PersistAsync(state, cancellationToken);
        await PublishAsync(state, PipelineEventKind.ExecutionCompleted, file, file.LastMessage, file.ExecutionReportPath, cancellationToken);

        if (ApplyExecutionStopStatus(state, file))
        {
            file.CompletedAt = DateTimeOffset.Now;
            AddTransition(state, "PipelineStopped", file.PipelineId, file.LastMessage);
            await PersistAsync(state, cancellationToken);
            await PublishAsync(state, PipelineEventKind.PipelineStopped, file, file.LastMessage, file.ExecutionReportPath, cancellationToken);
            return;
        }

        if (!file.ReviewRequired)
        {
            file.Status = PipelineFileStatus.CompletedWithoutReview;
            file.CompletedAt = DateTimeOffset.Now;
            file.LastMessage = "Execution completed; no review was required.";
            RefreshEligibility(state);
            var decision = nextFileSelector.SelectNextEligible(
                state,
                "Execution completed without a required review.");
            file.RecommendedNextFile = decision.FilePath;
            state.RecommendedNextFile = decision.FilePath;
            state.NextDecision = decision;
            state.Status = PipelineRunStatus.Paused;
            state.StopReason = decision.Reason;
            AddTransition(state, "NextRecommended", file.PipelineId, decision.Reason);
            AddTransition(state, "PipelinePaused", file.PipelineId, decision.Reason);
            await PersistAsync(state, cancellationToken);
            await PublishAsync(state, PipelineEventKind.NextRecommended, file, decision.Reason, decision.FilePath, cancellationToken);
            await PublishAsync(state, PipelineEventKind.PipelinePaused, file, decision.Reason, file.ExecutionReportPath, cancellationToken);
            return;
        }

        await ExecuteReviewAsync(state, file, executionResult, cancellationToken);
    }

    private async Task ExecuteReviewOnlyAsync(
        PipelineRunState state,
        PipelineFileRunState file,
        PipelineRunControl? control,
        CancellationToken cancellationToken)
    {
        if (control?.StopRequested == true)
        {
            await StopAtBoundaryAsync(state, "Pipeline stop was requested before re-review.", cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(file.ExecutionRunDirectory) ||
            !File.Exists(Path.Combine(file.ExecutionRunDirectory, "run-summary.json")))
        {
            throw new InvalidOperationException(
                $"Cannot re-review {file.RelativePath}: its prior execution summary is missing.");
        }

        state.Status = PipelineRunStatus.Running;
        state.CurrentFileId = file.PipelineId;
        var executionResult = await runStateStore.LoadRunResultAsync(file.ExecutionRunDirectory, cancellationToken);
        AddTransition(state, "ReReviewStarted", file.PipelineId, "Independent re-review started without re-executing the source YAML.");
        await PersistAsync(state, cancellationToken);
        await ExecuteReviewAsync(state, file, executionResult, cancellationToken);
    }

    private async Task ExecuteReviewAsync(
        PipelineRunState state,
        PipelineFileRunState file,
        RunResult executionResult,
        CancellationToken cancellationToken)
    {
        file.Status = PipelineFileStatus.Reviewing;
        file.LastMessage = "Post-run review is being generated.";
        var sourceConfig = await promptFileLoader.LoadAsync(file.FilePath, cancellationToken);
        var sourceFile = new PipelineFile
        {
            FilePath = file.FilePath,
            RelativePath = file.RelativePath,
            FileName = file.FileName,
            Config = sourceConfig
        };
        var iteration = file.ReviewHistory.Count;
        GeneratedPipelineReview generated;
        try
        {
            generated = await reviewGenerator.GenerateAsync(
                new PipelineReviewGenerationRequest
                {
                    SourceFile = sourceFile,
                    ExecutionResult = executionResult,
                    ExecutionRunDirectory = file.ExecutionRunDirectory!,
                    PipelineRunDirectory = state.PipelineRunDirectory,
                    ReviewAgent = file.ReviewAgent,
                    ReviewIteration = iteration,
                    KnownOwnerDecisions = [.. sourceFile.Metadata?.Review.KnownOwnerDecisions ?? []],
                    PreviousReview = file.ReviewHistory.LastOrDefault()
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            file.Status = PipelineFileStatus.ReviewFailed;
            file.ReviewVerdict = PipelineReviewVerdict.ReviewFailed;
            file.CompletedAt = DateTimeOffset.Now;
            file.LastMessage = $"Review YAML generation failed: {ex.Message}";
            state.Status = PipelineRunStatus.Failed;
            state.StopReason = file.LastMessage;
            AddTransition(state, "ReviewGenerationFailed", file.PipelineId, file.LastMessage);
            await PersistAsync(state, cancellationToken);
            await PublishAsync(state, PipelineEventKind.PipelineStopped, file, file.LastMessage, state.PipelineRunDirectory, cancellationToken);
            return;
        }
        file.ReviewYamlPath = generated.ReviewYamlPath;
        AddTransition(state, "ReviewGenerated", file.PipelineId, $"Generated {generated.ReviewYamlPath}.");
        await PersistAsync(state, cancellationToken);
        await PublishAsync(state, PipelineEventKind.ReviewGenerated, file, "Post-run review YAML generated.", generated.ReviewYamlPath, cancellationToken);
        await PublishAsync(state, PipelineEventKind.ReviewStarted, file, $"Review started with {file.ReviewAgent}.", generated.ReviewYamlPath, cancellationToken);

        var reviewExecution = await reviewRunner.ExecuteAsync(
            generated,
            sourceFile,
            executionResult,
            state.PipelineRunDirectory,
            cancellationToken);
        ApplyReviewResult(file, reviewExecution.ReviewResult);
        AddTransition(state, "ReviewCompleted", file.PipelineId, $"Review verdict: {file.ReviewVerdict}.");
        await PublishAsync(state, PipelineEventKind.ReviewCompleted, file, file.LastMessage, file.ReviewReportPath, cancellationToken);

        var decision = nextFileSelector.SelectNext(state, file, reviewExecution.ReviewResult);
        file.RecommendedNextFile = decision.FilePath;
        state.RecommendedNextFile = decision.FilePath;
        state.NextDecision = decision;
        ApplyPipelineStatusFromReview(state, file, reviewExecution.ReviewResult, decision);
        AddTransition(state, "NextRecommended", file.PipelineId, decision.Reason);
        await PersistAsync(state, cancellationToken);
        await PublishAsync(state, PipelineEventKind.NextRecommended, file, decision.Reason, decision.FilePath, cancellationToken);
        if (state.Status == PipelineRunStatus.Completed)
        {
            AddTransition(state, "PipelineCompleted", file.PipelineId, "All eligible pipeline work completed with approved reviews.");
            await PersistAsync(state, cancellationToken);
            await PublishAsync(state, PipelineEventKind.PipelineCompleted, file, "Pipeline completed.", state.PipelineRunDirectory, cancellationToken);
        }
        else if (IsTerminalPipelineStatus(state.Status))
        {
            AddTransition(state, "PipelineStopped", file.PipelineId, state.StopReason ?? file.LastMessage);
            await PersistAsync(state, cancellationToken);
            await PublishAsync(state, PipelineEventKind.PipelineStopped, file, state.StopReason ?? file.LastMessage, file.ReviewReportPath, cancellationToken);
        }
    }

    private void BuildQueue(PipelineRunState state, PipelinePlan plan)
    {
        var references = plan.Files.ToDictionary(file => file.PipelineId, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < plan.Files.Count; index++)
        {
            var file = plan.Files[index];
            var executionAgents = effectiveAgentPolicy
                .ResolveDistinctAgents(file.Config, state.ExecutionAgentOverride)
                .OrderBy(agent => agent, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var reviewRequired = file.Metadata?.Review.Required ?? state.RequireReviewForLegacyFiles;
            var reviewAgent = ResolveReviewAgent(file, state.ReviewAgentOverride, reviewRequired);
            state.Files.Add(new PipelineFileRunState
            {
                QueueOrder = index + 1,
                PipelineId = file.PipelineId,
                FilePath = file.FilePath,
                RelativePath = file.RelativePath,
                FileName = file.FileName,
                Title = file.Title,
                Phase = file.Phase,
                DeclaredOrder = file.Order,
                IsLegacy = file.IsLegacy,
                ExecutionAgent = string.Join(", ", executionAgents),
                ReviewAgent = reviewAgent,
                DependencyIds = file.Dependencies
                    .Select(reference => ResolvePlanReference(plan.Files, reference)?.PipelineId ?? reference)
                    .ToList(),
                Gate = file.Metadata?.Gate,
                Next = file.Metadata?.Next ?? new PipelineNextMetadata(),
                ReviewRequired = reviewRequired,
                LastMessage = "Pending."
            });
        }

        foreach (var file in state.Files)
        {
            file.GatePrerequisiteFileIds = state.Files
                .Where(candidate => candidate.QueueOrder < file.QueueOrder &&
                                    candidate.Gate?.RequiredForNextPhase == true &&
                                    !string.Equals(candidate.Phase, file.Phase, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.PipelineId)
                .ToList();
        }
    }

    private void RefreshAgentAvailability(PipelineRunState state)
    {
        foreach (var file in state.Files.Where(file => file.Status is PipelineFileStatus.Pending or PipelineFileStatus.Eligible))
        {
            file.ExecutionAgentAvailable = file.ExecutionAgent
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(IsAgentAvailable);
            file.ReviewAgentAvailable = !file.ReviewRequired || IsAgentAvailable(file.ReviewAgent);
        }
    }

    private static void RefreshEligibility(PipelineRunState state)
    {
        foreach (var file in state.Files.Where(file => file.Status is PipelineFileStatus.Pending or PipelineFileStatus.Eligible))
        {
            file.MissingDependencyIds = NextPipelineFileSelector.GetUnmetDependencyIds(state, file);
            file.MissingGatePrerequisiteIds = NextPipelineFileSelector.GetUnmetGatePrerequisiteIds(state, file);
            var agentsAvailable = file.ExecutionAgentAvailable &&
                                  (!file.ReviewRequired || file.ReviewAgentAvailable);
            file.Status = agentsAvailable &&
                          file.MissingDependencyIds.Count == 0 &&
                          file.MissingGatePrerequisiteIds.Count == 0
                ? PipelineFileStatus.Eligible
                : PipelineFileStatus.Pending;
            if (!agentsAvailable)
            {
                file.LastMessage = "Waiting for an unavailable execution or review agent.";
            }
            else if (file.MissingDependencyIds.Count > 0 || file.MissingGatePrerequisiteIds.Count > 0)
            {
                var dependencyText = file.MissingDependencyIds.Count == 0
                    ? string.Empty
                    : $"dependencies: {string.Join(", ", file.MissingDependencyIds)}";
                var gateText = file.MissingGatePrerequisiteIds.Count == 0
                    ? string.Empty
                    : $"gates: {string.Join(", ", file.MissingGatePrerequisiteIds)}";
                file.LastMessage = "Blocked by prerequisites - " +
                                   string.Join("; ", new[] { dependencyText, gateText }
                                       .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
            else
            {
                file.LastMessage = "Eligible.";
            }
        }
    }

    private bool IsAgentAvailable(string agent)
    {
        return string.Equals(agent, "dryrun", StringComparison.OrdinalIgnoreCase) ||
               !rateLimitStateStore.TryGetBlocked(agent, out _);
    }

    private static string ResolveReviewAgent(
        PipelineFile file,
        string? reviewOverride,
        bool reviewRequired)
    {
        if (!reviewRequired)
        {
            return string.Empty;
        }

        var candidate = EffectiveAgentPolicy.NormalizeOptional(reviewOverride) ??
                        EffectiveAgentPolicy.NormalizeOptional(file.Metadata?.Review.Agent) ??
                        EffectiveAgentPolicy.NormalizeOptional(file.Config.DefaultAgent) ??
                        EffectiveAgentPolicy.NormalizeOptional(file.Config.Prompts.FirstOrDefault()?.Agent);
        if (!Agents.AgentAdapterFactory.IsSupportedAgent(candidate))
        {
            throw new InvalidOperationException(
                $"{file.RelativePath}: required review has no supported review agent.");
        }

        return candidate!;
    }

    private static PipelineFile? ResolvePlanReference(
        IEnumerable<PipelineFile> files,
        string reference)
    {
        var normalized = reference.Replace('/', Path.DirectorySeparatorChar);
        var matches = files.Where(file =>
                string.Equals(file.PipelineId, reference, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.FileName, reference, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.RelativePath, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static PipelineFileRunState? ResolveFile(PipelineRunState state, string reference)
    {
        var normalized = reference.Replace('/', Path.DirectorySeparatorChar);
        var matches = state.Files.Where(file =>
                string.Equals(file.PipelineId, reference, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.FileName, reference, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.RelativePath, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.FilePath, reference, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static PipelineFileRunState? SelectInitialFile(PipelineRunState state)
    {
        RefreshEligibility(state);
        var eligible = state.Files
            .Where(file => NextPipelineFileSelector.IsEligible(state, file))
            .OrderBy(file => file.DeclaredOrder ?? int.MaxValue)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (eligible.Count == 0)
        {
            return null;
        }

        var samePriority = eligible.Where(file => file.DeclaredOrder == eligible[0].DeclaredOrder).ToList();
        return samePriority.Count == 1 ? eligible[0] : null;
    }

    private static void ApplyExecutionResult(PipelineFileRunState file, RunResult result)
    {
        file.ExecutionRunId = result.RunId;
        file.ExecutionRunDirectory ??= Path.Combine(result.RepoPath, ".agentbatchrunner", "runs", result.RunId);
        file.ExecutionReportPath = Path.Combine(file.ExecutionRunDirectory, "final-report.md");
        file.ExecutionStatus = DetermineExecutionStatus(result);
        file.Status = file.ExecutionStatus is RunStatus.Succeeded or RunStatus.UnverifiedSuccess
            ? PipelineFileStatus.ExecutionSucceeded
            : MapExecutionFileStatus(file.ExecutionStatus.Value);
        file.LastMessage = $"Execution completed with status {file.ExecutionStatus}.";
    }

    private static RunStatus DetermineExecutionStatus(RunResult result)
    {
        foreach (var status in new[]
                 {
                     RunStatus.RateLimited,
                     RunStatus.ToolchainFailure,
                     RunStatus.Blocked,
                     RunStatus.NeedsHumanDecision,
                     RunStatus.PrerequisiteMissing,
                     RunStatus.Canceled,
                     RunStatus.TimedOut,
                     RunStatus.NeedsHumanReview,
                     RunStatus.Failed
                 })
        {
            if (result.Tasks.Any(task => task.Status == status))
            {
                return status;
            }
        }

        return result.Tasks.Any(task => task.Status == RunStatus.UnverifiedSuccess)
            ? RunStatus.UnverifiedSuccess
            : RunStatus.Succeeded;
    }

    private static PipelineFileStatus MapExecutionFileStatus(RunStatus status)
    {
        return status switch
        {
            RunStatus.Blocked => PipelineFileStatus.Blocked,
            RunStatus.NeedsHumanDecision => PipelineFileStatus.NeedsHumanDecision,
            RunStatus.PrerequisiteMissing => PipelineFileStatus.PrerequisiteMissing,
            RunStatus.RateLimited => PipelineFileStatus.RateLimited,
            RunStatus.Canceled => PipelineFileStatus.Canceled,
            _ => PipelineFileStatus.Failed
        };
    }

    private static bool ApplyExecutionStopStatus(PipelineRunState state, PipelineFileRunState file)
    {
        state.Status = file.ExecutionStatus switch
        {
            RunStatus.Blocked => PipelineRunStatus.Blocked,
            RunStatus.NeedsHumanDecision => PipelineRunStatus.NeedsHumanDecision,
            RunStatus.PrerequisiteMissing => PipelineRunStatus.Blocked,
            RunStatus.RateLimited => PipelineRunStatus.RateLimited,
            RunStatus.Canceled => PipelineRunStatus.Canceled,
            RunStatus.ToolchainFailure => PipelineRunStatus.Failed,
            _ => PipelineRunStatus.Running
        };
        if (state.Status == PipelineRunStatus.Running)
        {
            return false;
        }

        state.StopReason = file.LastMessage;
        return true;
    }

    private static void ApplyReviewResult(PipelineFileRunState file, PipelineReviewResult result)
    {
        file.ReviewVerdict = result.ReviewVerdict;
        file.ReviewRunId = result.ReviewRunId;
        file.ReviewYamlPath = result.ReviewYamlPath;
        file.ReviewResultPath = result.ReviewResultPath;
        file.ReviewReportPath = result.ReviewReportPath;
        file.ReviewHistory.Add(result);
        file.Findings = [.. result.Findings];
        file.RequiredDecisions = [.. result.RequiredDecisions];
        file.RecommendedNextFile = result.RecommendedNextFile;
        file.Status = result.ReviewVerdict switch
        {
            PipelineReviewVerdict.Approved => PipelineFileStatus.Approved,
            PipelineReviewVerdict.ApprovedWithWarnings => PipelineFileStatus.ApprovedWithWarnings,
            PipelineReviewVerdict.Blocked => PipelineFileStatus.Blocked,
            PipelineReviewVerdict.NeedsHumanDecision => PipelineFileStatus.NeedsHumanDecision,
            PipelineReviewVerdict.PrerequisiteMissing => PipelineFileStatus.PrerequisiteMissing,
            PipelineReviewVerdict.ReviewFailed => PipelineFileStatus.ReviewFailed,
            PipelineReviewVerdict.Canceled => PipelineFileStatus.Canceled,
            PipelineReviewVerdict.RateLimited => PipelineFileStatus.RateLimited,
            _ => PipelineFileStatus.ReviewFailed
        };
        file.CompletedAt = DateTimeOffset.Now;
        file.LastMessage = $"Review completed with verdict {result.ReviewVerdict}.";
    }

    private static void ApplyPipelineStatusFromReview(
        PipelineRunState state,
        PipelineFileRunState file,
        PipelineReviewResult review,
        NextPipelineFileDecision decision)
    {
        state.Status = review.ReviewVerdict switch
        {
            PipelineReviewVerdict.Approved => decision.FilePath is null &&
                                               state.Files.All(candidate => candidate.Status is not (PipelineFileStatus.Pending or PipelineFileStatus.Eligible))
                ? PipelineRunStatus.Completed
                : PipelineRunStatus.Running,
            PipelineReviewVerdict.ApprovedWithWarnings => PipelineRunStatus.Paused,
            PipelineReviewVerdict.Blocked => PipelineRunStatus.Blocked,
            PipelineReviewVerdict.NeedsHumanDecision => PipelineRunStatus.NeedsHumanDecision,
            PipelineReviewVerdict.PrerequisiteMissing => PipelineRunStatus.Blocked,
            PipelineReviewVerdict.RateLimited => PipelineRunStatus.RateLimited,
            PipelineReviewVerdict.Canceled => PipelineRunStatus.Canceled,
            _ => PipelineRunStatus.Failed
        };
        if (state.Status == PipelineRunStatus.Completed)
        {
            state.CompletedAt = DateTimeOffset.Now;
            state.StopReason = null;
        }
        else if (state.Status is not PipelineRunStatus.Running)
        {
            state.StopReason = review.Summary;
        }
    }

    private async Task<string> SaveAggregateDiffAsync(
        PipelineRunState state,
        PipelineFileRunState file,
        RunResult result,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(state.PipelineRunDirectory, "execution-diffs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"{FileNameSanitizer.Sanitize(file.PipelineId)}-{FileNameSanitizer.Sanitize(result.RunId)}.patch");
        var builder = new StringBuilder();
        foreach (var task in result.Tasks)
        {
            var taskPatch = Path.Combine(task.TaskDirectory, "git-diff-after.patch");
            if (!File.Exists(taskPatch))
            {
                continue;
            }

            builder.AppendLine($"# Task {task.Id}");
            builder.AppendLine(await Utf8File.ReadAllTextAsync(taskPatch, cancellationToken));
        }

        await Utf8File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
        return path;
    }

    private static void EnsureManualActionBoundary(PipelineRunState state)
    {
        if (state.Status == PipelineRunStatus.Running ||
            state.Files.Any(file => file.Status is PipelineFileStatus.Running or PipelineFileStatus.Reviewing))
        {
            throw new InvalidOperationException(
                "Manual pipeline status changes are allowed only at file boundaries; wait for the active file to finish.");
        }
    }

    private static void ValidateManualRequest(
        PipelineManualActionRequest request,
        bool requireEvidenceForGateOverride)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new InvalidOperationException("A reason is required for a manual pipeline status change.");
        }

        if (requireEvidenceForGateOverride && string.IsNullOrWhiteSpace(request.EvidencePath))
        {
            throw new InvalidOperationException(
                "Manual gate approval requires an explicit evidence or report path.");
        }

        if (requireEvidenceForGateOverride && string.IsNullOrWhiteSpace(request.OverrideSource))
        {
            throw new InvalidOperationException(
                "Manual gate approval requires an explicit override source for the audit trail.");
        }
    }

    private static bool CanCompleteManually(PipelineFileStatus status)
    {
        return status is
            PipelineFileStatus.Pending or
            PipelineFileStatus.Eligible or
            PipelineFileStatus.ExecutionSucceeded or
            PipelineFileStatus.CompletedWithoutReview or
            PipelineFileStatus.Blocked or
            PipelineFileStatus.NeedsHumanDecision or
            PipelineFileStatus.PrerequisiteMissing or
            PipelineFileStatus.ReviewFailed or
            PipelineFileStatus.Failed or
            PipelineFileStatus.Canceled;
    }

    private static void ApplyManualStatus(
        PipelineRunState state,
        PipelineFileRunState file,
        PipelineManualActionKind action,
        PipelineFileStatus newStatus,
        PipelineManualActionRequest request,
        bool satisfiesDependencies,
        bool gateApproved)
    {
        var timestamp = DateTimeOffset.Now;
        var actor = NormalizeAuditValue(request.Actor, Environment.UserName);
        var source = NormalizeAuditValue(request.OverrideSource, "Manual pipeline action");
        var audit = new PipelineManualActionRecord
        {
            FileId = file.PipelineId,
            Action = action,
            PreviousStatus = file.Status,
            NewStatus = newStatus,
            Timestamp = timestamp,
            Reason = request.Reason.Trim(),
            EvidencePath = NormalizeOptional(request.EvidencePath),
            Notes = NormalizeOptional(request.Notes),
            SatisfiesDependencies = satisfiesDependencies,
            GateApproved = gateApproved,
            Actor = actor,
            OverrideSource = source
        };
        state.ManualActionHistory.Add(audit);
        file.Status = newStatus;
        file.ManualReason = audit.Reason;
        file.ManualEvidencePath = audit.EvidencePath;
        file.ManualNotes = audit.Notes;
        file.ManualTimestamp = timestamp;
        file.ManualActor = actor;
        file.ManualOverrideSource = source;
        file.ManualSatisfiesDependencies = satisfiesDependencies;
        file.ManualGateApproved = gateApproved;
        file.ActiveManualActionId = audit.AuditId;
        file.MissingDependencyIds.Clear();
        file.MissingGatePrerequisiteIds.Clear();
        file.CompletedAt = timestamp;
        AddTransition(state, action.ToString(), file.PipelineId, audit.Reason);
    }

    private async Task RecalculateAfterManualActionAsync(
        PipelineRunState state,
        PipelineFileRunState changedFile,
        string reason,
        CancellationToken cancellationToken)
    {
        RefreshAgentAvailability(state);
        RefreshEligibility(state);
        var decision = nextFileSelector.SelectNextEligible(state, reason);
        state.NextDecision = decision;
        state.RecommendedNextFile = decision.FilePath;
        state.CurrentFileId = null;
        state.CompletedAt = null;
        state.Status = DetermineStatusAfterManualAction(state);
        state.StopReason = decision.Reason;
        AddTransition(state, "EligibilityRecalculated", changedFile.PipelineId, decision.Reason);
        await PersistAsync(state, cancellationToken);
        await PublishAsync(
            state,
            PipelineEventKind.ManualStatusChanged,
            changedFile,
            changedFile.LastMessage,
            changedFile.ManualEvidencePath,
            cancellationToken);
        await PublishAsync(
            state,
            PipelineEventKind.EligibilityChanged,
            changedFile,
            decision.Reason,
            decision.FilePath,
            cancellationToken);
    }

    private static PipelineRunStatus DetermineStatusAfterManualAction(PipelineRunState state)
    {
        if (state.Files.Any(file => file.Status == PipelineFileStatus.RateLimited))
        {
            return PipelineRunStatus.RateLimited;
        }

        if (state.Files.Any(file => file.Status == PipelineFileStatus.NeedsHumanDecision))
        {
            return PipelineRunStatus.NeedsHumanDecision;
        }

        if (state.Files.Any(file => file.Status is PipelineFileStatus.Blocked or PipelineFileStatus.PrerequisiteMissing))
        {
            return PipelineRunStatus.Blocked;
        }

        if (state.Files.Any(file => file.Status is PipelineFileStatus.Failed or PipelineFileStatus.ReviewFailed))
        {
            return PipelineRunStatus.Failed;
        }

        if (state.Files.Any(file => file.Status == PipelineFileStatus.Canceled))
        {
            return PipelineRunStatus.Canceled;
        }

        return PipelineRunStatus.Paused;
    }

    private static bool HasProgressedPastPending(PipelineFileRunState file)
    {
        return file.Status is not (
            PipelineFileStatus.Pending or
            PipelineFileStatus.Eligible or
            PipelineFileStatus.Skipped or
            PipelineFileStatus.SkippedByUser);
    }

    private static void ClearManualStatus(PipelineFileRunState file)
    {
        file.ManualReason = null;
        file.ManualEvidencePath = null;
        file.ManualNotes = null;
        file.ManualTimestamp = null;
        file.ManualActor = null;
        file.ManualOverrideSource = null;
        file.ManualSatisfiesDependencies = false;
        file.ManualGateApproved = false;
        file.ActiveManualActionId = null;
        file.CompletedAt = null;
    }

    private static string NormalizeAuditValue(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task PersistAsync(PipelineRunState state, CancellationToken cancellationToken)
    {
        await pipelineReportGenerator.GenerateAsync(state, cancellationToken);
    }

    private async Task PauseAtBoundaryAsync(
        PipelineRunState state,
        string reason,
        CancellationToken cancellationToken)
    {
        state.Status = PipelineRunStatus.Paused;
        state.StopReason = reason;
        AddTransition(state, "PipelinePaused", state.CurrentFileId, reason);
        await PersistAsync(state, cancellationToken);
        await PublishAsync(state, PipelineEventKind.PipelinePaused, null, reason, state.PipelineRunDirectory, cancellationToken);
    }

    private async Task StopAtBoundaryAsync(
        PipelineRunState state,
        string reason,
        CancellationToken cancellationToken,
        PipelineRunStatus status = PipelineRunStatus.Canceled)
    {
        state.Status = status;
        state.StopReason = reason;
        state.CompletedAt = DateTimeOffset.Now;
        AddTransition(state, "PipelineStopped", state.CurrentFileId, reason);
        await PersistAsync(state, cancellationToken);
        await PublishAsync(state, PipelineEventKind.PipelineStopped, null, reason, state.PipelineRunDirectory, cancellationToken);
    }

    private async Task MarkCanceledAsync(
        PipelineRunState state,
        PipelineFileRunState file,
        CancellationToken cancellationToken)
    {
        file.Status = PipelineFileStatus.Canceled;
        file.CompletedAt = DateTimeOffset.Now;
        file.LastMessage = "Pipeline execution was canceled safely; worktree changes were preserved.";
        state.Status = PipelineRunStatus.Canceled;
        state.StopReason = file.LastMessage;
        state.CompletedAt = DateTimeOffset.Now;
        AddTransition(state, "PipelineCanceled", file.PipelineId, file.LastMessage);
        await PersistAsync(state, cancellationToken);
    }

    private async Task MarkFailedAsync(
        PipelineRunState state,
        PipelineFileRunState file,
        string reason,
        CancellationToken cancellationToken)
    {
        file.Status = PipelineFileStatus.Failed;
        file.CompletedAt = DateTimeOffset.Now;
        file.LastMessage = $"Pipeline file failed unexpectedly: {reason}";
        state.Status = PipelineRunStatus.Failed;
        state.StopReason = file.LastMessage;
        state.CompletedAt = DateTimeOffset.Now;
        AddTransition(state, "PipelineFailed", file.PipelineId, file.LastMessage);
        await PersistAsync(state, cancellationToken);
        await PublishAsync(state, PipelineEventKind.PipelineStopped, file, file.LastMessage, state.PipelineRunDirectory, cancellationToken);
    }

    private Task PublishAsync(
        PipelineRunState state,
        PipelineEventKind kind,
        PipelineFileRunState? file,
        string message,
        string? path,
        CancellationToken cancellationToken)
    {
        return _eventSink.OnPipelineEventAsync(
            new PipelineEvent
            {
                Kind = kind,
                PipelineRunId = state.PipelineRunId,
                PipelineFileId = file?.PipelineId,
                PipelineStatus = state.Status,
                FileStatus = file?.Status,
                Message = message,
                Path = path
            },
            cancellationToken);
    }

    private static void AddTransition(
        PipelineRunState state,
        string eventName,
        string? pipelineFileId,
        string message)
    {
        state.Transitions.Add(new PipelineTransitionRecord
        {
            Timestamp = DateTimeOffset.Now,
            Event = eventName,
            PipelineFileId = pipelineFileId,
            Message = message
        });
    }

    private static void ValidateAgentOverride(string? agent, string name)
    {
        if (!string.IsNullOrWhiteSpace(agent) && !Agents.AgentAdapterFactory.IsSupportedAgent(agent))
        {
            throw new InvalidOperationException($"Unsupported {name} '{agent}'. Use claude, codex, or dryrun.");
        }
    }

    private static bool IsTerminalPipelineStatus(PipelineRunStatus status)
    {
        return status is
            PipelineRunStatus.Completed or
            PipelineRunStatus.Blocked or
            PipelineRunStatus.NeedsHumanDecision or
            PipelineRunStatus.RateLimited or
            PipelineRunStatus.Canceled or
            PipelineRunStatus.Failed;
    }

    private string CreatePipelineRunId(string repoPath)
    {
        var baseId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var candidate = baseId;
        for (var suffix = 1; Directory.Exists(Path.Combine(repoPath, ".agentbatchrunner", "pipelines", candidate)); suffix++)
        {
            candidate = $"{baseId}-{suffix:D2}";
        }

        return candidate;
    }

    private static string CreateExecutionRunId(PipelineRunState state, PipelineFileRunState file)
    {
        return $"{state.PipelineRunId}-F{file.QueueOrder:D2}";
    }
}
