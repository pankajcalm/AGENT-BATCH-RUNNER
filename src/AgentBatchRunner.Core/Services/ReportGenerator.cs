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
        builder.AppendLine($"- Blocked: {result.Blocked}");
        builder.AppendLine($"- Needs human decision: {result.NeedsHumanDecision}");
        builder.AppendLine($"- Prerequisites missing: {result.PrerequisiteMissing}");
        builder.AppendLine($"- Skipped: {result.Skipped}");
        builder.AppendLine($"- Timed out tasks: {result.TimedOutTasks}");
        builder.AppendLine($"- Timed out attempts: {result.TimedOutAttempts}");
        builder.AppendLine($"- Agent switches: {result.AgentSwitches}");
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
        AppendRoutingChanges(builder, result);
        builder.AppendLine();
        AppendRunBlocker(builder, result);
        builder.AppendLine();
        builder.AppendLine("## Task Results");
        builder.AppendLine();
        builder.AppendLine("| ID | Title | Configured Agent | Default Agent | Run Override | Base Agent | Effective Agent | Routing Reason | Latest Attempt Agent | Rate-Limited Source | Fallback Agent | Invocations | Retry Budget Used | Status | Agent Outcome | Recommended Next |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---:|---:|---|---|---|");

        foreach (var task in result.Tasks)
        {
            builder.AppendLine(
                $"| {Escape(task.Id)} | {Escape(task.Title)} | {Escape(Display(task.ConfiguredAgent))} | " +
                $"{Escape(Display(task.DefaultAgent))} | {Escape(Display(task.AgentOverride))} | " +
                $"{Escape(Display(task.BaseAgent))} | {Escape(Display(task.EffectiveAgent, task.Agent))} | " +
                $"{task.RoutingReason} | {Escape(Display(task.LatestAttemptAgent))} | " +
                $"{Escape(Display(task.RateLimitedSourceAgent))} | {Escape(Display(task.FallbackAgent))} | " +
                $"{task.Attempts.Count} | {task.RetryAttemptsConsumed} | {task.Status} | " +
                $"{task.AgentOutcome?.AgentOutcome.ToString() ?? "(none)"} | {Escape(Display(task.RecommendedNextFile))} |");
        }

        builder.AppendLine();
        AppendAttemptRouting(builder, result);
        builder.AppendLine();
        AppendRateLimitedTasks(builder, result);
        builder.AppendLine();
        AppendAgentOutcomes(builder, result);
        builder.AppendLine();
        builder.AppendLine("## Failed Tasks");
        builder.AppendLine();

        var failedTasks = result.Tasks
            .Where(t => t.Status is
                RunStatus.Failed or
                RunStatus.NeedsHumanReview or
                RunStatus.ToolchainFailure or
                RunStatus.Blocked or
                RunStatus.NeedsHumanDecision or
                RunStatus.PrerequisiteMissing)
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

    private static void AppendRoutingChanges(StringBuilder builder, RunResult result)
    {
        builder.AppendLine("## Agent Routing Changes");
        builder.AppendLine();
        if (result.RoutingChanges.Count == 0)
        {
            builder.AppendLine("No run-local agent routing changes.");
            return;
        }

        builder.AppendLine("| Time | Source | Replacement | Reason | Mode | Starting Prompt | Affected Prompts | Reset Time |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var change in result.RoutingChanges)
        {
            builder.AppendLine(
                $"| {change.Timestamp:O} | {Escape(change.SourceAgent)} | {Escape(change.ReplacementAgent)} | " +
                $"{change.Reason} | {(change.IsAutomatic ? "Automatic" : "Manual")} | " +
                $"{Escape(change.StartingPromptId)} | {Escape(string.Join(", ", change.AffectedPromptIds))} | " +
                $"{Escape(change.RateLimitResetAt.HasValue ? AgentRateLimitDisplay.FormatLocal(change.RateLimitResetAt.Value) : "(none)")} |");
        }
    }

    private static void AppendAttemptRouting(StringBuilder builder, RunResult result)
    {
        builder.AppendLine("## Attempt Routing");
        builder.AppendLine();
        var attempts = result.Tasks.SelectMany(task => task.Attempts.Select(attempt => (task, attempt))).ToList();
        if (attempts.Count == 0)
        {
            builder.AppendLine("No agent invocations were recorded.");
            return;
        }

        builder.AppendLine("| Prompt | Invocation | Agent | Consumes Retry | Switch Number | Routing Reason | Previous Agent | Status |");
        builder.AppendLine("|---|---:|---|---|---:|---|---|---|");
        foreach (var (task, attempt) in attempts)
        {
            builder.AppendLine(
                $"| {Escape(task.Id)} | {attempt.AttemptNumber} | {Escape(Display(attempt.AttemptAgent, attempt.AgentResult?.AgentName))} | " +
                $"{attempt.ConsumesRetry} | {attempt.AgentSwitchNumber} | {attempt.RoutingReason} | " +
                $"{Escape(Display(attempt.PreviousAgent))} | {attempt.Status} |");
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
        builder.AppendLine(result.FailureKind == RunFailureKind.AgentOutcomeBlocked
            ? "- Suggested next step: Resolve the recorded blocker or human decision before resuming dependent work."
            : "- Suggested next step: Correct the configured agent executable or version, restart AgentBatchRunner if PATH changed, and resume the run.");
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
            builder.AppendLine($"- Rate-limited source agent: {Display(task.RateLimitedSourceAgent, task.LatestAttemptAgent)}");
            builder.AppendLine($"- Fallback agent: {Display(task.FallbackAgent)}");
            builder.AppendLine($"- Blocked until: {(task.RateLimitResetAt.HasValue ? AgentRateLimitDisplay.FormatLocal(task.RateLimitResetAt.Value) : "(unknown)")}");
            builder.AppendLine($"- Reason: {task.LastFailureReason ?? task.RateLimitReason ?? "(not recorded)"}");
            builder.AppendLine($"- Log file path: {task.LastFailedLogPath ?? "(none)"}");
            builder.AppendLine("- Suggested next step: Wait for the reset time or clear the limit manually only if you know the agent is available again.");
            builder.AppendLine();
        }
    }

    private static void AppendAgentOutcomes(StringBuilder builder, RunResult result)
    {
        builder.AppendLine("## Agent Outcomes");
        builder.AppendLine();
        var outcomes = result.Tasks.Where(task => task.AgentOutcome is not null).ToList();
        if (outcomes.Count == 0)
        {
            builder.AppendLine("No machine-readable agent outcomes were reported.");
            return;
        }

        builder.AppendLine("| Prompt | Outcome | Blocker Code | Blocker | Recommended Next | Consumed Retry |");
        builder.AppendLine("|---|---|---|---|---|---|");
        foreach (var task in outcomes)
        {
            var latest = task.Attempts.LastOrDefault(attempt => attempt.AgentOutcome is not null);
            builder.AppendLine(
                $"| {Escape(task.Id)} | {task.AgentOutcome!.AgentOutcome} | " +
                $"{Escape(Display(task.AgentOutcome.BlockerCode))} | {Escape(Display(task.AgentOutcome.Blocker))} | " +
                $"{Escape(Display(task.RecommendedNextFile))} | {latest?.ConsumesRetry.ToString() ?? "(none)"} |");
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

    private static string Display(string? preferred, string? fallback)
    {
        return !string.IsNullOrWhiteSpace(preferred) ? preferred : Display(fallback);
    }
}
