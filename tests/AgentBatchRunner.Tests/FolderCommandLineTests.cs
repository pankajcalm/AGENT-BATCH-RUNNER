using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class FolderCommandLineTests
{
    [Fact]
    public async Task FolderValidate_UsesCorePlanner()
    {
        using var workspace = TestWorkspace.Create();
        var pipeline = new FakePipelineRunner
        {
            Plan = new PipelinePlan
            {
                FolderPath = workspace.Root,
                Files = [new PipelineFile { FileName = "one.yaml", RelativePath = "one.yaml" }]
            }
        };
        var app = CreateApp(workspace.Root, pipeline);

        var exitCode = await app.RunAsync(["folder", "validate", workspace.Root], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(workspace.Root, pipeline.PlannedFolder);
    }

    [Fact]
    public async Task FolderRun_DefaultsToConfirmEachAndRunsCoreStateMachine()
    {
        using var workspace = TestWorkspace.Create();
        var pipeline = new FakePipelineRunner();
        var app = CreateApp(workspace.Root, pipeline);

        var exitCode = await app.RunAsync(["folder", "run", workspace.Root], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(PipelineExecutionMode.ConfirmEach, pipeline.CreatedOptions?.ExecutionMode);
        Assert.True(pipeline.RunPipelineCalled);
    }

    [Fact]
    public async Task FolderRun_RejectsInvalidModeBeforeCreatingRun()
    {
        using var workspace = TestWorkspace.Create();
        var pipeline = new FakePipelineRunner();
        var app = CreateApp(workspace.Root, pipeline);

        var exitCode = await app.RunAsync(
            ["folder", "run", workspace.Root, "--mode", "unsafe"],
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(pipeline.CreatedOptions);
    }

    [Fact]
    public async Task FolderSkip_MapsRequiredAuditDataToRunner()
    {
        using var workspace = TestWorkspace.Create();
        var directory = await SavePipelineStateAsync(workspace.Root);
        var pipeline = new FakePipelineRunner();
        var app = CreateApp(workspace.Root, pipeline);

        var exitCode = await app.RunAsync(
            [
                "folder", "skip", "--pipeline-run-directory", directory,
                "--file", "A.yaml", "--reason", "Handled elsewhere"
            ],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("A.yaml", pipeline.LastFileReference);
        Assert.Equal("Handled elsewhere", pipeline.LastManualRequest?.Reason);
        Assert.Equal("CLI folder skip", pipeline.LastManualRequest?.OverrideSource);
    }

    [Fact]
    public async Task FolderCompleteManually_DefaultsToNoDependencyOrGateOverride()
    {
        using var workspace = TestWorkspace.Create();
        var directory = await SavePipelineStateAsync(workspace.Root);
        var pipeline = new FakePipelineRunner();
        var app = CreateApp(workspace.Root, pipeline);

        var exitCode = await app.RunAsync(
            [
                "folder", "complete-manually", "--pipeline-run-directory", directory,
                "--file", "A", "--reason", "External review"
            ],
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.False(pipeline.LastManualRequest?.SatisfiesDependencies);
        Assert.False(pipeline.LastManualRequest?.GateApproved);
    }

    [Fact]
    public async Task FolderCompleteManually_GateOverrideRequiresConfirmationAuditOptions()
    {
        using var workspace = TestWorkspace.Create();
        var directory = await SavePipelineStateAsync(workspace.Root);
        var pipeline = new FakePipelineRunner();
        var app = CreateApp(workspace.Root, pipeline);

        var exitCode = await app.RunAsync(
            [
                "folder", "complete-manually", "--pipeline-run-directory", directory,
                "--file", "A", "--reason", "Approved", "--approve-gate"
            ],
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Null(pipeline.LastManualRequest);
    }

    [Fact]
    public async Task FolderStartFromAndUndo_UseCoreOperations()
    {
        using var workspace = TestWorkspace.Create();
        var directory = await SavePipelineStateAsync(workspace.Root);
        var pipeline = new FakePipelineRunner();
        var app = CreateApp(workspace.Root, pipeline);

        var startExitCode = await app.RunAsync(
            ["folder", "start-from", "--pipeline-run-directory", directory, "--file", "A"],
            CancellationToken.None);
        var undoExitCode = await app.RunAsync(
            ["folder", "undo-status", "--pipeline-run-directory", directory, "--file", "A"],
            CancellationToken.None);

        Assert.Equal(0, startExitCode);
        Assert.Equal(0, undoExitCode);
        Assert.True(pipeline.StartFromCalled);
        Assert.True(pipeline.UndoCalled);
        Assert.True(pipeline.LastStartRequest?.Confirmed);
    }

    private static async Task<string> SavePipelineStateAsync(string root)
    {
        var directory = Path.Combine(root, ".agentbatchrunner", "pipelines", "pipeline-test");
        var state = new PipelineRunState
        {
            PipelineRunId = "pipeline-test",
            FolderPath = root,
            RepoPath = root,
            PipelineRunDirectory = directory,
            Status = PipelineRunStatus.Paused,
            StartedAt = DateTimeOffset.Now,
            Files =
            [
                new PipelineFileRunState
                {
                    QueueOrder = 1,
                    PipelineId = "A",
                    FileName = "A.yaml",
                    FilePath = Path.Combine(root, "A.yaml"),
                    RelativePath = "A.yaml",
                    Status = PipelineFileStatus.Eligible
                }
            ]
        };
        await new PipelineStateStore().SaveStateAsync(state);
        return directory;
    }

    private static CommandLineApp CreateApp(string root, IPipelineFolderRunner pipelineRunner)
    {
        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var runStateStore = new RunStateStore();
        var reportGenerator = new ReportGenerator(runStateStore);
        var loader = new PromptFileLoader();
        var limits = new AgentRateLimitStateStore(Path.Combine(root, "limits.json"));
        var batchRunner = new BatchRunner(
            loader,
            new GitCheckpointManager(processRunner, logger),
            new VerificationRunner(processRunner, logger),
            runStateStore,
            reportGenerator,
            new AgentAdapterFactory(processRunner, logger),
            logger,
            rateLimitStateStore: limits,
            agentPreflightService: NoOpAgentPreflightService.Instance);
        var pipelineStateStore = new PipelineStateStore();
        return new CommandLineApp(
            loader,
            batchRunner,
            runStateStore,
            reportGenerator,
            limits,
            logger,
            isElevated: () => false,
            pipelineRunner: pipelineRunner,
            pipelineStateStore: pipelineStateStore,
            pipelineReportGenerator: new PipelineReportGenerator(pipelineStateStore));
    }

    private sealed class FakePipelineRunner : IPipelineFolderRunner
    {
        public PipelinePlan Plan { get; set; } = new();

        public string? PlannedFolder { get; private set; }

        public PipelineRunOptions? CreatedOptions { get; private set; }

        public bool RunPipelineCalled { get; private set; }

        public string? LastFileReference { get; private set; }

        public PipelineManualActionRequest? LastManualRequest { get; private set; }

        public PipelineStartFromRequest? LastStartRequest { get; private set; }

        public bool StartFromCalled { get; private set; }

        public bool UndoCalled { get; private set; }

        public Task<PipelinePlan> PlanAsync(string folderPath, CancellationToken cancellationToken = default)
        {
            PlannedFolder = folderPath;
            return Task.FromResult(Plan);
        }

        public Task<PipelineRunState> CreateAsync(
            string folderPath,
            PipelineRunOptions options,
            CancellationToken cancellationToken = default)
        {
            CreatedOptions = options;
            return Task.FromResult(new PipelineRunState
            {
                PipelineRunId = "pipeline-test",
                FolderPath = folderPath,
                RepoPath = folderPath,
                PipelineRunDirectory = Path.Combine(folderPath, ".agentbatchrunner", "pipelines", "pipeline-test"),
                ExecutionMode = options.ExecutionMode,
                Status = PipelineRunStatus.Paused
            });
        }

        public Task<PipelineRunState> RunSelectedAsync(
            PipelineRunState state,
            string fileReference,
            PipelineRunControl? control = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(state);
        }

        public Task<PipelineRunState> RunPipelineAsync(
            PipelineRunState state,
            string? initialFileReference = null,
            PipelineRunControl? control = null,
            CancellationToken cancellationToken = default)
        {
            RunPipelineCalled = true;
            state.Status = PipelineRunStatus.Paused;
            return Task.FromResult(state);
        }

        public Task<PipelineRunState> RunRecommendedNextAsync(
            PipelineRunState state,
            bool userConfirmed,
            PipelineRunControl? control = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(state);
        }

        public Task<PipelineRunState> SkipAsync(
            PipelineRunState state,
            string fileReference,
            PipelineManualActionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastFileReference = fileReference;
            LastManualRequest = request;
            return Task.FromResult(state);
        }

        public Task<PipelineRunState> CompleteManuallyAsync(
            PipelineRunState state,
            string fileReference,
            PipelineManualActionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastFileReference = fileReference;
            LastManualRequest = request;
            return Task.FromResult(state);
        }

        public PipelineStartFromPlan PlanStartFrom(PipelineRunState state, string fileReference)
        {
            return new PipelineStartFromPlan
            {
                TargetFileId = fileReference,
                TargetFilePath = fileReference,
                CanStart = true
            };
        }

        public Task<PipelineRunState> StartFromSelectedAsync(
            PipelineRunState state,
            string fileReference,
            PipelineStartFromRequest request,
            PipelineRunControl? control = null,
            CancellationToken cancellationToken = default)
        {
            LastFileReference = fileReference;
            LastStartRequest = request;
            StartFromCalled = true;
            return Task.FromResult(state);
        }

        public Task<PipelineRunState> UndoManualStatusAsync(
            PipelineRunState state,
            string fileReference,
            string actor,
            string overrideSource,
            CancellationToken cancellationToken = default)
        {
            LastFileReference = fileReference;
            UndoCalled = true;
            return Task.FromResult(state);
        }

        public Task<PipelineRunState> ResumeAsync(
            string pipelineRunDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PipelineRunState { PipelineRunDirectory = pipelineRunDirectory });
        }
    }
}
