using System.Text;
using System.Text.Json;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentBatchRunner.Services;

public sealed class PipelineReviewYamlGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public async Task<GeneratedPipelineReview> GenerateAsync(
        PipelineReviewGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var sourceName = Path.GetFileNameWithoutExtension(request.SourceFile.FileName);
        var generatedDirectory = Path.Combine(request.PipelineRunDirectory, "generated-reviews");
        var reviewsDirectory = Path.Combine(request.PipelineRunDirectory, "reviews");
        Directory.CreateDirectory(generatedDirectory);
        Directory.CreateDirectory(reviewsDirectory);

        var iteration = request.ReviewIteration;
        string iterationSuffix;
        string reviewYamlPath;
        string reviewResultPath;
        string reviewReportPath;
        do
        {
            iterationSuffix = iteration > 0 ? $"-R{iteration}" : string.Empty;
            reviewYamlPath = Path.Combine(generatedDirectory, $"{sourceName}.review{iterationSuffix}.yaml");
            reviewResultPath = Path.Combine(reviewsDirectory, $"{sourceName}.review-result{iterationSuffix}.json");
            reviewReportPath = Path.Combine(reviewsDirectory, $"{sourceName}.review-report{iterationSuffix}.md");
            iteration++;
        }
        while (File.Exists(reviewYamlPath) || File.Exists(reviewResultPath) || File.Exists(reviewReportPath));
        var selectedIteration = iteration - 1;

        var artifact = new GeneratedPipelineReview
        {
            Iteration = selectedIteration,
            ReviewPromptId = $"REVIEW-{FileNameSanitizer.Sanitize(request.SourceFile.PipelineId)}{iterationSuffix}",
            ReviewAgent = request.ReviewAgent.Trim().ToLowerInvariant(),
            ReviewYamlPath = reviewYamlPath,
            ReviewResultPath = reviewResultPath,
            ReviewReportPath = reviewReportPath
        };
        artifact.ReviewPrompt = BuildPrompt(request, artifact);

        var config = new BatchConfig
        {
            Project = $"Post-run review: {request.SourceFile.Title}",
            RepoPath = request.SourceFile.Config.RepoPath,
            DefaultAgent = artifact.ReviewAgent,
            DefaultMaxRetries = 1,
            DefaultAgentTimeoutSeconds = request.SourceFile.Config.DefaultAgentTimeoutSeconds,
            DefaultVerifyTimeoutSeconds = request.SourceFile.Config.DefaultVerifyTimeoutSeconds,
            CodexExecutablePath = request.SourceFile.Config.CodexExecutablePath,
            ClaudeExecutablePath = request.SourceFile.Config.ClaudeExecutablePath,
            MinimumCodexVersion = request.SourceFile.Config.MinimumCodexVersion,
            ClaudePermissionMode = "plan",
            ClaudeDangerouslySkipPermissions = false,
            CodexSandbox = "read-only",
            CodexFullAuto = false,
            Prompts =
            [
                new PromptTask
                {
                    Id = artifact.ReviewPromptId,
                    Title = $"Independent review of {request.SourceFile.Title}",
                    Agent = artifact.ReviewAgent,
                    Prompt = artifact.ReviewPrompt,
                    MaxRetries = 1,
                    Verify = BuildVerificationCommands(artifact)
                }
            ]
        };

        await Utf8File.WriteAllTextAsync(
            artifact.ReviewYamlPath,
            _serializer.Serialize(config),
            cancellationToken);
        return artifact;
    }

    private static string BuildPrompt(
        PipelineReviewGenerationRequest request,
        GeneratedPipelineReview artifact)
    {
        var metadataJson = JsonSerializer.Serialize(request.SourceFile.Metadata, JsonOptions);
        var previousReviewJson = request.PreviousReview is null
            ? "(none)"
            : JsonSerializer.Serialize(request.PreviousReview, JsonOptions);
        var taskPatches = request.ExecutionResult.Tasks
            .Select(task => Path.Combine(task.TaskDirectory, "git-diff-after.patch"))
            .Where(File.Exists)
            .ToList();
        var builder = new StringBuilder();
        builder.AppendLine("Perform an independent, read-only post-run review.");
        builder.AppendLine("Do not modify source files, product files, Git state, or execution artifacts.");
        builder.AppendLine("Inspect the existing evidence and return one JSON object as the final output without a Markdown code fence.");
        builder.AppendLine();
        builder.AppendLine($"Source YAML file: {request.SourceFile.FilePath}");
        builder.AppendLine($"Source pipeline file name: {request.SourceFile.FileName}");
        builder.AppendLine($"Execution run ID: {request.ExecutionResult.RunId}");
        builder.AppendLine($"Execution status: {DetermineExecutionStatus(request.ExecutionResult)}");
        builder.AppendLine($"Execution final report: {Path.Combine(request.ExecutionRunDirectory, "final-report.md")}");
        builder.AppendLine($"Execution run summary: {Path.Combine(request.ExecutionRunDirectory, "run-summary.json")}");
        builder.AppendLine("Task patch paths:");
        if (taskPatches.Count == 0)
        {
            builder.AppendLine("- (none recorded)");
        }
        else
        {
            foreach (var path in taskPatches)
            {
                builder.AppendLine($"- {path}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Pipeline metadata:");
        builder.AppendLine(metadataJson);
        builder.AppendLine();
        builder.AppendLine("Known owner decisions:");
        builder.AppendLine(request.KnownOwnerDecisions.Count == 0
            ? "(none recorded)"
            : string.Join(Environment.NewLine, request.KnownOwnerDecisions.Select(value => $"- {value}")));
        builder.AppendLine();
        builder.AppendLine("Previous review verdict when re-reviewing:");
        builder.AppendLine(previousReviewJson);
        builder.AppendLine();
        builder.AppendLine("Review all failed or retried attempts, the final report, run summary, task patches, required artifacts, and declared gate criteria.");
        builder.AppendLine("A successful execution process does not by itself approve the gate.");
        builder.AppendLine("Use only these reviewVerdict values: Approved, ApprovedWithWarnings, Blocked, NeedsHumanDecision, PrerequisiteMissing, ReviewFailed, Canceled, RateLimited.");
        builder.AppendLine("Approved requires gateApproved=true and no unresolved high or critical blockers.");
        builder.AppendLine("Blocked, NeedsHumanDecision, and PrerequisiteMissing must set canAutoAdvance=false.");
        builder.AppendLine();
        builder.AppendLine("Required JSON contract:");
        builder.AppendLine("{");
        builder.AppendLine("  \"schemaVersion\": \"1.0\",");
        builder.AppendLine($"  \"sourcePipelineFile\": \"{EscapeJson(request.SourceFile.FileName)}\",");
        builder.AppendLine($"  \"executionRunId\": \"{EscapeJson(request.ExecutionResult.RunId)}\",");
        builder.AppendLine("  \"executionStatus\": \"Succeeded|Blocked|NeedsHumanDecision|PrerequisiteMissing|Failed|RateLimited|Canceled|TimedOut\",");
        builder.AppendLine("  \"reviewVerdict\": \"Approved|ApprovedWithWarnings|Blocked|NeedsHumanDecision|PrerequisiteMissing|ReviewFailed|Canceled|RateLimited\",");
        builder.AppendLine($"  \"gateId\": \"{EscapeJson(request.SourceFile.Metadata?.Gate?.Id ?? string.Empty)}\",");
        builder.AppendLine("  \"gateApproved\": false,");
        builder.AppendLine("  \"summary\": \"Concise evidence-based summary\",");
        builder.AppendLine("  \"findings\": [{ \"id\": \"G0-001\", \"severity\": \"Critical\", \"title\": \"Finding\", \"detail\": \"Evidence\", \"requiresHumanDecision\": true }],");
        builder.AppendLine("  \"requiredDecisions\": [],");
        builder.AppendLine("  \"recommendedNextFile\": null,");
        builder.AppendLine("  \"canAutoAdvance\": false");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine($"The host will persist your JSON to: {artifact.ReviewResultPath}");
        builder.AppendLine($"The host will generate the Markdown review report at: {artifact.ReviewReportPath}");
        return builder.ToString();
    }

    private static List<string> BuildVerificationCommands(GeneratedPipelineReview artifact)
    {
        var resultPath = EscapePowerShellSingleQuoted(artifact.ReviewResultPath);
        var reportPath = EscapePowerShellSingleQuoted(artifact.ReviewReportPath);
        return
        [
            $"$p='{resultPath}'; if (!(Test-Path -LiteralPath $p)) {{ exit 2 }}; try {{ $null = Get-Content -Raw -LiteralPath $p | ConvertFrom-Json }} catch {{ Write-Error $_; exit 3 }}",
            $"if (!(Test-Path -LiteralPath '{reportPath}')) {{ exit 2 }}"
        ];
    }

    private static string DetermineExecutionStatus(RunResult result)
    {
        var terminalTask = result.Tasks.LastOrDefault(task => task.Status is not (RunStatus.Succeeded or RunStatus.UnverifiedSuccess));
        return terminalTask?.Status.ToString() ?? "Succeeded";
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static void Validate(PipelineReviewGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceFile.FilePath) ||
            string.IsNullOrWhiteSpace(request.SourceFile.Config.RepoPath) ||
            string.IsNullOrWhiteSpace(request.ExecutionResult.RunId) ||
            string.IsNullOrWhiteSpace(request.ExecutionRunDirectory) ||
            string.IsNullOrWhiteSpace(request.PipelineRunDirectory) ||
            !Agents.AgentAdapterFactory.IsSupportedAgent(request.ReviewAgent))
        {
            throw new InvalidOperationException("Review generation requires source, execution, pipeline directory, and supported review-agent information.");
        }
    }
}
