using AgentBatchRunner.Models;
using AgentBatchRunner.Services;
using AgentBatchRunner.Infrastructure;

namespace AgentBatchRunner.Tests;

public sealed class PipelineFolderRunnerTests
{
    [Fact]
    public async Task ConfirmEach_RunsOneFileReviewsAndPersistsRecommendation()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10, nextApproved: "20_b.yaml");
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20);
        var batch = new FakeBatchRunner();
        var reviews = new FakeReviewRunner();
        var runner = CreateRunner(workspace.Root, batch, reviews);
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());

        await runner.RunPipelineAsync(state);

        Assert.Equal(PipelineRunStatus.Paused, state.Status);
        Assert.Equal(PipelineFileStatus.Approved, state.Files[0].Status);
        Assert.Equal(PipelineFileStatus.Eligible, state.Files[1].Status);
        Assert.Equal(state.Files[1].FilePath, state.RecommendedNextFile);
        Assert.Equal(1, batch.CallCount);
        Assert.Equal(1, reviews.CallCount);
        Assert.True(File.Exists(Path.Combine(state.PipelineRunDirectory, "pipeline-state.json")));
        Assert.True(File.Exists(Path.Combine(state.PipelineRunDirectory, "pipeline-report.md")));
        Assert.True(File.Exists(state.Files[0].GitDiffPath));

        await runner.RunRecommendedNextAsync(state, userConfirmed: true);

        Assert.Equal(PipelineRunStatus.Completed, state.Status);
        Assert.Equal(2, batch.CallCount);
        Assert.Equal(2, reviews.CallCount);
        Assert.All(state.Files, file => Assert.Equal(PipelineFileStatus.Approved, file.Status));
    }

    [Fact]
    public async Task AutoAdvance_RunsApprovedQueueSequentially()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10, nextApproved: "20_b.yaml");
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20);
        var batch = new FakeBatchRunner();
        var reviews = new FakeReviewRunner();
        var runner = CreateRunner(workspace.Root, batch, reviews);
        var state = await runner.CreateAsync(
            workspace.Root,
            new PipelineRunOptions { ExecutionMode = PipelineExecutionMode.AutoAdvance });

        await runner.RunPipelineAsync(state);

        Assert.Equal(PipelineRunStatus.Completed, state.Status);
        Assert.Equal(["A", "B"], batch.ExecutedPipelineIds);
        Assert.Equal(1, state.AutomaticTransitions);
    }

    [Fact]
    public async Task BlockedReviewStopsEvenWhenRepairIsRecommended()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_repair.yaml", "REPAIR", 20);
        var reviews = new FakeReviewRunner
        {
            ResultFactory = (source, execution, generated) => Review(
                source,
                execution,
                generated,
                PipelineReviewVerdict.Blocked,
                "20_repair.yaml")
        };
        var batch = new FakeBatchRunner();
        var runner = CreateRunner(workspace.Root, batch, reviews);
        var state = await runner.CreateAsync(
            workspace.Root,
            new PipelineRunOptions { ExecutionMode = PipelineExecutionMode.AutoAdvance });

        await runner.RunPipelineAsync(state);

        Assert.Equal(PipelineRunStatus.Blocked, state.Status);
        Assert.Equal(PipelineFileStatus.Blocked, state.Files[0].Status);
        Assert.Equal(state.Files[1].FilePath, state.RecommendedNextFile);
        Assert.False(state.NextDecision?.CanAutoRun);
        Assert.Equal(1, batch.CallCount);
    }

    [Theory]
    [InlineData(RunStatus.Blocked, PipelineRunStatus.Blocked)]
    [InlineData(RunStatus.NeedsHumanDecision, PipelineRunStatus.NeedsHumanDecision)]
    [InlineData(RunStatus.PrerequisiteMissing, PipelineRunStatus.Blocked)]
    [InlineData(RunStatus.RateLimited, PipelineRunStatus.RateLimited)]
    public async Task ExplicitExecutionStopOutcome_DoesNotRunReview(
        RunStatus executionStatus,
        PipelineRunStatus expectedPipelineStatus)
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        var batch = new FakeBatchRunner { StatusFactory = _ => executionStatus };
        var reviews = new FakeReviewRunner();
        var runner = CreateRunner(workspace.Root, batch, reviews);
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());

        await runner.RunPipelineAsync(state);

        Assert.Equal(expectedPipelineStatus, state.Status);
        Assert.Equal(0, reviews.CallCount);
        Assert.Equal(1, batch.CallCount);
    }

    [Fact]
    public async Task AutoAdvance_StopsAtConfiguredTransitionLimit()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10, nextApproved: "20_b.yaml");
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20, nextApproved: "30_c.yaml");
        await WritePipelineAsync(workspace.Root, workspace.Root, "30_c.yaml", "C", 30);
        var batch = new FakeBatchRunner();
        var runner = CreateRunner(workspace.Root, batch, new FakeReviewRunner());
        var state = await runner.CreateAsync(
            workspace.Root,
            new PipelineRunOptions
            {
                ExecutionMode = PipelineExecutionMode.AutoAdvance,
                MaximumAutomaticTransitions = 1
            });

        await runner.RunPipelineAsync(state);

        Assert.Equal(PipelineRunStatus.Blocked, state.Status);
        Assert.Equal(["A", "B"], batch.ExecutedPipelineIds);
        Assert.Equal(PipelineFileStatus.Eligible, state.Files[2].Status);
        Assert.Contains("Maximum automatic transition", state.StopReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PauseRequest_AppliesAfterCurrentFile()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10, nextApproved: "20_b.yaml");
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20);
        var batch = new FakeBatchRunner();
        var runner = CreateRunner(workspace.Root, batch, new FakeReviewRunner());
        var state = await runner.CreateAsync(
            workspace.Root,
            new PipelineRunOptions { ExecutionMode = PipelineExecutionMode.AutoAdvance });
        var control = new PipelineRunControl();
        control.RequestPauseAfterCurrentFile();

        await runner.RunPipelineAsync(state, control: control);

        Assert.Equal(PipelineRunStatus.Paused, state.Status);
        Assert.Equal(1, batch.CallCount);
        Assert.Equal(PipelineFileStatus.Approved, state.Files[0].Status);
        Assert.Equal(PipelineFileStatus.Eligible, state.Files[1].Status);
    }

    [Fact]
    public async Task Resume_LoadsPersistedBoundaryWithoutRerunningCompletedFile()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10, nextApproved: "20_b.yaml");
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20);
        var batch = new FakeBatchRunner();
        var runner = CreateRunner(workspace.Root, batch, new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());
        await runner.RunPipelineAsync(state);

        var restored = await runner.ResumeAsync(state.PipelineRunDirectory);
        await runner.RunRecommendedNextAsync(restored, userConfirmed: true);

        Assert.Equal(["A", "B"], batch.ExecutedPipelineIds);
        Assert.Equal(PipelineRunStatus.Completed, restored.Status);
    }

    [Fact]
    public async Task Skip_PersistsAuditAndNeverExecutesOrReviewsFile()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        var batch = new FakeBatchRunner();
        var reviews = new FakeReviewRunner();
        var runner = CreateRunner(workspace.Root, batch, reviews);
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());

        await runner.SkipAsync(state, "A", ManualRequest("Already completed elsewhere."));
        await runner.RunPipelineAsync(state);
        var restored = await runner.ResumeAsync(state.PipelineRunDirectory);

        Assert.Equal(PipelineFileStatus.SkippedByUser, restored.Files[0].Status);
        Assert.False(restored.Files[0].ManualSatisfiesDependencies);
        Assert.False(restored.Files[0].ManualGateApproved);
        Assert.Equal(0, batch.CallCount);
        Assert.Equal(0, reviews.CallCount);
        var audit = Assert.Single(restored.ManualActionHistory);
        Assert.Equal(PipelineManualActionKind.SkippedByUser, audit.Action);
        Assert.Equal("Already completed elsewhere.", audit.Reason);
    }

    [Fact]
    public async Task SkippedPrerequisite_BlocksDependentButIndependentFileRemainsEligible()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20, dependencies: ["A"]);
        await WritePipelineAsync(workspace.Root, workspace.Root, "30_c.yaml", "C", 30);
        var batch = new FakeBatchRunner();
        var runner = CreateRunner(workspace.Root, batch, new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());

        await runner.SkipAsync(state, "A", ManualRequest("Skip independent branch."));

        Assert.Equal(PipelineFileStatus.Pending, state.Files.Single(file => file.PipelineId == "B").Status);
        Assert.Contains("A", state.Files.Single(file => file.PipelineId == "B").MissingDependencyIds);
        Assert.Equal(PipelineFileStatus.Eligible, state.Files.Single(file => file.PipelineId == "C").Status);
        Assert.Equal(state.Files.Single(file => file.PipelineId == "C").FilePath, state.RecommendedNextFile);
        await runner.RunSelectedAsync(state, "C");
        Assert.Equal(["C"], batch.ExecutedPipelineIds);
    }

    [Fact]
    public async Task ManualCompletion_RequiresReasonAndDoesNotSatisfyDependencyByDefault()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20, dependencies: ["A"]);
        var runner = CreateRunner(workspace.Root, new FakeBatchRunner(), new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.CompleteManuallyAsync(
            state,
            "A",
            new PipelineManualActionRequest()));
        await runner.CompleteManuallyAsync(state, "A", ManualRequest("Reviewed outside this run."));

        Assert.Equal(PipelineFileStatus.ManuallyCompleted, state.Files[0].Status);
        Assert.False(state.Files[0].ManualSatisfiesDependencies);
        Assert.Equal(PipelineFileStatus.Pending, state.Files[1].Status);
    }

    [Fact]
    public async Task ExplicitManualDependencySatisfaction_MakesDependentEligible()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20, dependencies: ["A"]);
        var runner = CreateRunner(workspace.Root, new FakeBatchRunner(), new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());
        var request = ManualRequest("Accepted external completion.");
        request.SatisfiesDependencies = true;

        await runner.CompleteManuallyAsync(state, "A", request);
        var restored = await runner.ResumeAsync(state.PipelineRunDirectory);

        Assert.Equal(PipelineFileStatus.ManuallyCompleted, restored.Files[0].Status);
        Assert.True(restored.Files[0].ManualSatisfiesDependencies);
        Assert.Equal(PipelineFileStatus.Eligible, restored.Files[1].Status);
    }

    [Fact]
    public async Task ManualCompletion_DoesNotApproveGateByDefault()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10, phase: "P0", declaresGate: true);
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20, phase: "P1");
        var runner = CreateRunner(workspace.Root, new FakeBatchRunner(), new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());
        var request = ManualRequest("Implementation exists externally.");
        request.SatisfiesDependencies = true;

        await runner.CompleteManuallyAsync(state, "A", request);

        Assert.False(state.Files[0].ManualGateApproved);
        Assert.Equal(PipelineFileStatus.Pending, state.Files[1].Status);
        Assert.Contains("A", state.Files[1].MissingGatePrerequisiteIds);
    }

    [Fact]
    public async Task ExplicitManualGateApproval_SatisfiesGatePrerequisiteWithAuditEvidence()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10, phase: "P0", declaresGate: true);
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20, phase: "P1");
        var runner = CreateRunner(workspace.Root, new FakeBatchRunner(), new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());
        var request = ManualRequest("Architecture review explicitly approved G-A.");
        request.GateApproved = true;
        request.EvidencePath = Path.Combine(workspace.Root, "architecture-review.md");

        await runner.CompleteManuallyAsync(state, "A", request);

        Assert.True(state.Files[0].ManualGateApproved);
        Assert.Equal(PipelineFileStatus.Eligible, state.Files[1].Status);
        Assert.True(state.ManualActionHistory[^1].GateApproved);
        Assert.Equal(request.EvidencePath, state.ManualActionHistory[^1].EvidencePath);
    }

    [Fact]
    public async Task StartFromSelected_RefusesUnmetPrerequisiteWithoutChangingQueue()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20, dependencies: ["A"]);
        var batch = new FakeBatchRunner();
        var runner = CreateRunner(workspace.Root, batch, new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());

        var plan = runner.PlanStartFrom(state, "B");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.StartFromSelectedAsync(state, "B", new PipelineStartFromRequest { Confirmed = true }));

        Assert.False(plan.CanStart);
        Assert.Contains("A", plan.UnmetPrerequisiteIds);
        Assert.Contains("unmet prerequisites", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(state.Files, file => Assert.NotEqual(PipelineFileStatus.SkippedByUser, file.Status));
        Assert.Equal(0, batch.CallCount);
    }

    [Fact]
    public async Task StartFromSelected_SkipsOnlyEarlierEligibleFilesAfterConfirmation()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20);
        await WritePipelineAsync(workspace.Root, workspace.Root, "30_c.yaml", "C", 30);
        var batch = new FakeBatchRunner();
        var runner = CreateRunner(workspace.Root, batch, new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());

        await runner.StartFromSelectedAsync(
            state,
            "B",
            new PipelineStartFromRequest
            {
                Confirmed = true,
                Reason = "Begin at B.",
                Actor = "tester",
                OverrideSource = "unit test"
            });

        Assert.Equal(PipelineFileStatus.SkippedByUser, state.Files.Single(file => file.PipelineId == "A").Status);
        Assert.Equal(PipelineFileStatus.Approved, state.Files.Single(file => file.PipelineId == "B").Status);
        Assert.Equal(PipelineFileStatus.Eligible, state.Files.Single(file => file.PipelineId == "C").Status);
        Assert.Equal(["B"], batch.ExecutedPipelineIds);
        Assert.Contains(state.ManualActionHistory, action => action.Action == PipelineManualActionKind.StartFromSelected);
    }

    [Fact]
    public async Task StartFromSelected_LeavesEarlierIneligibleFilePending()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "05_d.yaml", "D", 5);
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10, dependencies: ["D"]);
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20);
        var runner = CreateRunner(workspace.Root, new FakeBatchRunner(), new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());
        await runner.CompleteManuallyAsync(state, "D", ManualRequest("External work exists, but dependency acceptance is pending."));

        var plan = runner.PlanStartFrom(state, "B");
        await runner.StartFromSelectedAsync(
            state,
            "B",
            new PipelineStartFromRequest { Confirmed = true, Reason = "Start independent B." });

        var impact = Assert.Single(plan.EarlierFiles, item => item.FileId == "A");
        Assert.False(impact.WillBeSkipped);
        Assert.Equal(PipelineFileStatus.Pending, state.Files.Single(file => file.PipelineId == "A").Status);
    }

    [Fact]
    public async Task NoReviewCompletion_ClearsStaleRecommendationToCompletedFile()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        await WritePipelineAsync(workspace.Root, workspace.Root, "20_b.yaml", "B", 20);
        var runner = CreateRunner(workspace.Root, new FakeBatchRunner(), new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());
        await runner.SkipAsync(state, "A", ManualRequest("Skip A."));
        var target = state.Files.Single(file => file.PipelineId == "B");
        target.ReviewRequired = false;
        Assert.Equal(target.FilePath, state.RecommendedNextFile);

        await runner.StartFromSelectedAsync(
            state,
            "B",
            new PipelineStartFromRequest { Confirmed = true, Reason = "Start B." });

        Assert.Equal(PipelineFileStatus.CompletedWithoutReview, target.Status);
        Assert.Null(state.RecommendedNextFile);
        Assert.Null(state.NextDecision?.FilePath);
    }

    [Fact]
    public async Task Undo_RestoresPriorStatusAndResumeRetainsResult()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        var runner = CreateRunner(workspace.Root, new FakeBatchRunner(), new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());
        var originalStatus = state.Files[0].Status;
        await runner.SkipAsync(state, "A", ManualRequest("Accidental skip."));

        await runner.UndoManualStatusAsync(state, "A", "tester", "unit test");
        var restored = await runner.ResumeAsync(state.PipelineRunDirectory);

        Assert.Equal(originalStatus, restored.Files[0].Status);
        Assert.Null(restored.Files[0].ManualReason);
        Assert.Equal(2, restored.ManualActionHistory.Count);
        Assert.Equal(PipelineManualActionKind.UndoManualStatus, restored.ManualActionHistory[^1].Action);
        Assert.NotNull(restored.ManualActionHistory[^1].ReversesAuditId);
    }

    [Fact]
    public async Task ManualActionHistory_IsWrittenToPipelineReport()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, workspace.Root, "10_a.yaml", "A", 10);
        var runner = CreateRunner(workspace.Root, new FakeBatchRunner(), new FakeReviewRunner());
        var state = await runner.CreateAsync(workspace.Root, new PipelineRunOptions());

        await runner.SkipAsync(state, "A", ManualRequest("Documented external completion."));
        var report = await Utf8File.ReadAllTextAsync(Path.Combine(state.PipelineRunDirectory, "pipeline-report.md"));

        Assert.Contains("Manual Action Audit History", report);
        Assert.Contains("SkippedByUser", report);
        Assert.Contains("Documented external completion.", report);
        Assert.Contains("unit test", report);
    }

    private static PipelineFolderRunner CreateRunner(
        string root,
        FakeBatchRunner batchRunner,
        FakeReviewRunner reviewRunner)
    {
        var loader = new PromptFileLoader();
        var stateStore = new PipelineStateStore();
        return new PipelineFolderRunner(
            new PipelineFolderDiscovery(loader),
            new PipelinePlanBuilder(),
            loader,
            batchRunner,
            new RunStateStore(),
            new PipelineReviewYamlGenerator(),
            reviewRunner,
            new NextPipelineFileSelector(),
            stateStore,
            new PipelineReportGenerator(stateStore),
            new EffectiveAgentPolicy(),
            new AgentRateLimitStateStore(Path.Combine(root, ".agentbatchrunner", "test-limits.json")));
    }

    private static async Task WritePipelineAsync(
        string folder,
        string repoPath,
        string fileName,
        string id,
        int order,
        string? nextApproved = null,
        IReadOnlyCollection<string>? dependencies = null,
        string phase = "TEST",
        bool declaresGate = false)
    {
        var next = string.IsNullOrWhiteSpace(nextApproved)
            ? string.Empty
            : $"  next:{Environment.NewLine}    onApproved: {nextApproved}{Environment.NewLine}";
        var dependencyList = dependencies is null || dependencies.Count == 0
            ? "[]"
            : "[" + string.Join(", ", dependencies) + "]";
        var gate = declaresGate
            ? $"  gate:{Environment.NewLine}    id: G-{id}{Environment.NewLine}    requiredForNextPhase: true{Environment.NewLine}"
            : string.Empty;
        var yaml =
            $"pipeline:{Environment.NewLine}" +
            $"  id: {id}{Environment.NewLine}" +
            $"  title: {id} title{Environment.NewLine}" +
            $"  phase: {phase}{Environment.NewLine}" +
            $"  order: {order}{Environment.NewLine}" +
            $"  enabled: true{Environment.NewLine}" +
            $"  dependsOn: {dependencyList}{Environment.NewLine}" +
            gate +
            $"  review:{Environment.NewLine}" +
            $"    required: true{Environment.NewLine}" +
            $"    agent: dryrun{Environment.NewLine}" +
            $"    autoGenerate: true{Environment.NewLine}" +
            next +
            $"project: {id}{Environment.NewLine}" +
            $"repoPath: '{repoPath}'{Environment.NewLine}" +
            $"defaultAgent: dryrun{Environment.NewLine}" +
            $"prompts:{Environment.NewLine}" +
            $"  - id: {id}-P001{Environment.NewLine}" +
            $"    title: Work{Environment.NewLine}" +
            $"    prompt: Perform safe work.{Environment.NewLine}" +
            $"    verify: []{Environment.NewLine}";
        await Utf8File.WriteAllTextAsync(Path.Combine(folder, fileName), yaml);
    }

    private static PipelineManualActionRequest ManualRequest(string reason)
    {
        return new PipelineManualActionRequest
        {
            Reason = reason,
            Actor = "tester",
            OverrideSource = "unit test"
        };
    }

    private static PipelineReviewResult Review(
        PipelineFile source,
        RunResult execution,
        GeneratedPipelineReview generated,
        PipelineReviewVerdict verdict,
        string? recommended = null)
    {
        return new PipelineReviewResult
        {
            SourcePipelineFile = source.FileName,
            ExecutionRunId = execution.RunId,
            ReviewRunId = "review-" + source.PipelineId,
            ExecutionStatus = "Succeeded",
            ReviewVerdict = verdict,
            GateApproved = verdict is PipelineReviewVerdict.Approved or PipelineReviewVerdict.ApprovedWithWarnings,
            Summary = $"Review returned {verdict}.",
            RecommendedNextFile = recommended,
            CanAutoAdvance = verdict == PipelineReviewVerdict.Approved,
            ReviewYamlPath = generated.ReviewYamlPath,
            ReviewResultPath = generated.ReviewResultPath,
            ReviewReportPath = generated.ReviewReportPath
        };
    }

    private sealed class FakeBatchRunner : IBatchExecutionRunner
    {
        public Func<BatchConfig, RunStatus> StatusFactory { get; set; } = _ => RunStatus.Succeeded;

        public int CallCount { get; private set; }

        public List<string> ExecutedPipelineIds { get; } = [];

        public async Task<RunResult> RunAsync(
            BatchConfig config,
            RunOptions options,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ExecutedPipelineIds.Add(config.Pipeline!.Id);
            var runId = options.RunId!;
            var runDirectory = Path.Combine(config.RepoPath, ".agentbatchrunner", "runs", runId);
            var taskDirectory = Path.Combine(runDirectory, "tasks", config.Prompts[0].Id);
            Directory.CreateDirectory(taskDirectory);
            await Utf8File.WriteAllTextAsync(Path.Combine(taskDirectory, "git-diff-after.patch"), "diff", cancellationToken);
            var status = StatusFactory(config);
            var result = new RunResult
            {
                RunId = runId,
                Project = config.Project,
                RepoPath = config.RepoPath,
                StartedAt = DateTimeOffset.Now,
                CompletedAt = DateTimeOffset.Now,
                Tasks =
                [
                    new TaskRunResult
                    {
                        Id = config.Prompts[0].Id,
                        Title = config.Prompts[0].Title,
                        Status = status,
                        TaskDirectory = taskDirectory
                    }
                ]
            };
            var store = new RunStateStore();
            await store.SaveJsonAsync(Path.Combine(runDirectory, "run-summary.json"), result, cancellationToken);
            await Utf8File.WriteAllTextAsync(Path.Combine(runDirectory, "final-report.md"), "execution report", cancellationToken);
            return result;
        }
    }

    private sealed class FakeReviewRunner : IPipelineReviewRunner
    {
        public Func<PipelineFile, RunResult, GeneratedPipelineReview, PipelineReviewResult> ResultFactory { get; set; } =
            (source, execution, generated) => Review(
                source,
                execution,
                generated,
                PipelineReviewVerdict.Approved);

        public int CallCount { get; private set; }

        public Task<PipelineReviewExecutionResult> ExecuteAsync(
            GeneratedPipelineReview generatedReview,
            PipelineFile sourceFile,
            RunResult executionResult,
            string pipelineRunDirectory,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var review = ResultFactory(sourceFile, executionResult, generatedReview);
            return Task.FromResult(new PipelineReviewExecutionResult
            {
                GeneratedReview = generatedReview,
                ReviewRunDirectory = Path.Combine(pipelineRunDirectory, "review-runs", review.ReviewRunId),
                ReviewResult = review
            });
        }
    }
}
