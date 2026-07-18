using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public interface INextPipelineFileSelector
{
    NextPipelineFileDecision SelectNext(
        PipelineRunState pipelineState,
        PipelineFileRunState completedFile,
        PipelineReviewResult reviewResult);

    NextPipelineFileDecision SelectNextEligible(
        PipelineRunState pipelineState,
        string reason);
}

public sealed class NextPipelineFileSelector : INextPipelineFileSelector
{
    public NextPipelineFileDecision SelectNext(
        PipelineRunState pipelineState,
        PipelineFileRunState completedFile,
        PipelineReviewResult reviewResult)
    {
        var recommended = ResolveReference(pipelineState.Files, reviewResult.RecommendedNextFile);
        if (recommended is not null)
        {
            var recommendedDecision = BuildTargetDecision(
                pipelineState,
                reviewResult,
                recommended,
                "The machine-readable review recommended this file.");
            if (recommendedDecision is not null)
            {
                return recommendedDecision;
            }
        }

        var nextRule = GetNextRule(completedFile.Next, reviewResult.ReviewVerdict);
        if (string.Equals(nextRule, "stop", StringComparison.OrdinalIgnoreCase))
        {
            return Stop($"pipeline.next for {reviewResult.ReviewVerdict} requires the pipeline to stop.");
        }

        if (string.Equals(nextRule, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return Manual("pipeline.next requires manual next-file selection.");
        }

        var metadataTarget = ResolveReference(pipelineState.Files, nextRule);
        if (metadataTarget is not null)
        {
            var metadataDecision = BuildTargetDecision(
                pipelineState,
                reviewResult,
                metadataTarget,
                $"pipeline.next selected this file for {reviewResult.ReviewVerdict}.");
            if (metadataDecision is not null)
            {
                return metadataDecision;
            }
        }

        if (reviewResult.ReviewVerdict is not (
                PipelineReviewVerdict.Approved or PipelineReviewVerdict.ApprovedWithWarnings))
        {
            return Stop($"Review verdict {reviewResult.ReviewVerdict} blocks automatic next-file selection.");
        }

        var eligible = pipelineState.Files
            .Where(file => IsEligible(pipelineState, file))
            .OrderBy(file => file.DeclaredOrder ?? int.MaxValue)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (eligible.Count == 0)
        {
            return new NextPipelineFileDecision
            {
                Reason = pipelineState.Files.All(IsTerminal)
                    ? "No pending pipeline files remain."
                    : "No pending file is eligible; inspect dependency, gate, and agent availability state."
            };
        }

        var firstPriority = eligible[0].DeclaredOrder;
        var equallyRanked = eligible.Where(file => file.DeclaredOrder == firstPriority).ToList();
        if (equallyRanked.Count > 1)
        {
            return new NextPipelineFileDecision
            {
                Reason = "Multiple equally ranked files are eligible; manual selection is required.",
                RequiresHumanConfirmation = true,
                CandidateFilePaths = equallyRanked.Select(file => file.FilePath).ToList()
            };
        }

        if (eligible[0].IsLegacy)
        {
            return new NextPipelineFileDecision
            {
                FilePath = eligible[0].FilePath,
                Reason = "A legacy file is next by filename, but files without pipeline metadata require manual confirmation.",
                RequiresHumanConfirmation = true,
                CandidateFilePaths = eligible.Select(file => file.FilePath).ToList()
            };
        }

        return BuildTargetDecision(
                   pipelineState,
                   reviewResult,
                   eligible[0],
                   "Selected the eligible dependency-ordered file with the lowest explicit order.")
               ?? Stop("The next ordered file is not eligible.");
    }

    public NextPipelineFileDecision SelectNextEligible(
        PipelineRunState pipelineState,
        string reason)
    {
        var eligible = pipelineState.Files
            .Where(file => IsEligible(pipelineState, file))
            .OrderBy(file => file.DeclaredOrder ?? int.MaxValue)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (eligible.Count == 0)
        {
            return new NextPipelineFileDecision
            {
                Reason = pipelineState.Files.All(IsTerminal)
                    ? reason + " No pending pipeline files remain."
                    : reason + " No pending file is eligible; inspect dependency, gate, and agent availability state."
            };
        }

        var firstPriority = eligible[0].DeclaredOrder;
        var equallyRanked = eligible.Where(file => file.DeclaredOrder == firstPriority).ToList();
        if (equallyRanked.Count > 1)
        {
            return new NextPipelineFileDecision
            {
                Reason = reason + " Multiple equally ranked files are eligible; manual selection is required.",
                RequiresHumanConfirmation = true,
                CandidateFilePaths = equallyRanked.Select(file => file.FilePath).ToList()
            };
        }

        return new NextPipelineFileDecision
        {
            FilePath = eligible[0].FilePath,
            Reason = reason + (eligible[0].IsLegacy
                ? " The next legacy file requires manual confirmation."
                : " The next eligible dependency-ordered file was selected."),
            RequiresHumanConfirmation = true,
            CandidateFilePaths = [eligible[0].FilePath]
        };
    }

    public static bool IsEligible(PipelineRunState state, PipelineFileRunState candidate)
    {
        if (candidate.Status is not (PipelineFileStatus.Pending or PipelineFileStatus.Eligible) ||
            !candidate.ExecutionAgentAvailable ||
            (candidate.ReviewRequired && !candidate.ReviewAgentAvailable))
        {
            return false;
        }

        return GetUnmetDependencyIds(state, candidate).Count == 0 &&
               GetUnmetGatePrerequisiteIds(state, candidate).Count == 0;
    }

    public static List<string> GetUnmetDependencyIds(
        PipelineRunState state,
        PipelineFileRunState candidate)
    {
        var byId = state.Files.ToDictionary(file => file.PipelineId, StringComparer.OrdinalIgnoreCase);
        return candidate.DependencyIds
            .Where(id => !byId.TryGetValue(id, out var dependency) || !SatisfiesDependency(dependency))
            .ToList();
    }

    public static List<string> GetUnmetGatePrerequisiteIds(
        PipelineRunState state,
        PipelineFileRunState candidate)
    {
        var byId = state.Files.ToDictionary(file => file.PipelineId, StringComparer.OrdinalIgnoreCase);
        return candidate.GatePrerequisiteFileIds
            .Where(id => !byId.TryGetValue(id, out var gateFile) || !SatisfiesGatePrerequisite(gateFile))
            .ToList();
    }

    public static bool SatisfiesDependency(PipelineFileRunState file)
    {
        return file.Status switch
        {
            PipelineFileStatus.Approved => true,
            PipelineFileStatus.CompletedWithoutReview => !file.ReviewRequired && file.Gate is null,
            PipelineFileStatus.ManuallyCompleted => file.ManualSatisfiesDependencies,
            _ => false
        };
    }

    public static bool SatisfiesGatePrerequisite(PipelineFileRunState file)
    {
        return file.Status switch
        {
            PipelineFileStatus.Approved => true,
            PipelineFileStatus.ManuallyCompleted => file.ManualGateApproved,
            _ => false
        };
    }

    private static NextPipelineFileDecision? BuildTargetDecision(
        PipelineRunState state,
        PipelineReviewResult review,
        PipelineFileRunState target,
        string reason)
    {
        var isReReview = target.HasCompletedExecution &&
                         target.ReviewVerdict is not PipelineReviewVerdict.Approved;
        if (!isReReview && !IsEligible(state, target))
        {
            return null;
        }

        if (target.HasCompletedExecution && !isReReview)
        {
            return null;
        }

        var approved = review.ReviewVerdict == PipelineReviewVerdict.Approved;
        var warningsAllowed = review.ReviewVerdict == PipelineReviewVerdict.ApprovedWithWarnings &&
                              state.AutoAdvanceApprovedWithWarnings;
        var canAutoRun = state.ExecutionMode == PipelineExecutionMode.AutoAdvance &&
                         review.CanAutoAdvance &&
                         (approved || warningsAllowed);
        return new NextPipelineFileDecision
        {
            FilePath = target.FilePath,
            Reason = isReReview ? reason + " The completed target will be re-reviewed without re-execution." : reason,
            CanAutoRun = canAutoRun,
            RequiresHumanConfirmation = !canAutoRun,
            IsReReview = isReReview,
            CandidateFilePaths = [target.FilePath]
        };
    }

    private static PipelineFileRunState? ResolveReference(
        IReadOnlyCollection<PipelineFileRunState> files,
        string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) ||
            reference.Equals("manual", StringComparison.OrdinalIgnoreCase) ||
            reference.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = reference.Trim().Replace('/', Path.DirectorySeparatorChar);
        var matches = files.Where(file =>
                string.Equals(file.PipelineId, reference.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.FileName, reference.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.RelativePath, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file.FilePath, reference.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string? GetNextRule(PipelineNextMetadata next, PipelineReviewVerdict verdict)
    {
        return verdict switch
        {
            PipelineReviewVerdict.Approved => next.OnApproved,
            PipelineReviewVerdict.ApprovedWithWarnings => next.OnApprovedWithWarnings,
            PipelineReviewVerdict.Blocked => next.OnBlocked,
            PipelineReviewVerdict.NeedsHumanDecision => next.OnNeedsHumanDecision,
            PipelineReviewVerdict.PrerequisiteMissing => next.OnPrerequisiteMissing,
            PipelineReviewVerdict.ReviewFailed => next.OnReviewFailed,
            PipelineReviewVerdict.Canceled => next.OnCanceled,
            PipelineReviewVerdict.RateLimited => next.OnRateLimited,
            _ => null
        };
    }

    private static bool IsTerminal(PipelineFileRunState file)
    {
        return file.Status is
            PipelineFileStatus.Approved or
            PipelineFileStatus.ApprovedWithWarnings or
            PipelineFileStatus.Blocked or
            PipelineFileStatus.NeedsHumanDecision or
            PipelineFileStatus.PrerequisiteMissing or
            PipelineFileStatus.ReviewFailed or
            PipelineFileStatus.RateLimited or
            PipelineFileStatus.Canceled or
            PipelineFileStatus.Failed or
            PipelineFileStatus.Skipped or
            PipelineFileStatus.SkippedByUser or
            PipelineFileStatus.ManuallyCompleted or
            PipelineFileStatus.CompletedWithoutReview;
    }

    private static NextPipelineFileDecision Manual(string reason)
    {
        return new NextPipelineFileDecision
        {
            Reason = reason,
            RequiresHumanConfirmation = true
        };
    }

    private static NextPipelineFileDecision Stop(string reason)
    {
        return new NextPipelineFileDecision { Reason = reason };
    }
}
