using System.Text;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class PipelineReviewReportGenerator(RunStateStore runStateStore)
{
    public async Task SaveAsync(
        PipelineReviewResult result,
        string resultPath,
        string reportPath,
        CancellationToken cancellationToken = default)
    {
        result.ReviewResultPath = resultPath;
        result.ReviewReportPath = reportPath;
        await runStateStore.SaveJsonAsync(resultPath, result, cancellationToken);
        await Utf8File.WriteAllTextAsync(reportPath, BuildMarkdown(result), cancellationToken);
    }

    public string BuildMarkdown(PipelineReviewResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AgentBatchRunner Pipeline Review");
        builder.AppendLine();
        builder.AppendLine("## Verdict");
        builder.AppendLine();
        builder.AppendLine($"- Source pipeline file: {result.SourcePipelineFile}");
        builder.AppendLine($"- Execution run ID: {result.ExecutionRunId}");
        builder.AppendLine($"- Review run ID: {result.ReviewRunId}");
        builder.AppendLine($"- Execution status: {result.ExecutionStatus}");
        builder.AppendLine($"- Review verdict: {result.ReviewVerdict}");
        builder.AppendLine($"- Gate: {result.GateId ?? "(none)"}");
        builder.AppendLine($"- Gate approved: {result.GateApproved}");
        builder.AppendLine($"- Can auto-advance: {result.CanAutoAdvance}");
        builder.AppendLine($"- Recommended next file: {result.RecommendedNextFile ?? "(none)"}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine(result.Summary);
        builder.AppendLine();
        builder.AppendLine("## Findings");
        builder.AppendLine();
        if (result.Findings.Count == 0)
        {
            builder.AppendLine("No findings were reported.");
        }
        else
        {
            builder.AppendLine("| ID | Severity | Title | Human Decision | Detail |");
            builder.AppendLine("|---|---|---|---|---|");
            foreach (var finding in result.Findings)
            {
                builder.AppendLine(
                    $"| {Escape(finding.Id)} | {finding.Severity} | {Escape(finding.Title)} | " +
                    $"{finding.RequiresHumanDecision} | {Escape(finding.Detail ?? string.Empty)} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Required Decisions");
        builder.AppendLine();
        if (result.RequiredDecisions.Count == 0)
        {
            builder.AppendLine("No human decisions were requested.");
        }
        else
        {
            foreach (var decision in result.RequiredDecisions)
            {
                builder.AppendLine($"- {decision}");
            }
        }

        if (!string.IsNullOrWhiteSpace(result.FailureReason))
        {
            builder.AppendLine();
            builder.AppendLine("## Review Failure");
            builder.AppendLine();
            builder.AppendLine(result.FailureReason);
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
