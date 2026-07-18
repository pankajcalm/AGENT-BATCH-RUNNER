using System.Text;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class PipelineReportGenerator(PipelineStateStore stateStore)
{
    public async Task GenerateAsync(
        PipelineRunState state,
        CancellationToken cancellationToken = default)
    {
        await stateStore.SaveStateAsync(state, cancellationToken);
        await Utf8File.WriteAllTextAsync(
            Path.Combine(state.PipelineRunDirectory, "pipeline-report.md"),
            BuildMarkdown(state),
            cancellationToken);
    }

    public string BuildMarkdown(PipelineRunState state)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AgentBatchRunner Folder Pipeline Report");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Folder: {state.FolderPath}");
        builder.AppendLine($"- Repository: {state.RepoPath}");
        builder.AppendLine($"- Pipeline run ID: {state.PipelineRunId}");
        builder.AppendLine($"- Status: {state.Status}");
        builder.AppendLine($"- Execution mode: {state.ExecutionMode}");
        builder.AppendLine($"- Started: {state.StartedAt:O}");
        builder.AppendLine($"- Completed: {(state.CompletedAt.HasValue ? state.CompletedAt.Value.ToString("O") : "(not completed)")}");
        builder.AppendLine($"- Recommended next file: {Display(state.RecommendedNextFile)}");
        builder.AppendLine($"- Stop reason: {Display(state.StopReason)}");
        builder.AppendLine($"- Automatic transitions: {state.AutomaticTransitions}/{state.MaximumAutomaticTransitions}");
        builder.AppendLine();
        builder.AppendLine("## Queue");
        builder.AppendLine();
        builder.AppendLine("| # | File | Pipeline ID | Phase | Execution Agent | Review Agent | Dependencies | Gate | Execution | Review | Status | Manual Reason | Manual Time | Evidence | Dependencies Satisfied | Gate Override | Next Recommendation |");
        builder.AppendLine("|---:|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var file in state.Files.OrderBy(file => file.QueueOrder))
        {
            var hasManualStatus = file.Status is PipelineFileStatus.SkippedByUser or PipelineFileStatus.ManuallyCompleted;
            builder.AppendLine(
                $"| {file.QueueOrder} | {Escape(file.RelativePath)} | {Escape(file.PipelineId)} | {Escape(file.Phase)} | " +
                $"{Escape(file.ExecutionAgent)} | {Escape(Display(file.ReviewAgent))} | " +
                $"{Escape(string.Join(", ", file.DependencyIds))} | {Escape(Display(file.Gate?.Id))} | " +
                $"{file.ExecutionStatus?.ToString() ?? "(not run)"} | {file.ReviewVerdict?.ToString() ?? "(not reviewed)"} | " +
                $"{file.Status} | {Escape(Display(file.ManualReason))} | " +
                $"{(file.ManualTimestamp.HasValue ? file.ManualTimestamp.Value.ToString("O") : "(none)")} | " +
                $"{Escape(Display(file.ManualEvidencePath))} | " +
                $"{(hasManualStatus ? file.ManualSatisfiesDependencies : "(n/a)")} | " +
                $"{(hasManualStatus ? file.ManualGateApproved : "(n/a)")} | " +
                $"{Escape(Display(file.RecommendedNextFile))} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Reviews And Gates");
        builder.AppendLine();
        foreach (var file in state.Files.Where(file => file.ReviewHistory.Count > 0))
        {
            builder.AppendLine($"### {Escape(file.PipelineId)} - {Escape(file.Title)}");
            builder.AppendLine();
            foreach (var review in file.ReviewHistory)
            {
                builder.AppendLine($"- Review {review.ReviewRunId}: {review.ReviewVerdict}; gate {Display(review.GateId)} approved: {review.GateApproved}");
                builder.AppendLine($"- Summary: {review.Summary}");
                builder.AppendLine($"- Report: {Display(review.ReviewReportPath)}");
                foreach (var finding in review.Findings)
                {
                    builder.AppendLine($"- Finding {finding.Id} [{finding.Severity}]: {finding.Title}");
                }
                foreach (var decision in review.RequiredDecisions)
                {
                    builder.AppendLine($"- Required decision: {decision}");
                }
            }

            builder.AppendLine();
        }

        builder.AppendLine("## Manual Action Audit History");
        builder.AppendLine();
        if (state.ManualActionHistory.Count == 0)
        {
            builder.AppendLine("No manual pipeline actions were recorded.");
        }
        else
        {
            builder.AppendLine("| Time | File | Action | Previous | New | Actor | Source | Reason | Evidence | Dependencies Satisfied | Gate Approved | Reverses |");
            builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var action in state.ManualActionHistory)
            {
                builder.AppendLine(
                    $"| {action.Timestamp:O} | {Escape(action.FileId)} | {action.Action} | " +
                    $"{action.PreviousStatus} | {action.NewStatus} | {Escape(action.Actor)} | " +
                    $"{Escape(action.OverrideSource)} | {Escape(action.Reason)} | " +
                    $"{Escape(Display(action.EvidencePath))} | {action.SatisfiesDependencies} | " +
                    $"{action.GateApproved} | {Escape(Display(action.ReversesAuditId))} |");
            }
        }

        builder.AppendLine();

        builder.AppendLine("## Transition History");
        builder.AppendLine();
        if (state.Transitions.Count == 0)
        {
            builder.AppendLine("No transitions were recorded.");
        }
        else
        {
            builder.AppendLine("| Time | Event | File | Message |");
            builder.AppendLine("|---|---|---|---|");
            foreach (var transition in state.Transitions)
            {
                builder.AppendLine(
                    $"| {transition.Timestamp:O} | {Escape(transition.Event)} | " +
                    $"{Escape(Display(transition.PipelineFileId))} | {Escape(transition.Message)} |");
            }
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string Display(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
