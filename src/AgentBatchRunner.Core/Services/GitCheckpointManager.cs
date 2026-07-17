using AgentBatchRunner.Infrastructure;

namespace AgentBatchRunner.Services;

public sealed class GitCheckpointManager(ProcessRunner processRunner, ConsoleLogger logger)
{
    public void EnsureRepository(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
        {
            throw new DirectoryNotFoundException($"Repository path does not exist: {repoPath}");
        }

        var dotGit = Path.Combine(repoPath, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
        {
            throw new InvalidOperationException($"Repository path is not a Git repository: {repoPath}");
        }
    }

    public async Task<string> CreateCheckpointAsync(
        string repoPath,
        string taskDirectory,
        string promptId,
        CancellationToken cancellationToken = default,
        TimeSpan? commandTimeout = null)
    {
        EnsureRepository(repoPath);

        var statusResult = await processRunner.RunExecutableAsync(
            "git",
            ["status", "--short", "--", ".", ":(exclude).agentbatchrunner"],
            repoPath,
            cancellationToken,
            timeout: commandTimeout);
        await Utf8File.WriteAllTextAsync(
            Path.Combine(taskDirectory, "git-status-before.txt"),
            statusResult.StandardOutput,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(statusResult.StandardOutput))
        {
            logger.Warning("Working tree is dirty. Initial status and diff will be saved; continuing.");
            var diffBefore = await processRunner.RunExecutableAsync(
                "git",
                ["diff", "--", ".", ":(exclude).agentbatchrunner"],
                repoPath,
                cancellationToken,
                timeout: commandTimeout);
            await Utf8File.WriteAllTextAsync(
                Path.Combine(taskDirectory, "git-diff-before.patch"),
                diffBefore.StandardOutput,
                cancellationToken);
        }

        var checkpointId = $"agentbatchrunner/{FileNameSanitizer.Sanitize(promptId)}-before-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        var branchResult = await processRunner.RunExecutableAsync(
            "git",
            ["branch", checkpointId],
            repoPath,
            cancellationToken,
            timeout: commandTimeout);

        if (branchResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not create checkpoint branch '{checkpointId}': {branchResult.CombinedOutput}");
        }

        await Utf8File.WriteAllTextAsync(
            Path.Combine(taskDirectory, "checkpoint.txt"),
            checkpointId,
            cancellationToken);
        logger.Info($"Created checkpoint branch {checkpointId}.");
        return checkpointId;
    }

    public async Task SaveDiffAfterAsync(
        string repoPath,
        string taskDirectory,
        CancellationToken cancellationToken = default,
        TimeSpan? commandTimeout = null)
    {
        var diffAfter = await processRunner.RunExecutableAsync(
            "git",
            ["diff", "--", ".", ":(exclude).agentbatchrunner"],
            repoPath,
            cancellationToken,
            timeout: commandTimeout);
        await Utf8File.WriteAllTextAsync(
            Path.Combine(taskDirectory, "git-diff-after.patch"),
            diffAfter.StandardOutput,
            cancellationToken);
    }
}
