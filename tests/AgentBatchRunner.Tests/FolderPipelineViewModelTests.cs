using System.Windows.Threading;
using AgentBatchRunner.Gui.Services;
using AgentBatchRunner.Gui.ViewModels;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class FolderPipelineViewModelTests
{
    [Fact]
    public async Task ScanFolder_PopulatesEveryPlannedQueueRow()
    {
        using var workspace = TestWorkspace.Create();
        var coordinator = new FakeGuiPipelineCoordinator
        {
            Plan = new PipelinePlan
            {
                FolderPath = workspace.Root,
                Files =
                [
                    PipelineFile("A", "10_a.yaml", workspace.Root),
                    PipelineFile("B", "20_b.yaml", workspace.Root)
                ]
            }
        };
        var viewModel = new FolderPipelineViewModel(
            Dispatcher.CurrentDispatcher,
            coordinator,
            () => workspace.Root,
            _ => true)
        {
            FolderPath = workspace.Root
        };

        await viewModel.ScanFolderAsync();

        Assert.Equal(2, viewModel.Files.Count);
        Assert.Equal(["A", "B"], viewModel.Files.Select(file => file.PipelineId));
        Assert.Equal("Planned", viewModel.PipelineStateText);
    }

    [Fact]
    public void LoadState_MapsVerdictRecommendationAndEvidence()
    {
        using var workspace = TestWorkspace.Create();
        var viewModel = new FolderPipelineViewModel(
            Dispatcher.CurrentDispatcher,
            new FakeGuiPipelineCoordinator());
        var reportPath = Path.Combine(workspace.Root, "review.md");
        var state = new PipelineRunState
        {
            PipelineRunId = "pipeline-1",
            RepoPath = workspace.Root,
            PipelineRunDirectory = workspace.Root,
            Status = PipelineRunStatus.Paused,
            NextDecision = new NextPipelineFileDecision
            {
                FilePath = Path.Combine(workspace.Root, "20_b.yaml"),
                RequiresHumanConfirmation = true,
                Reason = "Approved; confirm next."
            },
            Files =
            [
                new PipelineFileRunState
                {
                    QueueOrder = 1,
                    PipelineId = "A",
                    FileName = "10_a.yaml",
                    FilePath = Path.Combine(workspace.Root, "10_a.yaml"),
                    Status = PipelineFileStatus.Approved,
                    ExecutionStatus = RunStatus.Succeeded,
                    ReviewVerdict = PipelineReviewVerdict.Approved,
                    ReviewReportPath = reportPath,
                    RecommendedNextFile = "20_b.yaml",
                    Findings =
                    [
                        new PipelineReviewFinding
                        {
                            Id = "W-1",
                            Severity = PipelineFindingSeverity.Warning,
                            Title = "Document warning"
                        }
                    ]
                }
            ]
        };

        viewModel.LoadState(state);

        var row = Assert.Single(viewModel.Files);
        Assert.Equal("Approved", row.ReviewVerdict);
        Assert.Equal("20_b.yaml", row.RecommendedNextFile);
        Assert.Contains("W-1", row.FindingsText);
        Assert.Equal(reportPath, row.ReviewReportPath);
        Assert.True(viewModel.RunRecommendedNextCommand.CanExecute(null));
    }

    [Fact]
    public void LoadState_BlockedPipelineDisablesFurtherExecutionCommands()
    {
        var viewModel = new FolderPipelineViewModel(
            Dispatcher.CurrentDispatcher,
            new FakeGuiPipelineCoordinator());
        viewModel.LoadState(new PipelineRunState
        {
            Status = PipelineRunStatus.Blocked,
            NextDecision = new NextPipelineFileDecision
            {
                FilePath = @"C:\pipeline\repair.yaml",
                RequiresHumanConfirmation = true
            },
            Files =
            [
                new PipelineFileRunState
                {
                    PipelineId = "BLOCKED",
                    FileName = "blocked.yaml",
                    Status = PipelineFileStatus.Blocked
                }
            ]
        });

        Assert.False(viewModel.RunRecommendedNextCommand.CanExecute(null));
        Assert.False(viewModel.RunPipelineCommand.CanExecute(null));
        Assert.True(viewModel.ApproveNextCommand.CanExecute(null));
    }

    [Fact]
    public async Task ManualStatus_MapsDetailsAndEnablesUndoOnly()
    {
        using var workspace = TestWorkspace.Create();
        var coordinator = new FakeGuiPipelineCoordinator
        {
            Plan = new PipelinePlan
            {
                FolderPath = workspace.Root,
                Files = [PipelineFile("A", "10_a.yaml", workspace.Root)]
            }
        };
        var viewModel = new FolderPipelineViewModel(Dispatcher.CurrentDispatcher, coordinator)
        {
            FolderPath = workspace.Root
        };
        await viewModel.ScanFolderAsync();
        viewModel.LoadState(new PipelineRunState
        {
            PipelineRunId = "manual",
            RepoPath = workspace.Root,
            PipelineRunDirectory = workspace.Root,
            Status = PipelineRunStatus.Paused,
            Files =
            [
                new PipelineFileRunState
                {
                    QueueOrder = 1,
                    PipelineId = "A",
                    FileName = "10_a.yaml",
                    FilePath = Path.Combine(workspace.Root, "10_a.yaml"),
                    Status = PipelineFileStatus.ManuallyCompleted,
                    ManualReason = "Reviewed externally.",
                    ManualTimestamp = DateTimeOffset.Now,
                    ManualEvidencePath = Path.Combine(workspace.Root, "evidence.md"),
                    ManualSatisfiesDependencies = true
                }
            ]
        });

        var row = Assert.Single(viewModel.Files);
        Assert.Equal("Reviewed externally.", row.ManualReason);
        Assert.Equal("Accepted", row.DependencySatisfactionText);
        Assert.Contains("dependency accepted", row.ManualStatusIndicator, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.UndoManualStatusCommand.CanExecute(null));
        Assert.False(viewModel.SkipSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task SkipSelected_UsesPromptAndRefreshesCurrentRow()
    {
        using var workspace = TestWorkspace.Create();
        var file = PipelineFile("A", "10_a.yaml", workspace.Root);
        var state = new PipelineRunState
        {
            PipelineRunId = "manual",
            RepoPath = workspace.Root,
            PipelineRunDirectory = workspace.Root,
            Status = PipelineRunStatus.Paused,
            Files =
            [
                new PipelineFileRunState
                {
                    QueueOrder = 1,
                    PipelineId = "A",
                    FileName = file.FileName,
                    FilePath = file.FilePath,
                    Status = PipelineFileStatus.Eligible
                }
            ]
        };
        var coordinator = new FakeGuiPipelineCoordinator
        {
            Plan = new PipelinePlan { FolderPath = workspace.Root, Files = [file] },
            CreateState = state
        };
        var viewModel = new FolderPipelineViewModel(
            Dispatcher.CurrentDispatcher,
            coordinator,
            manualActionPrompt: _ => new PipelineManualActionRequest
            {
                Reason = "Not needed in this run.",
                Actor = "tester",
                OverrideSource = "view-model test"
            })
        {
            FolderPath = workspace.Root
        };
        await viewModel.ScanFolderAsync();

        await viewModel.SkipSelectedFileAsync();

        Assert.Equal("A", coordinator.LastFileReference);
        Assert.Equal("Not needed in this run.", coordinator.LastManualRequest?.Reason);
        Assert.Equal(PipelineFileStatus.SkippedByUser, Assert.Single(viewModel.Files).Status);
    }

    [Fact]
    public async Task StartFromBlockedSelection_OffersAndSelectsPrerequisite()
    {
        using var workspace = TestWorkspace.Create();
        var first = PipelineFile("A", "10_a.yaml", workspace.Root);
        var second = PipelineFile("B", "20_b.yaml", workspace.Root);
        var state = new PipelineRunState
        {
            PipelineRunId = "manual",
            RepoPath = workspace.Root,
            PipelineRunDirectory = workspace.Root,
            Status = PipelineRunStatus.Paused,
            Files =
            [
                new PipelineFileRunState { QueueOrder = 1, PipelineId = "A", FileName = first.FileName, FilePath = first.FilePath, Status = PipelineFileStatus.Eligible },
                new PipelineFileRunState { QueueOrder = 2, PipelineId = "B", FileName = second.FileName, FilePath = second.FilePath, Status = PipelineFileStatus.Pending, DependencyIds = ["A"] }
            ]
        };
        var coordinator = new FakeGuiPipelineCoordinator
        {
            Plan = new PipelinePlan { FolderPath = workspace.Root, Files = [first, second] },
            CreateState = state,
            StartPlan = new PipelineStartFromPlan
            {
                TargetFileId = "B",
                TargetFilePath = second.FilePath,
                CanStart = false,
                UnmetPrerequisiteIds = ["A"],
                Reason = "A is required."
            }
        };
        var viewModel = new FolderPipelineViewModel(
            Dispatcher.CurrentDispatcher,
            coordinator,
            startFromPrompt: _ => new PipelineStartFromDialogResult { SelectPrerequisiteId = "A" })
        {
            FolderPath = workspace.Root
        };
        await viewModel.ScanFolderAsync();
        viewModel.SelectedFile = viewModel.Files.Single(file => file.PipelineId == "B");

        await viewModel.StartPipelineFromSelectedAsync();

        Assert.Equal("A", viewModel.SelectedFile?.PipelineId);
        Assert.False(coordinator.StartFromCalled);
    }

    private static PipelineFile PipelineFile(string id, string fileName, string repoPath)
    {
        return new PipelineFile
        {
            FilePath = Path.Combine(repoPath, fileName),
            RelativePath = fileName,
            FileName = fileName,
            Config = new BatchConfig
            {
                Project = id,
                RepoPath = repoPath,
                DefaultAgent = "dryrun",
                Pipeline = new PipelineMetadata { Id = id, Title = id, Order = id == "A" ? 10 : 20 },
                Prompts = [new PromptTask { Id = id + "-P1", Title = "Task", Prompt = "Work" }]
            }
        };
    }

    private sealed class FakeGuiPipelineCoordinator : IGuiPipelineCoordinator
    {
        public PipelinePlan Plan { get; set; } = new();

        public PipelineRunState CreateState { get; set; } = new();

        public PipelineStartFromPlan StartPlan { get; set; } = new() { CanStart = true };

        public string? LastFileReference { get; private set; }

        public PipelineManualActionRequest? LastManualRequest { get; private set; }

        public bool StartFromCalled { get; private set; }

        public event EventHandler<PipelineEvent>? PipelineEventReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<RunEvent>? RunEventReceived
        {
            add { }
            remove { }
        }

        public Task<PipelinePlan> PlanAsync(string folderPath, CancellationToken cancellationToken)
            => Task.FromResult(Plan);

        public Task<PipelineRunState> CreateAsync(string folderPath, PipelineRunOptions options, CancellationToken cancellationToken)
            => Task.FromResult(CreateState);

        public Task<PipelineRunState> RunSelectedAsync(PipelineRunState state, string fileReference, PipelineRunControl control, CancellationToken cancellationToken)
            => Task.FromResult(state);

        public Task<PipelineRunState> RunPipelineAsync(PipelineRunState state, string? initialFileReference, PipelineRunControl control, CancellationToken cancellationToken)
            => Task.FromResult(state);

        public Task<PipelineRunState> RunRecommendedNextAsync(PipelineRunState state, bool userConfirmed, PipelineRunControl control, CancellationToken cancellationToken)
            => Task.FromResult(state);

        public Task<PipelineRunState> SkipAsync(PipelineRunState state, string fileReference, PipelineManualActionRequest request, CancellationToken cancellationToken)
        {
            LastFileReference = fileReference;
            LastManualRequest = request;
            var file = state.Files.Single(item => item.PipelineId == fileReference);
            file.Status = PipelineFileStatus.SkippedByUser;
            file.ManualReason = request.Reason;
            return Task.FromResult(state);
        }

        public Task<PipelineRunState> CompleteManuallyAsync(PipelineRunState state, string fileReference, PipelineManualActionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(state);

        public PipelineStartFromPlan PlanStartFrom(PipelineRunState state, string fileReference)
            => StartPlan;

        public Task<PipelineRunState> StartFromSelectedAsync(PipelineRunState state, string fileReference, PipelineStartFromRequest request, PipelineRunControl control, CancellationToken cancellationToken)
        {
            StartFromCalled = true;
            return Task.FromResult(state);
        }

        public Task<PipelineRunState> UndoManualStatusAsync(PipelineRunState state, string fileReference, string actor, string overrideSource, CancellationToken cancellationToken)
            => Task.FromResult(state);

        public Task<PipelineRunState> ResumeAsync(string pipelineRunDirectory, CancellationToken cancellationToken)
            => Task.FromResult(new PipelineRunState());

        public string? FindLatestPipelineDirectory(string repoPath) => null;
    }
}
