using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class AgentSwitchingTests
{
    [Fact]
    public async Task PreBlockedClaude_AutomaticallyRoutesToCodexBeforeInvocation()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var now = DateTimeOffset.Parse("2026-07-17T12:00:00Z");
        var store = CreateStore(temp.Root, now);
        Block(store, "claude", now.AddHours(1));
        var factory = new ScriptedAgentFactory();
        factory.EnqueueSuccess("codex", "codex-session");

        var result = await CreateRunner(factory, store).RunAsync(
            CreateFallbackConfig(repo.Root, [Prompt("P001", "claude")]),
            new RunOptions());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.Succeeded, task.Status);
        Assert.Equal("claude", task.BaseAgent);
        Assert.Equal("codex", task.EffectiveAgent);
        Assert.Equal(AgentRoutingReason.RateLimitFallback, task.RoutingReason);
        var attempt = Assert.Single(task.Attempts);
        Assert.Equal("codex", attempt.AttemptAgent);
        Assert.DoesNotContain(factory.Invocations, call => call.Agent == "claude");
        Assert.Equal(1, result.AgentSwitches);
    }

    [Fact]
    public async Task ClaudeRateLimit_ContinuesSameTaskWithCodexWithoutConsumingRetry()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var factory = new ScriptedAgentFactory();
        factory.EnqueueRateLimit("claude");
        factory.EnqueueSuccess("codex", "codex-session");

        var result = await CreateRunner(factory, store).RunAsync(
            CreateFallbackConfig(repo.Root, [Prompt("P001", "claude")]),
            new RunOptions());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.Succeeded, task.Status);
        Assert.Equal(2, task.Attempts.Count);
        Assert.False(task.Attempts[0].ConsumesRetry);
        Assert.True(task.Attempts[1].ConsumesRetry);
        Assert.Equal(1, task.RetryAttemptsConsumed);
        Assert.Equal("claude", task.Attempts[0].AttemptAgent);
        Assert.Equal("codex", task.Attempts[1].AttemptAgent);
        Assert.Null(factory.Invocations.Single(call => call.Agent == "codex").SessionId);
        Assert.False(factory.Invocations.Single(call => call.Agent == "codex").ResumeSession);
        Assert.Contains("working tree may contain partially completed changes", factory.Invocations[1].Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerificationRetry_ResumesOnlyFallbackSession()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var factory = new ScriptedAgentFactory();
        factory.EnqueueRateLimit("claude");
        factory.EnqueueSuccess("codex", "codex-session");
        factory.EnqueueSuccess("codex", "codex-session");
        var task = Prompt("P001", "claude");
        task.MaxRetries = 2;
        task.Verify =
        [
            "if (Test-Path 'verification.marker') { exit 0 } else { Set-Content 'verification.marker' 'created'; exit 7 }"
        ];

        var result = await CreateRunner(factory, store).RunAsync(
            CreateFallbackConfig(repo.Root, [task]),
            new RunOptions());

        var taskResult = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.Succeeded, taskResult.Status);
        Assert.Equal(3, taskResult.Attempts.Count);
        Assert.Equal(2, taskResult.RetryAttemptsConsumed);
        var codexCalls = factory.Invocations.Where(call => call.Agent == "codex").ToList();
        Assert.Null(codexCalls[0].SessionId);
        Assert.False(codexCalls[0].ResumeSession);
        Assert.Equal("codex-session", codexCalls[1].SessionId);
        Assert.True(codexCalls[1].ResumeSession);
        Assert.DoesNotContain(codexCalls, call => call.SessionId?.StartsWith("claude", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task RateLimitFallback_ReroutesLaterMatchingPromptsButNotConfiguredCodexPrompt()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var factory = new ScriptedAgentFactory();
        factory.EnqueueRateLimit("claude");
        factory.EnqueueSuccess("codex", "c1");
        factory.EnqueueSuccess("codex", "c2");
        factory.EnqueueSuccess("codex", "c3");
        var config = CreateFallbackConfig(
            repo.Root,
            [Prompt("P001", "claude"), Prompt("P002", "claude"), Prompt("P003", "codex")]);

        var result = await CreateRunner(factory, store).RunAsync(config, new RunOptions());

        Assert.All(result.Tasks, task => Assert.Equal(RunStatus.Succeeded, task.Status));
        var p2 = result.Tasks.Single(task => task.Id == "P002");
        Assert.Equal("claude", p2.BaseAgent);
        Assert.Equal("codex", p2.EffectiveAgent);
        Assert.Equal(AgentRoutingReason.RateLimitFallback, p2.RoutingReason);
        var p3 = result.Tasks.Single(task => task.Id == "P003");
        Assert.Equal("codex", p3.BaseAgent);
        Assert.Equal("codex", p3.EffectiveAgent);
        Assert.Equal(AgentRoutingReason.Yaml, p3.RoutingReason);
        Assert.Single(result.RoutingChanges);
        Assert.Equal(["P001", "P002"], result.RoutingChanges[0].AffectedPromptIds);
    }

    [Fact]
    public async Task CompletedClaudePrompt_RemainsUnchangedWhenLaterClaudePromptSwitches()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var factory = new ScriptedAgentFactory();
        factory.EnqueueSuccess("claude", "claude-one");
        factory.EnqueueRateLimit("claude");
        factory.EnqueueSuccess("codex", "codex-two");

        var result = await CreateRunner(factory, store).RunAsync(
            CreateFallbackConfig(repo.Root, [Prompt("P001", "claude"), Prompt("P002", "claude")]),
            new RunOptions());

        var completed = result.Tasks.Single(task => task.Id == "P001");
        Assert.Equal("claude", completed.EffectiveAgent);
        Assert.Equal(AgentRoutingReason.Yaml, completed.RoutingReason);
        Assert.DoesNotContain("P001", Assert.Single(result.RoutingChanges).AffectedPromptIds);
    }

    [Fact]
    public async Task AutoSwitchDisabled_PreservesRateLimitedStopBehavior()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var factory = new ScriptedAgentFactory();
        factory.EnqueueRateLimit("claude");
        var config = CreateFallbackConfig(repo.Root, [Prompt("P001", "claude")]);
        config.AutoSwitchOnRateLimit = false;

        var result = await CreateRunner(factory, store).RunAsync(config, new RunOptions());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.RateLimited, task.Status);
        Assert.Single(task.Attempts);
        Assert.Empty(result.RoutingChanges);
        Assert.DoesNotContain(factory.Invocations, call => call.Agent == "codex");
    }

    [Fact]
    public async Task BlockedFallback_IsNeverInvoked()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var now = DateTimeOffset.Parse("2026-07-17T12:00:00Z");
        var store = CreateStore(temp.Root, now);
        Block(store, "claude", now.AddHours(1));
        Block(store, "codex", now.AddHours(1));
        var factory = new ScriptedAgentFactory();

        var result = await CreateRunner(factory, store).RunAsync(
            CreateFallbackConfig(repo.Root, [Prompt("P001", "claude")]),
            new RunOptions());

        Assert.Equal(RunStatus.RateLimited, Assert.Single(result.Tasks).Status);
        Assert.Empty(factory.Invocations);
        Assert.Empty(result.RoutingChanges);
    }

    [Fact]
    public async Task MaximumSwitchCount_PreventsSecondFallbackAndLoop()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var factory = new ScriptedAgentFactory();
        factory.EnqueueRateLimit("claude");
        factory.EnqueueRateLimit("codex");
        var config = CreateFallbackConfig(repo.Root, [Prompt("P001", "claude")]);
        config.RateLimitFallbacks["codex"] = ["dryrun"];
        config.MaxRateLimitAgentSwitchesPerTask = 1;

        var result = await CreateRunner(factory, store).RunAsync(config, new RunOptions());

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.RateLimited, task.Status);
        Assert.Equal(2, task.Attempts.Count);
        Assert.All(task.Attempts, attempt => Assert.False(attempt.ConsumesRetry));
        Assert.Equal(0, task.RetryAttemptsConsumed);
        Assert.DoesNotContain(factory.Invocations, call => call.Agent == "dryrun");
        Assert.Single(result.RoutingChanges);
    }

    [Fact]
    public async Task FallbackPreflightFailure_StopsBeforePromptAndCheckpoint()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var factory = new ScriptedAgentFactory();
        var preflight = new StaticPreflightService(new AgentPreflightResult
        {
            Succeeded = false,
            FailureReason = "Codex fallback executable is unusable.",
            Toolchains =
            [
                new AgentToolchainInfo
                {
                    AgentName = "codex",
                    Status = AgentPreflightStatus.Failed,
                    FailureReason = "Codex fallback executable is unusable."
                }
            ]
        });

        var result = await CreateRunner(factory, store, preflight).RunAsync(
            CreateFallbackConfig(repo.Root, [Prompt("P001", "claude")]),
            new RunOptions());

        Assert.Equal(RunFailureKind.PreflightFailed, result.FailureKind);
        Assert.Empty(factory.Invocations);
        Assert.Equal(RunStatus.Skipped, Assert.Single(result.Tasks).Status);
        Assert.False(File.Exists(Path.Combine(result.Tasks[0].TaskDirectory, "git-status-before.txt")));
    }

    [Fact]
    public async Task ManualSwitchQueuedDuringTask_AppliesOnlyAtNextPromptBoundary()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var factory = new BlockingAgentFactory();
        var routing = new RunAgentRoutingController();
        var config = CreateFallbackConfig(repo.Root, [Prompt("P001", "claude"), Prompt("P002", "claude")]);
        config.AutoSwitchOnRateLimit = false;
        var runTask = CreateRunner(factory, store).RunAsync(
            config,
            new RunOptions { RoutingController = routing });
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await routing.QueueSwitchAsync(
            new AgentSwitchRequest
            {
                SourceAgent = "claude",
                ReplacementAgent = "codex",
                Reason = AgentRoutingReason.ManualPendingOverride,
                AffectedPromptIds = ["P002"],
                StartingPromptId = "P002",
                UserConfirmed = true
            },
            CancellationToken.None);
        Assert.Empty(routing.CreateSnapshot("run").Changes);
        factory.Release.TrySetResult();

        var result = await runTask;

        Assert.Equal("claude", result.Tasks.Single(task => task.Id == "P001").EffectiveAgent);
        Assert.Equal("codex", result.Tasks.Single(task => task.Id == "P002").EffectiveAgent);
        Assert.Equal(AgentRoutingReason.ManualPendingOverride, result.Tasks.Single(task => task.Id == "P002").RoutingReason);
    }

    [Fact]
    public async Task CancellationWhileSwitchQueued_DoesNotApplyQueuedChange()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var factory = new BlockingAgentFactory();
        var routing = new RunAgentRoutingController();
        using var cancellation = new CancellationTokenSource();
        var runTask = CreateRunner(factory, store).RunAsync(
            CreateFallbackConfig(repo.Root, [Prompt("P001", "claude"), Prompt("P002", "claude")]),
            new RunOptions { RoutingController = routing },
            cancellation.Token);
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await routing.QueueSwitchAsync(
            new AgentSwitchRequest
            {
                SourceAgent = "claude",
                ReplacementAgent = "codex",
                Reason = AgentRoutingReason.ManualPendingOverride,
                AffectedPromptIds = ["P002"]
            },
            CancellationToken.None);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.Empty(routing.CreateSnapshot("run").Changes);
    }

    [Fact]
    public async Task Resume_RestoresRoutingAndAppendsAttemptWithoutOverwritingRateLimitArtifact()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var stateStore = new RunStateStore();
        var runId = "20260717-120000";
        var runDirectory = stateStore.CreateRunDirectory(repo.Root, runId);
        var taskDirectory = Path.Combine(runDirectory, "tasks", "P001");
        var firstAttemptDirectory = Path.Combine(taskDirectory, "attempts", "attempt-1");
        Directory.CreateDirectory(firstAttemptDirectory);
        await Utf8File.WriteAllTextAsync(Path.Combine(firstAttemptDirectory, "agent-output.txt"), "claude rate limited");
        var routing = new RunAgentRoutingController();
        routing.ApplySwitch(
            new AgentSwitchRequest
            {
                SourceAgent = "claude",
                ReplacementAgent = "codex",
                Reason = AgentRoutingReason.ManualPendingOverride,
                AffectedPromptIds = ["P001"],
                StartingPromptId = "P001",
                UserConfirmed = true,
                RetryRateLimitedTask = true
            },
            [new AgentRoutingCandidate { PromptId = "P001", BaseAgent = "claude", BaseRoutingReason = AgentRoutingReason.Yaml }]);
        await stateStore.SaveRoutingAsync(runDirectory, routing.CreateSnapshot(runId));
        var existing = new RunResult
        {
            RunId = runId,
            Project = "Demo",
            RepoPath = repo.Root,
            StartedAt = DateTimeOffset.Now,
            Tasks =
            [
                new TaskRunResult
                {
                    Id = "P001",
                    Title = "P001",
                    Agent = "claude",
                    EffectiveAgent = "claude",
                    BaseAgent = "claude",
                    Status = RunStatus.RateLimited,
                    CheckpointId = "existing-checkpoint",
                    TaskDirectory = taskDirectory,
                    Attempts =
                    [
                        new AttemptResult
                        {
                            AttemptNumber = 1,
                            AttemptAgent = "claude",
                            ConsumesRetry = false,
                            Status = RunStatus.RateLimited,
                            AttemptDirectory = firstAttemptDirectory,
                            AgentResult = new AgentExecutionResult
                            {
                                AgentName = "claude",
                                ExitCode = 75,
                                IsRateLimited = true,
                                StandardError = "Usage limit reached."
                            }
                        }
                    ]
                }
            ]
        };
        var factory = new ScriptedAgentFactory();
        factory.EnqueueSuccess("codex", "codex-session");
        var preflight = new RecordingSuccessfulPreflightService();
        var config = CreateFallbackConfig(repo.Root, [Prompt("P001", "claude")]);
        config.AutoSwitchOnRateLimit = false;

        var result = await CreateRunner(factory, store, preflight).RunAsync(
            config,
            new RunOptions { RunId = runId, ExistingResult = existing });

        var task = Assert.Single(result.Tasks);
        Assert.Equal(RunStatus.Succeeded, task.Status);
        Assert.Equal([1, 2], task.Attempts.Select(attempt => attempt.AttemptNumber));
        Assert.True(File.Exists(Path.Combine(firstAttemptDirectory, "agent-output.txt")));
        Assert.True(File.Exists(Path.Combine(taskDirectory, "attempts", "attempt-2", "agent-output.txt")));
        Assert.Equal("codex", Assert.Single(factory.Invocations).Agent);
        Assert.Equal(["codex"], Assert.Single(preflight.AgentSets));
        Assert.Contains("previous agent", factory.Invocations[0].Prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulFallback_ReportContainsRoutingAndAttemptDetails()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var store = CreateStore(temp.Root, DateTimeOffset.Parse("2026-07-17T12:00:00Z"));
        var factory = new ScriptedAgentFactory();
        factory.EnqueueRateLimit("claude");
        factory.EnqueueSuccess("codex", "codex-session");

        var result = await CreateRunner(factory, store).RunAsync(
            CreateFallbackConfig(repo.Root, [Prompt("P001", "claude")]),
            new RunOptions());
        var report = await Utf8File.ReadAllTextAsync(Path.Combine(
            repo.Root,
            ".agentbatchrunner",
            "runs",
            result.RunId,
            "final-report.md"));

        Assert.Contains("## Agent Routing Changes", report);
        Assert.Contains("RateLimitFallback", report);
        Assert.Contains("| P001 | 1 | claude | False", report);
        Assert.Contains("| P001 | 2 | codex | True", report);
    }

    private static BatchRunner CreateRunner(
        AgentAdapterFactory factory,
        AgentRateLimitStateStore store,
        IAgentPreflightService? preflight = null)
    {
        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var stateStore = new RunStateStore();
        return new BatchRunner(
            new PromptFileLoader(),
            new GitCheckpointManager(processRunner, logger),
            new VerificationRunner(processRunner, logger),
            stateStore,
            new ReportGenerator(stateStore),
            factory,
            logger,
            rateLimitStateStore: store,
            agentPreflightService: preflight ?? NoOpAgentPreflightService.Instance);
    }

    private static BatchConfig CreateFallbackConfig(string repoPath, List<PromptTask> prompts)
    {
        return new BatchConfig
        {
            Project = "Switching",
            RepoPath = repoPath,
            DefaultAgent = "claude",
            DefaultMaxRetries = 2,
            AutoSwitchOnRateLimit = true,
            MaxRateLimitAgentSwitchesPerTask = 1,
            RateLimitFallbacks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude"] = ["codex"]
            },
            Prompts = prompts
        };
    }

    private static PromptTask Prompt(string id, string agent)
    {
        return new PromptTask
        {
            Id = id,
            Title = id,
            Agent = agent,
            Prompt = $"Complete {id}.",
            Verify = ["exit 0"]
        };
    }

    private static AgentRateLimitStateStore CreateStore(string root, DateTimeOffset now)
    {
        return new AgentRateLimitStateStore(Path.Combine(root, "agent-rate-limits.json"), () => now);
    }

    private static void Block(AgentRateLimitStateStore store, string agent, DateTimeOffset until)
    {
        store.SetBlocked(new AgentRateLimitInfo
        {
            AgentName = agent,
            IsBlocked = true,
            BlockedUntil = until,
            LastDetectedAt = store.Now,
            Reason = "Usage limit reached.",
            RawMessage = "Usage limit reached."
        });
    }

    private sealed record Invocation(
        string Agent,
        string Prompt,
        string? SessionId,
        int AttemptNumber,
        bool ResumeSession);

    private sealed class ScriptedAgentFactory : AgentAdapterFactory
    {
        private readonly Dictionary<string, Queue<Func<AgentExecutionRequest, AgentExecutionResult>>> _scripts =
            new(StringComparer.OrdinalIgnoreCase);

        public ScriptedAgentFactory()
            : base(new ProcessRunner(), new ConsoleLogger())
        {
        }

        public List<Invocation> Invocations { get; } = [];

        public void EnqueueSuccess(string agent, string sessionId)
        {
            Enqueue(agent, _ => new AgentExecutionResult
            {
                AgentName = agent,
                Command = $"fake-{agent}",
                ExitCode = 0,
                SessionId = sessionId
            });
        }

        public void EnqueueRateLimit(string agent)
        {
            Enqueue(agent, _ => new AgentExecutionResult
            {
                AgentName = agent,
                Command = $"fake-{agent}",
                ExitCode = 1,
                StandardError = "Usage limit reached. Retry after 15 minutes."
            });
        }

        public void Enqueue(string agent, Func<AgentExecutionRequest, AgentExecutionResult> resultFactory)
        {
            if (!_scripts.TryGetValue(agent, out var queue))
            {
                queue = new Queue<Func<AgentExecutionRequest, AgentExecutionResult>>();
                _scripts[agent] = queue;
            }

            queue.Enqueue(resultFactory);
        }

        public override IAgentAdapter Create(string agent)
        {
            return new ScriptedAdapter(agent, this);
        }

        private sealed class ScriptedAdapter(string name, ScriptedAgentFactory owner) : IAgentAdapter
        {
            public string Name => name;

            public Task<AgentExecutionResult> ExecuteAsync(
                AgentExecutionRequest request,
                CancellationToken cancellationToken)
            {
                owner.Invocations.Add(new Invocation(
                    name,
                    request.Prompt,
                    request.SessionId,
                    request.AttemptNumber,
                    request.ShouldResumeSession));
                if (!owner._scripts.TryGetValue(name, out var queue) || queue.Count == 0)
                {
                    throw new InvalidOperationException($"No scripted result is available for {name}.");
                }

                return Task.FromResult(queue.Dequeue()(request));
            }
        }
    }

    private sealed class BlockingAgentFactory : AgentAdapterFactory
    {
        public BlockingAgentFactory()
            : base(new ProcessRunner(), new ConsoleLogger())
        {
        }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override IAgentAdapter Create(string agent)
        {
            return string.Equals(agent, "claude", StringComparison.OrdinalIgnoreCase)
                ? new BlockingAdapter(this)
                : new ImmediateAdapter(agent);
        }

        private sealed class BlockingAdapter(BlockingAgentFactory owner) : IAgentAdapter
        {
            public string Name => "claude";

            public async Task<AgentExecutionResult> ExecuteAsync(
                AgentExecutionRequest request,
                CancellationToken cancellationToken)
            {
                owner.Started.TrySetResult();
                await owner.Release.Task.WaitAsync(cancellationToken);
                return new AgentExecutionResult { AgentName = Name, Command = "fake-claude", ExitCode = 0, SessionId = "claude-session" };
            }
        }

        private sealed class ImmediateAdapter(string name) : IAgentAdapter
        {
            public string Name => name;

            public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new AgentExecutionResult
                {
                    AgentName = name,
                    Command = $"fake-{name}",
                    ExitCode = 0,
                    SessionId = $"{name}-session"
                });
            }
        }
    }

    private sealed class StaticPreflightService(AgentPreflightResult result) : IAgentPreflightService
    {
        public Task<AgentPreflightResult> RunAsync(
            BatchConfig config,
            IReadOnlyCollection<string> effectiveAgents,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingSuccessfulPreflightService : IAgentPreflightService
    {
        public List<IReadOnlyList<string>> AgentSets { get; } = [];

        public Task<AgentPreflightResult> RunAsync(
            BatchConfig config,
            IReadOnlyCollection<string> effectiveAgents,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            AgentSets.Add(effectiveAgents.ToList());
            return Task.FromResult(new AgentPreflightResult { Succeeded = true });
        }
    }
}
