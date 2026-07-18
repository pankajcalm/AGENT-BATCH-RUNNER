using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class PipelineStateStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RetainsQueueReviewHistoryAndTransitions()
    {
        using var workspace = TestWorkspace.Create();
        var store = new PipelineStateStore();
        var directory = store.CreatePipelineRunDirectory(workspace.Root, "pipeline-1");
        var state = new PipelineRunState
        {
            PipelineRunId = "pipeline-1",
            FolderPath = workspace.Root,
            RepoPath = workspace.Root,
            PipelineRunDirectory = directory,
            Status = PipelineRunStatus.Paused,
            StartedAt = DateTimeOffset.Now,
            Files =
            [
                new PipelineFileRunState
                {
                    PipelineId = "A",
                    FileName = "A.yaml",
                    Status = PipelineFileStatus.Blocked,
                    ReviewHistory =
                    [
                        new PipelineReviewResult
                        {
                            ReviewRunId = "review-1",
                            ReviewVerdict = PipelineReviewVerdict.Blocked,
                            Summary = "Gate blocked."
                        }
                    ]
                }
            ],
            Transitions =
            [
                new PipelineTransitionRecord
                {
                    Timestamp = DateTimeOffset.Now,
                    Event = "ReviewCompleted",
                    PipelineFileId = "A",
                    Message = "Blocked"
                }
            ]
        };

        await store.SaveStateAsync(state);
        var loaded = await store.LoadStateAsync(directory);

        Assert.Equal(PipelineRunStatus.Paused, loaded.Status);
        Assert.Equal(PipelineReviewVerdict.Blocked, Assert.Single(loaded.Files[0].ReviewHistory).ReviewVerdict);
        Assert.Equal("ReviewCompleted", Assert.Single(loaded.Transitions).Event);
        Assert.True(File.Exists(Path.Combine(directory, "pipeline-state.json")));
        Assert.True(File.Exists(Path.Combine(directory, "pipeline-summary.json")));
        Assert.True(File.Exists(Path.Combine(directory, "queue.json")));
    }
}
