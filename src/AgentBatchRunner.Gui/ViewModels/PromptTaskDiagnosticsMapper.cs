using System.IO;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Gui.ViewModels;

public static class PromptTaskDiagnosticsMapper
{
    public static void ApplyRunEvent(PromptTaskViewModel task, RunEvent runEvent)
    {
        if (!string.IsNullOrWhiteSpace(runEvent.BaseAgent))
        {
            task.BaseAgent = runEvent.BaseAgent;
        }

        if (!string.IsNullOrWhiteSpace(runEvent.EffectiveAgent))
        {
            task.EffectiveAgent = runEvent.EffectiveAgent;
        }

        if (!string.IsNullOrWhiteSpace(runEvent.AttemptAgent))
        {
            task.LatestAttemptAgent = runEvent.AttemptAgent;
        }

        if (runEvent.RoutingReason.HasValue)
        {
            task.RoutingReason = runEvent.RoutingReason.Value.ToString();
        }

        if (runEvent.AgentOutcome is not null)
        {
            task.AgentOutcomeText = runEvent.AgentOutcome.AgentOutcome.ToString();
            task.BlockerCode = runEvent.AgentOutcome.BlockerCode ?? string.Empty;
            task.RecommendedNextFile = runEvent.AgentOutcome.RecommendedNext ?? string.Empty;
            task.LastFailureReason = runEvent.AgentOutcome.Blocker ?? task.LastFailureReason;
        }

        if (!string.IsNullOrWhiteSpace(runEvent.Command))
        {
            task.Command = runEvent.Command;
        }

        if (!string.IsNullOrWhiteSpace(runEvent.WorkingDirectory))
        {
            task.WorkingDirectory = runEvent.WorkingDirectory;
        }

        if (runEvent.ExitCode.HasValue)
        {
            task.ExitCodeText = runEvent.ExitCode.Value.ToString();
        }

        if (runEvent.Kind is RunEventKind.AgentCompleted or RunEventKind.AgentFailed or RunEventKind.AgentTimedOut or
            RunEventKind.AgentRateLimited or RunEventKind.AgentToolchainFailed or
            RunEventKind.VerificationPassed or RunEventKind.VerificationFailed or RunEventKind.VerificationTimedOut)
        {
            task.TimedOutText = runEvent.TimedOut ? "True" : "False";
        }

        if (runEvent.Timeout.HasValue)
        {
            task.TimeoutText = $"{runEvent.Timeout.Value.TotalSeconds:0}s";
        }

        if (runEvent.StandardOutput is not null)
        {
            task.Stdout = runEvent.StandardOutput;
        }

        if (runEvent.StandardError is not null)
        {
            task.Stderr = runEvent.StandardError;
        }

        if (runEvent.CombinedOutput is not null)
        {
            task.CombinedOutput = runEvent.CombinedOutput;
        }

        if (runEvent.ExitCode == -1 && !string.IsNullOrWhiteSpace(runEvent.StandardError))
        {
            task.ExceptionDetails = runEvent.StandardError;
        }

        if ((runEvent.Kind is RunEventKind.AttemptStarted or RunEventKind.AgentStarted) &&
            !string.IsNullOrWhiteSpace(runEvent.Path))
        {
            SetAttemptFolder(task, runEvent.Path);
        }

        if ((runEvent.Kind is RunEventKind.AgentCompleted or RunEventKind.AgentFailed or RunEventKind.AgentTimedOut or
            RunEventKind.AgentRateLimited or RunEventKind.AgentToolchainFailed) &&
            !string.IsNullOrWhiteSpace(runEvent.Path))
        {
            task.AgentOutputFilePath = runEvent.Path;
            SetAttemptFolder(task, Path.GetDirectoryName(runEvent.Path) ?? string.Empty);
        }

        if ((runEvent.Kind is RunEventKind.VerificationPassed or RunEventKind.VerificationFailed or RunEventKind.VerificationTimedOut) &&
            !string.IsNullOrWhiteSpace(runEvent.Path))
        {
            task.VerificationLogPath = runEvent.Path;
            SetAttemptFolder(task, Path.GetDirectoryName(runEvent.Path) ?? string.Empty);
        }
    }

    public static void ApplyAttemptResult(
        PromptTaskViewModel task,
        AttemptResult attempt,
        string agent,
        string workingDirectory,
        string? failedCommand,
        string? lastFailureReason)
    {
        task.EffectiveAgent = agent;
        task.LatestAttemptAgent = string.IsNullOrWhiteSpace(attempt.AttemptAgent)
            ? attempt.AgentResult?.AgentName ?? agent
            : attempt.AttemptAgent;
        task.RoutingReason = attempt.RoutingReason.ToString();
        task.CurrentAttempt = attempt.AttemptNumber;
        task.WorkingDirectory = workingDirectory;
        task.LatestAttemptFolder = attempt.AttemptDirectory;
        task.AttemptStatusFilePath = Path.Combine(attempt.AttemptDirectory, "status.json");
        if (attempt.AgentOutcome is not null)
        {
            task.AgentOutcomeText = attempt.AgentOutcome.AgentOutcome.ToString();
            task.BlockerCode = attempt.AgentOutcome.BlockerCode ?? string.Empty;
            task.RecommendedNextFile = attempt.AgentOutcome.RecommendedNext ?? string.Empty;
        }

        if (attempt.AgentResult is not null)
        {
            task.Command = attempt.AgentResult.Command;
            task.ExitCodeText = attempt.AgentResult.ExitCode.ToString();
            task.TimedOutText = attempt.AgentResult.TimedOut ? "True" : "False";
            task.TimeoutText = attempt.AgentResult.Timeout.HasValue
                ? $"{attempt.AgentResult.Timeout.Value.TotalSeconds:0}s"
                : string.Empty;
            task.Stdout = attempt.AgentResult.StandardOutput;
            task.Stderr = attempt.AgentResult.StandardError;
            task.CombinedOutput = attempt.AgentResult.CombinedOutput;
            task.ExceptionDetails = attempt.AgentResult.ExitCode == -1
                ? attempt.AgentResult.StandardError
                : string.Empty;
            task.AgentOutputFilePath = Path.Combine(attempt.AttemptDirectory, "agent-output.txt");
        }

        if (attempt.VerificationResult is not null)
        {
            task.VerificationLogPath = attempt.VerificationResult.LogPath;
            var failedVerification = attempt.VerificationResult.Commands.FirstOrDefault(c => !c.Succeeded);
            if (failedVerification is not null)
            {
                task.Command = failedVerification.Command;
                task.ExitCodeText = failedVerification.ExitCode.ToString();
                task.TimedOutText = failedVerification.TimedOut ? "True" : "False";
                task.TimeoutText = failedVerification.Timeout.HasValue
                    ? $"{failedVerification.Timeout.Value.TotalSeconds:0}s"
                    : task.TimeoutText;
                task.Stdout = failedVerification.StandardOutput;
                task.Stderr = failedVerification.StandardError;
                task.CombinedOutput = failedVerification.CombinedOutput;
            }
        }

        task.FailedCommand = failedCommand ?? task.Command;
        task.LastFailureReason = lastFailureReason ?? attempt.TimeoutReason ?? string.Empty;
    }

    private static void SetAttemptFolder(PromptTaskViewModel task, string attemptFolder)
    {
        if (string.IsNullOrWhiteSpace(attemptFolder))
        {
            return;
        }

        task.LatestAttemptFolder = attemptFolder;
        task.AttemptStatusFilePath = Path.Combine(attemptFolder, "status.json");
    }
}
