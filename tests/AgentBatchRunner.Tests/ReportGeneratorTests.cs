using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class ReportGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_WritesMarkdownAndJsonSummary()
    {
        using var temp = TestWorkspace.Create();
        var stateStore = new RunStateStore();
        var generator = new ReportGenerator(stateStore);
        var runDirectory = Path.Combine(temp.Root, ".agentbatchrunner", "runs", "20260625-183000");
        Directory.CreateDirectory(runDirectory);

        var result = new RunResult
        {
            RunId = "20260625-183000",
            Project = "Demo",
            RepoPath = temp.Root,
            StartedAt = DateTimeOffset.Parse("2026-06-25T18:30:00Z"),
            CompletedAt = DateTimeOffset.Parse("2026-06-25T18:31:00Z"),
            Tasks =
            [
                new TaskRunResult
                {
                    Id = "P001",
                    Title = "Task",
                    Agent = "dryrun",
                    Status = RunStatus.Succeeded,
                    Attempts = [new AttemptResult { AttemptNumber = 1 }]
                }
            ]
        };

        await generator.GenerateAsync(runDirectory, result);

        var reportPath = Path.Combine(runDirectory, "final-report.md");
        Assert.True(File.Exists(reportPath));
        Assert.True(File.Exists(Path.Combine(runDirectory, "run-summary.json")));
        var markdown = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("# AgentBatchRunner Final Report", markdown);
        Assert.Contains("| P001 | Task | (none) | (none) | (none) | dryrun | 1 | Succeeded |", markdown);
    }

    [Fact]
    public async Task GenerateAsync_WritesTimeoutCountsToJsonSummary()
    {
        using var temp = TestWorkspace.Create();
        var runDirectory = Path.Combine(temp.Root, ".agentbatchrunner", "runs", "20260626-090000");
        Directory.CreateDirectory(runDirectory);

        var result = new RunResult
        {
            RunId = "20260626-090000",
            Project = "Demo",
            RepoPath = temp.Root,
            StartedAt = DateTimeOffset.Parse("2026-06-26T09:00:00Z"),
            Tasks =
            [
                new TaskRunResult
                {
                    Id = "P001",
                    Title = "Timed out task",
                    Agent = "claude",
                    Status = RunStatus.NeedsHumanReview,
                    TimedOut = true,
                    Attempts = [new AttemptResult { AttemptNumber = 1, TimedOut = true }]
                }
            ]
        };

        await new ReportGenerator(new RunStateStore()).GenerateAsync(runDirectory, result);

        var json = await File.ReadAllTextAsync(Path.Combine(runDirectory, "run-summary.json"));
        Assert.Contains("\"timedOutTasks\": 1", json);
        Assert.Contains("\"timedOutAttempts\": 1", json);
    }

    [Fact]
    public void BuildMarkdown_ReportsAccurateCountsAndFailedTaskDetails()
    {
        var generator = new ReportGenerator(new RunStateStore());
        var result = new RunResult
        {
            RunId = "20260626-090000",
            Project = "Demo",
            Tasks =
            [
                new TaskRunResult
                {
                    Id = "P001",
                    Title = "Passing task",
                    Agent = "dryrun",
                    Status = RunStatus.Succeeded,
                    Attempts = [new AttemptResult { AttemptNumber = 1 }]
                },
                new TaskRunResult
                {
                    Id = "P002",
                    Title = "Stuck task",
                    Agent = "claude",
                    Status = RunStatus.NeedsHumanReview,
                    Attempts =
                    [
                        new AttemptResult { AttemptNumber = 1, TimedOut = true },
                        new AttemptResult { AttemptNumber = 2, TimedOut = true }
                    ],
                    TimedOut = true,
                    LastFailedVerificationCommand = "dotnet test",
                    LastFailedExitCode = 124,
                    LastFailedLogPath = @"C:\runs\P002\attempts\attempt-2\verification.log",
                    LastFailureReason = "Verification command timed out after 900s."
                }
            ]
        };

        var markdown = generator.BuildMarkdown(result);

        Assert.Equal(2, result.TotalPrompts);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.NeedsHumanReview);
        Assert.Contains("- Total prompts: 2", markdown);
        Assert.Contains("- Succeeded: 1", markdown);
        Assert.Contains("- Needs human review: 1", markdown);
        Assert.Contains("- Timed out tasks: 1", markdown);
        Assert.Contains("- Timed out attempts: 2", markdown);
        // Failed-tasks section surfaces the actionable failure metadata.
        Assert.Contains("### P002 - Stuck task", markdown);
        Assert.Contains("Last failed verification command: dotnet test", markdown);
        Assert.Contains("Exit code: 124", markdown);
        Assert.Contains("Failure reason: Verification command timed out after 900s.", markdown);
        Assert.Contains("verification.log", markdown);
        // A succeeded task is not listed as failed.
        Assert.DoesNotContain("### P001", markdown);
    }

    [Fact]
    public void BuildMarkdown_ReportsRateLimitedTaskDetails()
    {
        var generator = new ReportGenerator(new RunStateStore());
        var result = new RunResult
        {
            RunId = "20260626-100000",
            Project = "Demo",
            Tasks =
            [
                new TaskRunResult
                {
                    Id = "P003",
                    Title = "Blocked task",
                    Agent = "codex",
                    Status = RunStatus.RateLimited,
                    Attempts = [new AttemptResult { AttemptNumber = 1, Status = RunStatus.RateLimited }],
                    RateLimitResetAt = DateTimeOffset.Parse("2026-06-26T20:30:00Z"),
                    LastFailedLogPath = @"C:\runs\P003\attempts\attempt-1\agent-output.txt",
                    LastFailureReason = "Codex is rate-limited until 2026-06-26 16:30:00 -04:00."
                }
            ]
        };

        var markdown = generator.BuildMarkdown(result);

        Assert.Equal(1, result.RateLimited);
        Assert.Contains("- Rate limited: 1", markdown);
        Assert.Contains("## Rate Limited Tasks", markdown);
        Assert.Contains("### P003 - Blocked task", markdown);
        Assert.Contains("Agent: codex", markdown);
        Assert.Contains("agent-output.txt", markdown);
        Assert.Contains("No failed tasks.", markdown);
    }
}
