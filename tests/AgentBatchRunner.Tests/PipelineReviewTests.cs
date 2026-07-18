using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class PipelineReviewTests
{
    [Fact]
    public async Task GenerateAsync_CreatesReadOnlyReviewYamlReferencingRunArtifacts()
    {
        using var workspace = TestWorkspace.Create();
        var pipelineRunDirectory = Path.Combine(workspace.Root, ".agentbatchrunner", "pipelines", "pipeline-1");
        var executionRunDirectory = Path.Combine(workspace.Root, ".agentbatchrunner", "runs", "run-1");
        var taskDirectory = Path.Combine(executionRunDirectory, "tasks", "P001");
        Directory.CreateDirectory(taskDirectory);
        await Utf8File.WriteAllTextAsync(Path.Combine(executionRunDirectory, "final-report.md"), "report");
        await Utf8File.WriteAllTextAsync(Path.Combine(executionRunDirectory, "run-summary.json"), "{}");
        await Utf8File.WriteAllTextAsync(Path.Combine(taskDirectory, "git-diff-after.patch"), "diff");
        var sourcePath = Path.Combine(workspace.Root, "11_phase_0_recovery.yaml");
        await Utf8File.WriteAllTextAsync(sourcePath, "source");
        var source = CreateSourceFile(workspace.Root, sourcePath);
        var execution = new RunResult
        {
            RunId = "run-1",
            RepoPath = workspace.Root,
            Tasks = [new TaskRunResult { Id = "P001", Status = RunStatus.Succeeded, TaskDirectory = taskDirectory }]
        };

        var generated = await new PipelineReviewYamlGenerator().GenerateAsync(new PipelineReviewGenerationRequest
        {
            SourceFile = source,
            ExecutionResult = execution,
            ExecutionRunDirectory = executionRunDirectory,
            PipelineRunDirectory = pipelineRunDirectory,
            ReviewAgent = "codex"
        });

        Assert.True(File.Exists(generated.ReviewYamlPath));
        Assert.EndsWith("11_phase_0_recovery.review.yaml", generated.ReviewYamlPath, StringComparison.OrdinalIgnoreCase);
        var config = await new PromptFileLoader().LoadAsync(generated.ReviewYamlPath);
        Assert.Equal("read-only", config.CodexSandbox);
        Assert.Equal("plan", config.ClaudePermissionMode);
        Assert.False(config.CodexFullAuto);
        Assert.False(config.ClaudeDangerouslySkipPermissions);
        Assert.Equal(1, config.DefaultMaxRetries);
        var prompt = Assert.Single(config.Prompts);
        Assert.Contains(sourcePath, prompt.Prompt);
        Assert.Contains(Path.Combine(executionRunDirectory, "final-report.md"), prompt.Prompt);
        Assert.Contains(Path.Combine(executionRunDirectory, "run-summary.json"), prompt.Prompt);
        Assert.Contains(Path.Combine(taskDirectory, "git-diff-after.patch"), prompt.Prompt);
        Assert.Contains("does not by itself approve the gate", prompt.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not modify", prompt.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, prompt.Verify.Count);
        Assert.Contains(generated.ReviewResultPath, prompt.Verify[0]);
        Assert.Contains(generated.ReviewReportPath, prompt.Verify[1]);
    }

    [Fact]
    public void Parser_ParsesMachineReadableReviewJson()
    {
        const string output =
            "Review follows:\n```json\n" +
            "{\"schemaVersion\":\"1.0\",\"sourcePipelineFile\":\"11.yaml\",\"executionRunId\":\"run-1\",\"executionStatus\":\"Succeeded\",\"reviewVerdict\":\"Blocked\",\"gateId\":\"G0\",\"gateApproved\":false,\"summary\":\"Gate is blocked.\",\"findings\":[{\"id\":\"G0-001\",\"severity\":\"Critical\",\"title\":\"Measurement missing\",\"requiresHumanDecision\":true}],\"requiredDecisions\":[\"Choose policy.\"],\"recommendedNextFile\":\"12_repair.yaml\",\"canAutoAdvance\":false}\n```";

        var parsed = new PipelineReviewResultParser().TryParse(output, out var result, out var error);

        Assert.True(parsed, error);
        Assert.Equal(PipelineReviewVerdict.Blocked, result.ReviewVerdict);
        Assert.False(result.GateApproved);
        Assert.False(result.CanAutoAdvance);
        Assert.Equal(PipelineFindingSeverity.Critical, Assert.Single(result.Findings).Severity);
        Assert.Equal("12_repair.yaml", result.RecommendedNextFile);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"reviewVerdict\":\"Unknown\"}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"sourcePipelineFile\":\"x.yaml\",\"executionRunId\":\"r\",\"executionStatus\":\"Succeeded\",\"reviewVerdict\":\"Approved\",\"gateApproved\":false,\"summary\":\"No\"}")]
    public void Parser_RejectsInvalidReviewOutput(string output)
    {
        Assert.False(new PipelineReviewResultParser().TryParse(output, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task ReportGenerator_WritesJsonAndMarkdownSeparately()
    {
        using var workspace = TestWorkspace.Create();
        var resultPath = Path.Combine(workspace.Root, "review.json");
        var reportPath = Path.Combine(workspace.Root, "review.md");
        var result = new PipelineReviewResult
        {
            SourcePipelineFile = "11.yaml",
            ExecutionRunId = "run-1",
            ReviewRunId = "review-1",
            ExecutionStatus = "Succeeded",
            ReviewVerdict = PipelineReviewVerdict.Approved,
            GateApproved = true,
            CanAutoAdvance = true,
            Summary = "Approved with evidence."
        };

        await new PipelineReviewReportGenerator(new RunStateStore())
            .SaveAsync(result, resultPath, reportPath);

        Assert.True(File.Exists(resultPath));
        Assert.Contains("Approved", await Utf8File.ReadAllTextAsync(reportPath));
    }

    [Fact]
    public async Task GenerateAsync_WhenReviewEvidenceExists_AppendsIterationWithoutOverwrite()
    {
        using var workspace = TestWorkspace.Create();
        var sourcePath = Path.Combine(workspace.Root, "gate.yaml");
        await Utf8File.WriteAllTextAsync(sourcePath, "source");
        var source = CreateSourceFile(workspace.Root, sourcePath);
        var executionDirectory = Path.Combine(workspace.Root, "runs", "run-1");
        Directory.CreateDirectory(executionDirectory);
        var request = new PipelineReviewGenerationRequest
        {
            SourceFile = source,
            ExecutionResult = new RunResult { RunId = "run-1", RepoPath = workspace.Root },
            ExecutionRunDirectory = executionDirectory,
            PipelineRunDirectory = Path.Combine(workspace.Root, "pipeline"),
            ReviewAgent = "codex"
        };
        var generator = new PipelineReviewYamlGenerator();

        var first = await generator.GenerateAsync(request);
        var firstContent = await Utf8File.ReadAllTextAsync(first.ReviewYamlPath);
        var second = await generator.GenerateAsync(request);

        Assert.NotEqual(first.ReviewYamlPath, second.ReviewYamlPath);
        Assert.EndsWith("gate.review-R1.yaml", second.ReviewYamlPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(firstContent, await Utf8File.ReadAllTextAsync(first.ReviewYamlPath));
    }

    private static PipelineFile CreateSourceFile(string repoPath, string sourcePath)
    {
        return new PipelineFile
        {
            FilePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            RelativePath = Path.GetFileName(sourcePath),
            Config = new BatchConfig
            {
                Project = "Recovery",
                RepoPath = repoPath,
                DefaultAgent = "codex",
                Pipeline = new PipelineMetadata
                {
                    Id = "RECOVERY",
                    Title = "Phase 0 recovery",
                    Gate = new PipelineGateMetadata { Id = "G0", RequiredForNextPhase = true }
                },
                Prompts = [new PromptTask { Id = "P001", Title = "Task", Prompt = "Do work." }]
            }
        };
    }
}
