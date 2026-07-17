using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class BatchRunnerTests
{
    [Fact]
    public async Task RunAsync_RetriesFailedVerificationUpToMaxRetries()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = repo.Root,
            DefaultAgent = "dryrun",
            DefaultMaxRetries = 2,
            Prompts =
            [
                new PromptTask
                {
                    Id = "P001",
                    Title = "Always fail",
                    Prompt = "Make this pass.",
                    Verify = ["exit 7"],
                    MaxRetries = 2
                }
            ]
        };

        var runner = CreateBatchRunner();
        var result = await runner.RunAsync(config, new RunOptions());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.NeedsHumanReview, task.Status);
        Assert.Equal(2, task.Attempts.Count);
        Assert.All(task.Attempts, a => Assert.Equal(RunStatus.Failed, a.Status));
        Assert.True(File.Exists(Path.Combine(task.TaskDirectory, "git-status-before.txt")));
        Assert.True(File.Exists(Path.Combine(task.TaskDirectory, "git-diff-after.patch")));
    }

    [Fact]
    public async Task RunAsync_PassingVerification_SucceedsInSingleAttempt()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = repo.Root,
            DefaultAgent = "dryrun",
            DefaultMaxRetries = 3,
            Prompts =
            [
                new PromptTask
                {
                    Id = "P001",
                    Title = "Passing task",
                    Prompt = "Do the thing.",
                    Verify = ["exit 0"]
                }
            ]
        };

        var result = await CreateBatchRunner().RunAsync(config, new RunOptions());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.Succeeded, task.Status);
        Assert.Single(task.Attempts);
        Assert.Equal(1, result.Succeeded);
        var verificationLog = Path.Combine(task.TaskDirectory, "attempts", "attempt-1", "verification.log");
        Assert.True(File.Exists(verificationLog));
    }

    [Fact]
    public async Task RunAsync_PublishesStructuredRunEvents()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var sink = new RecordingRunEventSink();
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = repo.Root,
            DefaultAgent = "dryrun",
            DefaultMaxRetries = 1,
            Prompts =
            [
                new PromptTask
                {
                    Id = "P001",
                    Title = "Evented task",
                    Prompt = "Do the thing.",
                    Verify = ["exit 0"]
                }
            ]
        };

        await CreateBatchRunner(eventSink: sink).RunAsync(config, new RunOptions());

        Assert.Contains(sink.Events, e => e.Kind == RunEventKind.RunStarted && e.RunId is not null);
        Assert.Contains(sink.Events, e => e.Kind == RunEventKind.TaskStarted && e.PromptId == "P001");
        Assert.Contains(sink.Events, e => e.Kind == RunEventKind.CheckpointCreated && e.PromptId == "P001");
        Assert.Contains(sink.Events, e => e.Kind == RunEventKind.AttemptStarted && e.AttemptNumber == 1);
        Assert.Contains(sink.Events, e => e.Kind == RunEventKind.AgentCompleted && e.PromptId == "P001");
        Assert.Contains(sink.Events, e => e.Kind == RunEventKind.VerificationStarted && e.Command == "exit 0");
        Assert.Contains(sink.Events, e => e.Kind == RunEventKind.VerificationPassed && e.Command == "exit 0");
        Assert.Contains(sink.Events, e => e.Kind == RunEventKind.TaskSucceeded && e.Status == RunStatus.Succeeded);
        Assert.Contains(sink.Events, e => e.Kind == RunEventKind.ReportGenerated && e.Path?.EndsWith("final-report.md", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(sink.Events, e => e.Kind == RunEventKind.RunCompleted);
    }

    [Fact]
    public async Task RunAsync_Resume_SkipsSucceededTasksAndReusesRunId()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = repo.Root,
            DefaultAgent = "dryrun",
            DefaultMaxRetries = 2,
            Prompts =
            [
                new PromptTask { Id = "P001", Title = "Already done", Prompt = "x", Verify = ["exit 0"] },
                new PromptTask { Id = "P002", Title = "Still pending", Prompt = "y", Verify = ["exit 0"] }
            ]
        };

        var existingResult = new RunResult
        {
            RunId = "20260626-120000",
            Project = config.Project,
            RepoPath = config.RepoPath,
            StartedAt = DateTimeOffset.Now,
            Tasks =
            [
                new TaskRunResult
                {
                    Id = "P001",
                    Title = "Already done",
                    Agent = "dryrun",
                    Status = RunStatus.Succeeded,
                    Attempts = [new AttemptResult { AttemptNumber = 1, Status = RunStatus.Succeeded }]
                }
            ]
        };

        var result = await CreateBatchRunner().RunAsync(
            config,
            new RunOptions
            {
                RunId = "20260626-120000",
                ExistingResult = existingResult,
                SkipPromptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P001" }
            });

        Assert.Equal("20260626-120000", result.RunId);
        Assert.Same(existingResult, result);

        var p001 = result.Tasks.Single(t => t.Id == "P001");
        Assert.Equal(RunStatus.Succeeded, p001.Status);
        Assert.Single(p001.Attempts); // skipped: never re-run

        var p002 = result.Tasks.Single(t => t.Id == "P002");
        Assert.Equal(RunStatus.Succeeded, p002.Status);
    }

    [Fact]
    public async Task RunAsync_NoVerifyCommands_YieldsUnverifiedSuccess()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = repo.Root,
            DefaultAgent = "dryrun",
            DefaultMaxRetries = 2,
            Prompts =
            [
                new PromptTask
                {
                    Id = "P001",
                    Title = "No verification",
                    Prompt = "Do the thing.",
                    Verify = []
                }
            ]
        };

        var result = await CreateBatchRunner().RunAsync(config, new RunOptions());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.UnverifiedSuccess, task.Status);
        Assert.Single(task.Attempts); // no retry: an unverified success is terminal
        Assert.Equal(1, result.UnverifiedSuccess);
        Assert.Equal(0, result.NeedsHumanReview);
    }

    [Fact]
    public async Task RunAsync_CliOverride_TakesPrecedenceOverPerPromptAndDefault()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = repo.Root,
            DefaultAgent = "claude",
            DefaultMaxRetries = 1,
            Prompts =
            [
                new PromptTask { Id = "P001", Title = "t", Prompt = "x", Agent = "claude", Verify = ["exit 0"] }
            ]
        };

        // dryrun wins, so the real claude CLI is never invoked.
        var result = await CreateBatchRunner().RunAsync(config, new RunOptions { AgentOverride = "dryrun" });

        Assert.Equal("dryrun", Assert.Single(result.Tasks).Agent);
    }

    [Fact]
    public async Task RunAsync_PerPromptAgent_TakesPrecedenceOverDefault()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = repo.Root,
            DefaultAgent = "claude",
            DefaultMaxRetries = 1,
            Prompts =
            [
                new PromptTask { Id = "P001", Title = "t", Prompt = "x", Agent = "dryrun", Verify = ["exit 0"] }
            ]
        };

        var result = await CreateBatchRunner().RunAsync(config, new RunOptions());

        Assert.Equal("dryrun", Assert.Single(result.Tasks).Agent);
    }

    [Fact]
    public async Task RunAsync_AgentTimeoutRetriesUntilMaxRetries()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = repo.Root,
            DefaultAgent = "dryrun",
            DefaultMaxRetries = 2,
            DefaultAgentTimeoutSeconds = 1,
            DefaultVerifyTimeoutSeconds = 30,
            Prompts =
            [
                new PromptTask
                {
                    Id = "P001",
                    Title = "Timed out agent",
                    Prompt = "Do the thing.",
                    Verify = ["exit 0"]
                }
            ]
        };

        var timeoutAdapter = new AlwaysTimeoutAgentAdapter();
        var result = await CreateBatchRunner(new TestAgentAdapterFactory(timeoutAdapter))
            .RunAsync(config, new RunOptions());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.NeedsHumanReview, task.Status);
        Assert.True(task.TimedOut);
        Assert.Equal("Agent command timed out after 1s.", task.LastFailureReason);
        Assert.Equal(2, task.Attempts.Count);
        Assert.Equal(2, timeoutAdapter.CallCount);
        Assert.Equal(2, result.TimedOutAttempts);
        Assert.All(task.Attempts, attempt =>
        {
            Assert.Equal(RunStatus.Failed, attempt.Status);
            Assert.True(attempt.TimedOut);
            Assert.Equal("Agent command timed out after 1s.", attempt.TimeoutReason);
            Assert.NotNull(attempt.AgentResult);
            Assert.True(attempt.AgentResult!.TimedOut);
            Assert.Equal(124, attempt.AgentResult.ExitCode);
        });

        var secondAgentOutput = await File.ReadAllTextAsync(
            Path.Combine(task.TaskDirectory, "attempts", "attempt-2", "agent-output.txt"));
        Assert.Contains("Timed out: True", secondAgentOutput);
    }

    [Fact]
    public async Task RunAsync_PreBlockedAgentPreventsInvocation()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var rateLimitStore = new AgentRateLimitStateStore(
            Path.Combine(temp.Root, "agent-rate-limits.json"),
            () => DateTimeOffset.Parse("2026-06-26T12:00:00Z"));
        rateLimitStore.SetBlocked(new AgentRateLimitInfo
        {
            AgentName = "claude",
            IsBlocked = true,
            BlockedUntil = DateTimeOffset.Parse("2026-06-26T13:00:00Z"),
            LastDetectedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            Reason = "Usage limit reached.",
            RawMessage = "Usage limit reached."
        });
        var adapter = new CountingAgentAdapter("claude", new AgentExecutionResult
        {
            AgentName = "claude",
            Command = "claude",
            ExitCode = 0
        });
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = repo.Root,
            DefaultAgent = "claude",
            DefaultMaxRetries = 3,
            Prompts =
            [
                new PromptTask { Id = "P001", Title = "Blocked", Prompt = "Do the thing.", Verify = ["exit 0"] }
            ]
        };

        var result = await CreateBatchRunner(
                new TestAgentAdapterFactory(adapter),
                rateLimitStateStore: rateLimitStore)
            .RunAsync(config, new RunOptions());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.RateLimited, task.Status);
        Assert.Equal(1, result.RateLimited);
        Assert.Equal(0, result.NeedsHumanReview);
        Assert.Equal(0, adapter.CallCount);
        var attempt = Assert.Single(task.Attempts);
        Assert.Equal(RunStatus.RateLimited, attempt.Status);
        Assert.NotNull(attempt.AgentResult);
        Assert.True(attempt.AgentResult!.IsRateLimited);
    }

    [Fact]
    public async Task RunAsync_DetectedRateLimitStopsWithoutConsumingRetries()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var rateLimitStore = new AgentRateLimitStateStore(
            Path.Combine(temp.Root, "agent-rate-limits.json"),
            () => now);
        var adapter = new CountingAgentAdapter("claude", new AgentExecutionResult
        {
            AgentName = "claude",
            Command = "claude -p prompt",
            ExitCode = 1,
            StandardError = "Usage limit reached. Retry after 15 minutes."
        });
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = repo.Root,
            DefaultAgent = "claude",
            DefaultMaxRetries = 3,
            Prompts =
            [
                new PromptTask { Id = "P001", Title = "Limit", Prompt = "Do the thing.", Verify = ["exit 0"] }
            ]
        };

        var result = await CreateBatchRunner(
                new TestAgentAdapterFactory(adapter),
                rateLimitStateStore: rateLimitStore)
            .RunAsync(config, new RunOptions());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.RateLimited, task.Status);
        Assert.Single(task.Attempts);
        Assert.Equal(1, adapter.CallCount);
        Assert.Equal(0, result.NeedsHumanReview);
        Assert.Equal(now.AddMinutes(15), task.RateLimitResetAt);
        Assert.True(rateLimitStore.TryGetBlocked("claude", out var stored));
        Assert.Equal(now.AddMinutes(15), stored.BlockedUntil);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".agentbatchrunner", "runs", result.RunId, "final-report.md")));
    }

    private static BatchRunner CreateBatchRunner(
        AgentAdapterFactory? agentAdapterFactory = null,
        IRunEventSink? eventSink = null,
        AgentRateLimitStateStore? rateLimitStateStore = null)
    {
        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var stateStore = new RunStateStore();
        var reportGenerator = new ReportGenerator(stateStore);
        return new BatchRunner(
            new PromptFileLoader(),
            new GitCheckpointManager(processRunner, logger),
            new VerificationRunner(processRunner, logger, eventSink),
            stateStore,
            reportGenerator,
            agentAdapterFactory ?? new AgentAdapterFactory(processRunner, logger),
            logger,
            eventSink,
            rateLimitStateStore: rateLimitStateStore);
    }

    private sealed class TestAgentAdapterFactory(IAgentAdapter adapter)
        : AgentAdapterFactory(new ProcessRunner(), new ConsoleLogger())
    {
        public override IAgentAdapter Create(string agent)
        {
            return adapter;
        }
    }

    private sealed class AlwaysTimeoutAgentAdapter : IAgentAdapter
    {
        public string Name => "dryrun";

        public int CallCount { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new AgentExecutionResult
            {
                AgentName = Name,
                Command = "fake-agent",
                ExitCode = 124,
                Duration = TimeSpan.FromSeconds(1),
                TimedOut = true,
                Timeout = TimeSpan.FromSeconds(request.Options.TimeoutSeconds),
                StandardOutput = "partial agent stdout",
                StandardError = "partial agent stderr"
            });
        }
    }

    private sealed class CountingAgentAdapter(string name, AgentExecutionResult result) : IAgentAdapter
    {
        public string Name => name;

        public int CallCount { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new AgentExecutionResult
            {
                AgentName = result.AgentName,
                Command = result.Command,
                ExitCode = result.ExitCode,
                Duration = result.Duration,
                TimedOut = result.TimedOut,
                Timeout = result.Timeout,
                StandardOutput = result.StandardOutput,
                StandardError = result.StandardError,
                SessionId = result.SessionId,
                IsRateLimited = result.IsRateLimited,
                RateLimitResetAt = result.RateLimitResetAt,
                RateLimitReason = result.RateLimitReason
            });
        }
    }

    private sealed class RecordingRunEventSink : IRunEventSink
    {
        public List<RunEvent> Events { get; } = [];

        public Task OnRunEventAsync(RunEvent runEvent, CancellationToken cancellationToken)
        {
            Events.Add(runEvent);
            return Task.CompletedTask;
        }
    }
}
