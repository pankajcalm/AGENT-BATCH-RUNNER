using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class GitCheckpointManagerTests
{
    [Fact]
    public async Task CreateCheckpoint_CreatesBranch_WithoutSwitchingOrMutatingTree()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var taskDirectory = CreateTaskDirectory(repo);
        var branchBefore = repo.CurrentBranch();
        var headBefore = repo.HeadCommit();

        var manager = new GitCheckpointManager(new ProcessRunner(), new ConsoleLogger());
        var checkpointId = await manager.CreateCheckpointAsync(repo.Root, taskDirectory, "P001");

        // A checkpoint branch was created at the current commit.
        Assert.StartsWith("agentbatchrunner/", checkpointId);
        var branches = repo.Git("branch --list " + checkpointId);
        Assert.Contains(checkpointId, branches);
        Assert.Equal(headBefore, repo.Git($"rev-parse {checkpointId}"));

        // The user's branch and HEAD are untouched: no switch, no new commit, no reset.
        Assert.Equal(branchBefore, repo.CurrentBranch());
        Assert.Equal(headBefore, repo.HeadCommit());
        Assert.True(File.Exists(Path.Combine(taskDirectory, "checkpoint.txt")));
        Assert.True(File.Exists(Path.Combine(taskDirectory, "git-status-before.txt")));
    }

    [Fact]
    public async Task CreateCheckpoint_DirtyTree_SavesDiffAndPreservesUncommittedChanges()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var taskDirectory = CreateTaskDirectory(repo);
        var dirtyFile = Path.Combine(repo.Root, "dirty.txt");
        await File.WriteAllTextAsync(dirtyFile, "uncommitted work");

        var manager = new GitCheckpointManager(new ProcessRunner(), new ConsoleLogger());
        await manager.CreateCheckpointAsync(repo.Root, taskDirectory, "P002");

        // The dirty change is recorded but NOT discarded (no reset/checkout happened).
        Assert.True(File.Exists(Path.Combine(taskDirectory, "git-diff-before.patch")));
        Assert.True(File.Exists(dirtyFile));
        Assert.Equal("uncommitted work", await File.ReadAllTextAsync(dirtyFile));
        Assert.Contains("dirty.txt", repo.Git("status --short"));
    }

    [Fact]
    public void EnsureRepository_Throws_WhenPathIsNotAGitRepo()
    {
        using var notARepo = TestWorkspace.Create();
        var manager = new GitCheckpointManager(new ProcessRunner(), new ConsoleLogger());

        Assert.Throws<InvalidOperationException>(() => manager.EnsureRepository(notARepo.Root));
    }

    private static string CreateTaskDirectory(TestWorkspace repo)
    {
        var taskDirectory = Path.Combine(repo.Root, ".agentbatchrunner", "runs", "test-run", "tasks", "P001");
        Directory.CreateDirectory(taskDirectory);
        return taskDirectory;
    }
}
