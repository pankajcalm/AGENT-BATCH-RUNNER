using System.Text;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class ReportGenerator(RunStateStore runStateStore)
{
    public async Task GenerateAsync(
        string runDirectory,
        RunResult result,
        CancellationToken cancellationToken = default)
    {
        await runStateStore.SaveJsonAsync(Path.Combine(runDirectory, "run-summary.json"), result, cancellationToken);
        var report = BuildMarkdown(result);
        await Utf8File.WriteAllTextAsync(
            Path.Combine(runDirectory, "final-report.md"),
            report,
            cancellationToken);
    }

    public string BuildMarkdown(RunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# AgentBatchRunner Final Report");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Total prompts: {result.TotalPrompts}");
        builder.AppendLine($"- Succeeded: {result.Succeeded}");
        builder.AppendLine($"- Unverified success: {result.UnverifiedSuccess}");
        builder.AppendLine($"- Failed: {result.Failed}");
        builder.AppendLine($"- Needs human review: {result.NeedsHumanReview}");
        builder.AppendLine($"- Rate limited: {result.RateLimited}");
        builder.AppendLine($"- Toolchain failures: {result.ToolchainFailures}");
        builder.AppendLine($"- Skipped: {result.Skipped}");
        builder.AppendLine($"- Timed out tasks: {result.TimedOutTasks}");
        builder.AppendLine($"- Timed out attempts: {result.TimedOutAttempts}");
        builder.AppendLine($"- Started: {result.StartedAt:O}");
        builder.AppendLine($"- Completed: {(result.CompletedAt.HasValue ? result.CompletedAt.Value.ToString("O") : "(not completed)")}");
        builder.AppendLine();
        builder.AppendLine("## Routing");
        builder.AppendLine();
        builder.AppendLine($"- Default agent: {Display(result.DefaultAgent)}");
        builder.AppendLine($"- Run override: {Display(result.AgentOverride)}");
        builder.AppendLine($"- Routing mode: {(string.IsNullOrWhiteSpace(result.AgentOverride) ? "From YAML" : $"Global override: {result.AgentOverride}")}");
        builder.AppendLine();
        AppendToolchains(builder, result);
        builder.AppendLine();
        AppendRunBlocker(builder, result);
        builder.AppendLine();
        builder.AppendLine("## Task Results");
        builder.AppendLine();
        builder.AppendLine("| ID | Title | Configured Agent | Default Agent | Run Override | Effective Agent | Attempts | Status |");
        builder.AppendLine("|---|---|---|---|---|---|---:|---|");

        foreach (var task in result.Tasks)
        {
            builder.AppendLine(
                $"| {Escape(task.Id)} | {Escape(task.Title)} | {Escape(Display(task.ConfiguredAgent))} | " +
                $"{Escape(Display(task.DefaultAgent))} | {Escape(Display(task.AgentOverride))} | " +
                $"{Escape(task.Agent)} | {task.Attempts.Count} | {task.Status} |");
        }

        builder.AppendLine();
        AppendRateLimitedTasks(builder, result);
        builder.AppendLine();
        builder.AppendLine("## Failed Tasks");
        builder.AppendLine();

        var failedTasks = result.Tasks
            .Where(t => t.Status is RunStatus.Failed or RunStatus.NeedsHumanReview or RunStatus.ToolchainFailure)
            .ToList();

        if (failedTasks.Count == 0)
        {
            builder.AppendLine("No failed tasks.");
            return builder.ToString();
        }

        foreach (var task in failedTasks)
        {
            builder.AppendLine($"### {Escape(task.Id)} - {Escape(task.Title)}");
            builder.AppendLine();
            builder.AppendLine($"- Prompt ID: {task.Id}");
            builder.AppendLine($"- Title: {task.Title}");
            builder.AppendLine($"- Last failed verification command: {task.LastFailedVerificationCommand ?? "(none)"}");
            builder.AppendLine($"- Exit code: {(task.LastFailedExitCode.HasValue ? task.LastFailedExitCode.Value.ToString() : "(unknown)")}");
            builder.AppendLine($"- Failure reason: {task.LastFailureReason ?? "(not recorded)"}");
            builder.AppendLine($"- Log file path: {task.LastFailedLogPath ?? "(none)"}");
            builder.AppendLine("- Suggested next step: Inspect the log and working tree diff, then either fix manually or rerun the prompt after adjusting the YAML.");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static void AppendToolchains(StringBuilder builder, RunResult result)
    {
        builder.AppendLine("## Agent Toolchains");
        builder.AppendLine();

        if (result.Toolchains.Count == 0)
        {
            builder.AppendLine("No external agent toolchains were recorded.");
            return;
        }

        builder.AppendLine("| Agent | Executable | Version | Preflight | Failure | ");
        builder.AppendLine("|---|---|---|---|---|");
        foreach (var toolchain in result.Toolchains)
        {
            builder.AppendLine(
                $"| {Escape(toolchain.AgentName)} | {Escape(Display(toolchain.ExecutablePath))} | " +
                $"{Escape(Display(toolchain.Version))} | {toolchain.Status} | {Escape(Display(toolchain.FailureReason))} |");
        }
    }

    private static void AppendRunBlocker(StringBuilder builder, RunResult result)
    {
        builder.AppendLine("## Run-Level Blocker");
        builder.AppendLine();
        if (result.FailureKind == RunFailureKind.None)
        {
            builder.AppendLine("No run-level blocker was recorded.");
            return;
        }

        builder.AppendLine($"- Classification: {result.FailureKind}");
        builder.AppendLine($"- Reason: {result.RunFailureReason ?? "(not recorded)"}");
        builder.AppendLine("- Suggested next step: Correct the configured agent executable or version, restart AgentBatchRunner if PATH changed, and resume the run.");
    }

    private static void AppendRateLimitedTasks(StringBuilder builder, RunResult result)
    {
        builder.AppendLine("## Rate Limited Tasks");
        builder.AppendLine();

        var rateLimitedTasks = result.Tasks
            .Where(t => t.Status == RunStatus.RateLimited)
            .ToList();

        if (rateLimitedTasks.Count == 0)
        {
            builder.AppendLine("No rate-limited tasks.");
            return;
        }

        foreach (var task in rateLimitedTasks)
        {
            builder.AppendLine($"### {Escape(task.Id)} - {Escape(task.Title)}");
            builder.AppendLine();
            builder.AppendLine($"- Prompt ID: {task.Id}");
            builder.AppendLine($"- Title: {task.Title}");
            builder.AppendLine($"- Agent: {task.Agent}");
            builder.AppendLine($"- Blocked until: {(task.RateLimitResetAt.HasValue ? AgentRateLimitDisplay.FormatLocal(task.RateLimitResetAt.Value) : "(unknown)")}");
            builder.AppendLine($"- Reason: {task.LastFailureReason ?? task.RateLimitReason ?? "(not recorded)"}");
            builder.AppendLine($"- Log file path: {task.LastFailedLogPath ?? "(none)"}");
            builder.AppendLine("- Suggested next step: Wait for the reset time or clear the limit manually only if you know the agent is available again.");
            builder.AppendLine();
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static string Display(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
    }
}
