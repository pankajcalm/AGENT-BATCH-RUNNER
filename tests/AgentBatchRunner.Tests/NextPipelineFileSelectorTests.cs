using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class NextPipelineFileSelectorTests
{
    [Fact]
    public void SelectNext_ReviewRecommendationWins()
    {
        var state = CreateState(File("A", 10, PipelineFileStatus.Approved), File("B", 20), File("C", 30));
        var review = Approved("C.yaml");

        var decision = new NextPipelineFileSelector().SelectNext(state, state.Files[0], review);

        Assert.EndsWith("C.yaml", decision.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("review recommended", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectNext_MetadataRulePrecedesDependencyOrderFallback()
    {
        var completed = File("A", 10, PipelineFileStatus.Approved);
        completed.Next.OnApproved = "C";
        var state = CreateState(completed, File("B", 20), File("C", 30));

        var decision = new NextPipelineFileSelector().SelectNext(state, completed, Approved());

        Assert.EndsWith("C.yaml", decision.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pipeline.next", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectNext_UsesLowestEligibleOrderAndHonorsDependencyApproval()
    {
        var approved = File("A", 10, PipelineFileStatus.Approved);
        var blockedByDependency = File("B", 20);
        blockedByDependency.DependencyIds.Add("MISSING");
        var eligible = File("C", 30);
        var state = CreateState(approved, blockedByDependency, eligible);

        var decision = new NextPipelineFileSelector().SelectNext(state, approved, Approved());

        Assert.Equal(eligible.FilePath, decision.FilePath);
    }

    [Fact]
    public void SelectNext_EquallyRankedCandidatesRequireManualSelection()
    {
        var completed = File("A", 10, PipelineFileStatus.Approved);
        var left = File("B", null);
        var right = File("C", null);
        var state = CreateState(completed, left, right);

        var decision = new NextPipelineFileSelector().SelectNext(state, completed, Approved());

        Assert.Null(decision.FilePath);
        Assert.True(decision.RequiresHumanConfirmation);
        Assert.Equal(2, decision.CandidateFilePaths.Count);
    }

    [Fact]
    public void SelectNext_DoesNotSelectAlreadyApprovedFileAgain()
    {
        var completed = File("A", 10, PipelineFileStatus.Approved);
        var alreadyApproved = File("B", 20, PipelineFileStatus.Approved);
        var pending = File("C", 30);
        var state = CreateState(completed, alreadyApproved, pending);

        var decision = new NextPipelineFileSelector().SelectNext(state, completed, Approved());

        Assert.Equal(pending.FilePath, decision.FilePath);
    }

    [Fact]
    public void SelectNext_BlockedReviewCanRecommendRepairButNeverAutoRuns()
    {
        var blocked = File("A", 10, PipelineFileStatus.Blocked);
        var repair = File("REPAIR", 20);
        var state = CreateState(blocked, repair);
        state.ExecutionMode = PipelineExecutionMode.AutoAdvance;
        var review = new PipelineReviewResult
        {
            ReviewVerdict = PipelineReviewVerdict.Blocked,
            RecommendedNextFile = repair.FileName,
            CanAutoAdvance = false
        };

        var decision = new NextPipelineFileSelector().SelectNext(state, blocked, review);

        Assert.Equal(repair.FilePath, decision.FilePath);
        Assert.False(decision.CanAutoRun);
        Assert.True(decision.RequiresHumanConfirmation);
    }

    [Fact]
    public void SelectNext_CompletedBlockedTargetBecomesReReviewNotReExecution()
    {
        var repair = File("REPAIR", 20, PipelineFileStatus.Approved);
        var original = File("ORIGINAL", 10, PipelineFileStatus.Blocked);
        original.ExecutionRunId = "run-1";
        var state = CreateState(original, repair);

        var decision = new NextPipelineFileSelector().SelectNext(
            state,
            repair,
            Approved(original.FileName));

        Assert.True(decision.IsReReview);
        Assert.Equal(original.FilePath, decision.FilePath);
    }

    [Fact]
    public void SelectNext_ApprovedWithWarningsRequiresConfirmationByDefault()
    {
        var completed = File("A", 10, PipelineFileStatus.ApprovedWithWarnings);
        var next = File("B", 20);
        var state = CreateState(completed, next);
        state.ExecutionMode = PipelineExecutionMode.AutoAdvance;
        var review = new PipelineReviewResult
        {
            ReviewVerdict = PipelineReviewVerdict.ApprovedWithWarnings,
            CanAutoAdvance = true,
            RecommendedNextFile = next.FileName
        };

        var decision = new NextPipelineFileSelector().SelectNext(state, completed, review);

        Assert.False(decision.CanAutoRun);
        Assert.True(decision.RequiresHumanConfirmation);
    }

    [Fact]
    public void SelectNextEligible_ExcludesSkippedAndIneligibleFiles()
    {
        var skipped = File("A", 10, PipelineFileStatus.SkippedByUser);
        var blockedBySkipped = File("B", 20);
        blockedBySkipped.DependencyIds.Add("A");
        var independent = File("C", 30);
        var state = CreateState(skipped, blockedBySkipped, independent);

        var decision = new NextPipelineFileSelector().SelectNextEligible(
            state,
            "Manual status changed.");

        Assert.Equal(independent.FilePath, decision.FilePath);
        Assert.DoesNotContain(skipped.FilePath, decision.CandidateFilePaths);
        Assert.DoesNotContain(blockedBySkipped.FilePath, decision.CandidateFilePaths);
    }

    private static PipelineReviewResult Approved(string? recommendation = null)
    {
        return new PipelineReviewResult
        {
            ReviewVerdict = PipelineReviewVerdict.Approved,
            GateApproved = true,
            CanAutoAdvance = true,
            RecommendedNextFile = recommendation
        };
    }

    private static PipelineRunState CreateState(params PipelineFileRunState[] files)
    {
        return new PipelineRunState
        {
            ExecutionMode = PipelineExecutionMode.ConfirmEach,
            Files = [.. files]
        };
    }

    private static PipelineFileRunState File(
        string id,
        int? order,
        PipelineFileStatus status = PipelineFileStatus.Pending)
    {
        return new PipelineFileRunState
        {
            PipelineId = id,
            FileName = id + ".yaml",
            RelativePath = id + ".yaml",
            FilePath = Path.Combine(@"C:\pipeline", id + ".yaml"),
            DeclaredOrder = order,
            Status = status,
            ExecutionAgentAvailable = true,
            ReviewAgentAvailable = true,
            ReviewRequired = true,
            ReviewVerdict = status switch
            {
                PipelineFileStatus.Approved => PipelineReviewVerdict.Approved,
                PipelineFileStatus.ApprovedWithWarnings => PipelineReviewVerdict.ApprovedWithWarnings,
                PipelineFileStatus.Blocked => PipelineReviewVerdict.Blocked,
                _ => null
            }
        };
    }
}
