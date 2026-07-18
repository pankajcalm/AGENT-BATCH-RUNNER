using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class PipelineReviewRunnerTests
{
    [Fact]
    public async Task ExecuteAsync_ParsesPersistsAndVerifiesReviewWithoutProductChanges()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var setup = await CreateReviewSetupAsync(repo.Root);
        var adapter = new StaticReviewAdapter(BuildReviewJson(setup.Source.FileName, setup.Execution.RunId));

        var result = await CreateRunner(repo.Root, adapter).ExecuteAsync(
            setup.Generated,
            setup.Source,
            setup.Execution,
            setup.PipelineRunDirectory);

        Assert.True(
            result.ReviewResult.ReviewVerdict == PipelineReviewVerdict.Approved,
            result.ReviewResult.FailureReason ?? result.ReviewResult.Summary);
        Assert.True(result.ReviewResult.GateApproved);
        Assert.True(result.VerificationResult?.Succeeded);
        Assert.False(result.ProductFilesChanged);
        Assert.True(File.Exists(setup.Generated.ReviewResultPath));
        Assert.True(File.Exists(setup.Generated.ReviewReportPath));
        Assert.True(File.Exists(Path.Combine(result.ReviewRunDirectory, "agent-output.txt")));
        Assert.Equal("read-only", adapter.LastRequest?.Options.CodexSandbox);
        Assert.Equal("plan", adapter.LastRequest?.Options.ClaudePermissionMode);
        Assert.False(adapter.LastRequest?.ShouldResumeSession);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidReviewJsonStopsAsReviewFailed()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var setup = await CreateReviewSetupAsync(repo.Root);

        var result = await CreateRunner(repo.Root, new StaticReviewAdapter("not review json"))
            .ExecuteAsync(setup.Generated, setup.Source, setup.Execution, setup.PipelineRunDirectory);

        Assert.Equal(PipelineReviewVerdict.ReviewFailed, result.ReviewResult.ReviewVerdict);
        Assert.Contains("JSON", result.ReviewResult.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(setup.Generated.ReviewResultPath));
    }

    [Fact]
    public async Task ExecuteAsync_ProductMutationStopsWithoutReset()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        var setup = await CreateReviewSetupAsync(repo.Root);
        var adapter = new MutatingReviewAdapter(BuildReviewJson(setup.Source.FileName, setup.Execution.RunId));

        var result = await CreateRunner(repo.Root, adapter).ExecuteAsync(
            setup.Generated,
            setup.Source,
            setup.Execution,
            setup.PipelineRunDirectory);

        Assert.Equal(PipelineReviewVerdict.ReviewFailed, result.ReviewResult.ReviewVerdict);
        Assert.True(result.ProductFilesChanged);
        Assert.True(File.Exists(Path.Combine(repo.Root, "review-violation.txt")));
        Assert.Contains("without reset", result.ReviewResult.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    private static PipelineReviewRunner CreateRunner(string repoPath, IAgentAdapter adapter)
    {
        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var stateStore = new RunStateStore();
        return new PipelineReviewRunner(
            new PromptFileLoader(),
            new StaticAdapterProvider(adapter),
            NoOpAgentPreflightService.Instance,
            new VerificationRunner(processRunner, logger),
            processRunner,
            stateStore,
            new PipelineReviewResultParser(),
            new PipelineReviewReportGenerator(stateStore),
            new AgentRateLimitDetector(),
            new AgentRateLimitStateStore(Path.Combine(repoPath, ".agentbatchrunner", "test-limits.json")));
    }

    private static async Task<ReviewSetup> CreateReviewSetupAsync(string repoPath)
    {
        var sourcePath = Path.Combine(repoPath, "11_phase.yaml");
        await Utf8File.WriteAllTextAsync(sourcePath, PipelineDiscoveryTests.ValidBatchYaml(repoPath));
        var source = new PipelineFile
        {
            FilePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            RelativePath = Path.GetFileName(sourcePath),
            Config = new BatchConfig
            {
                Project = "Pipeline review",
                RepoPath = repoPath,
                DefaultAgent = "codex",
                Pipeline = new PipelineMetadata
                {
                    Id = "PHASE",
                    Title = "Phase",
                    Gate = new PipelineGateMetadata { Id = "G0", RequiredForNextPhase = true },
                    Review = new PipelineReviewMetadata { Required = true, Agent = "codex" }
                },
                Prompts = [new PromptTask { Id = "P001", Title = "Task", Prompt = "Work", Verify = [] }]
            }
        };
        var execution = new RunResult
        {
            RunId = "execution-1",
            RepoPath = repoPath,
            Tasks = [new TaskRunResult { Id = "P001", Status = RunStatus.Succeeded }]
        };
        var executionDirectory = Path.Combine(repoPath, ".agentbatchrunner", "runs", execution.RunId);
        Directory.CreateDirectory(executionDirectory);
        await Utf8File.WriteAllTextAsync(Path.Combine(executionDirectory, "final-report.md"), "report");
        await Utf8File.WriteAllTextAsync(Path.Combine(executionDirectory, "run-summary.json"), "{}");
        var pipelineDirectory = Path.Combine(repoPath, ".agentbatchrunner", "pipelines", "pipeline-1");
        var generated = await new PipelineReviewYamlGenerator().GenerateAsync(new PipelineReviewGenerationRequest
        {
            SourceFile = source,
            ExecutionResult = execution,
            ExecutionRunDirectory = executionDirectory,
            PipelineRunDirectory = pipelineDirectory,
            ReviewAgent = "codex"
        });
        return new ReviewSetup(source, execution, pipelineDirectory, generated);
    }

    private static string BuildReviewJson(string sourceFile, string executionRunId)
    {
        return
            "{" +
            "\"schemaVersion\":\"1.0\"," +
            $"\"sourcePipelineFile\":\"{sourceFile}\"," +
            $"\"executionRunId\":\"{executionRunId}\"," +
            "\"executionStatus\":\"Succeeded\"," +
            "\"reviewVerdict\":\"Approved\"," +
            "\"gateId\":\"G0\"," +
            "\"gateApproved\":true," +
            "\"summary\":\"Gate evidence is complete.\"," +
            "\"findings\":[]," +
            "\"requiredDecisions\":[]," +
            "\"recommendedNextFile\":null," +
            "\"canAutoAdvance\":true" +
            "}";
    }

    private sealed record ReviewSetup(
        PipelineFile Source,
        RunResult Execution,
        string PipelineRunDirectory,
        GeneratedPipelineReview Generated);

    private sealed class StaticAdapterProvider(IAgentAdapter adapter) : IAgentAdapterProvider
    {
        public IAgentAdapter Create(string agent)
        {
            return adapter;
        }
    }

    private class StaticReviewAdapter(string output) : IAgentAdapter
    {
        public string Name => "codex";

        public AgentExecutionRequest? LastRequest { get; private set; }

        public virtual Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new AgentExecutionResult
            {
                AgentName = Name,
                Command = "fake-review",
                ExitCode = 0,
                StandardOutput = output
            });
        }
    }

    private sealed class MutatingReviewAdapter(string output) : StaticReviewAdapter(output)
    {
        public override Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            File.WriteAllText(Path.Combine(request.RepoPath, "review-violation.txt"), "must remain for inspection");
            return base.ExecuteAsync(request, cancellationToken);
        }
    }
}
