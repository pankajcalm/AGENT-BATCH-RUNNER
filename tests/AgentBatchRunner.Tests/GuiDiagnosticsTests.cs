using System.Collections.ObjectModel;
using AgentBatchRunner.Agents;
using AgentBatchRunner.Gui.ViewModels;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Tests;

public sealed class GuiDiagnosticsTests
{
    [Fact]
    public void ResetForRun_ClearsPreviousLogsAndRows()
    {
        var logs = new ObservableCollection<LogEntryViewModel>
        {
            new(DateTimeOffset.UtcNow, "INFO", "old run message")
        };
        var promptTasks = new ObservableCollection<PromptTaskViewModel>
        {
            new()
            {
                Id = "OLD",
                Title = "Old task",
                Status = "Failed",
                LastMessage = "stale"
            }
        };
        var config = new BatchConfig
        {
            Project = "Test",
            RepoPath = @"C:\repo",
            DefaultMaxRetries = 3,
            Prompts =
            [
                new PromptTask
                {
                    Id = "P001",
                    Title = "Current task",
                    Prompt = "Do the current thing.",
                    MaxRetries = 2
                }
            ]
        };

        GuiRunStateResetter.ResetForRun(logs, promptTasks, config, "codex");

        Assert.Empty(logs);
        var task = Assert.Single(promptTasks);
        Assert.Equal("P001", task.Id);
        Assert.Equal("Current task", task.Title);
        Assert.Equal("Do the current thing.", task.PromptText);
        Assert.Equal("codex", task.Agent);
        Assert.Equal(2, task.MaxAttempts);
        Assert.Equal("Pending", task.Status);
        Assert.Equal("Waiting to run.", task.LastMessage);
        Assert.Equal("False", task.TimedOutText);
    }

    [Fact]
    public void ApplyAttemptResult_MapsAgentFailureDetails()
    {
        var attemptDirectory = Path.Combine(Path.GetTempPath(), "agentbatchrunner-tests", "attempt-2");
        var attempt = new AttemptResult
        {
            AttemptNumber = 2,
            AttemptDirectory = attemptDirectory,
            Status = RunStatus.Failed,
            TimedOut = true,
            TimeoutReason = "Agent command timed out after 1s.",
            AgentResult = new AgentExecutionResult
            {
                AgentName = "claude",
                Command = "claude -p \"fix it\"",
                ExitCode = 124,
                TimedOut = true,
                Timeout = TimeSpan.FromSeconds(1),
                StandardOutput = "partial agent stdout",
                StandardError = "partial agent stderr"
            }
        };
        var task = new PromptTaskViewModel { Id = "P001" };

        PromptTaskDiagnosticsMapper.ApplyAttemptResult(
            task,
            attempt,
            "claude",
            @"C:\repo",
            "claude -p \"fix it\"",
            "Agent command failed.");

        Assert.Equal("claude", task.Agent);
        Assert.Equal(2, task.CurrentAttempt);
        Assert.Equal(@"C:\repo", task.WorkingDirectory);
        Assert.Equal("claude -p \"fix it\"", task.Command);
        Assert.Equal("124", task.ExitCodeText);
        Assert.Equal("True", task.TimedOutText);
        Assert.Equal("1s", task.TimeoutText);
        Assert.Equal("partial agent stdout", task.Stdout);
        Assert.Equal("partial agent stderr", task.Stderr);
        Assert.Contains("partial agent stdout", task.CombinedOutput);
        Assert.Contains("partial agent stderr", task.CombinedOutput);
        Assert.Equal("Agent command failed.", task.LastFailureReason);
        Assert.Equal(attemptDirectory, task.LatestAttemptFolder);
        Assert.Equal(Path.Combine(attemptDirectory, "status.json"), task.AttemptStatusFilePath);
        Assert.Equal(Path.Combine(attemptDirectory, "agent-output.txt"), task.AgentOutputFilePath);
    }

    [Fact]
    public void ApplyAttemptResult_MapsVerificationTimeoutDetails()
    {
        var attemptDirectory = Path.Combine(Path.GetTempPath(), "agentbatchrunner-tests", "attempt-1");
        var verificationLogPath = Path.Combine(attemptDirectory, "verification.log");
        var attempt = new AttemptResult
        {
            AttemptNumber = 1,
            AttemptDirectory = attemptDirectory,
            Status = RunStatus.Failed,
            VerificationResult = new VerificationResult
            {
                Succeeded = false,
                TimedOut = true,
                Timeout = TimeSpan.FromSeconds(5),
                FailedCommand = "dotnet test",
                FailedExitCode = 124,
                LogPath = verificationLogPath,
                Commands =
                [
                    new CommandResult
                    {
                        Command = "dotnet test",
                        ExitCode = 124,
                        TimedOut = true,
                        Timeout = TimeSpan.FromSeconds(5),
                        StandardOutput = "partial verification stdout",
                        StandardError = "partial verification stderr"
                    }
                ]
            }
        };
        var task = new PromptTaskViewModel { Id = "P001" };

        PromptTaskDiagnosticsMapper.ApplyAttemptResult(
            task,
            attempt,
            "dryrun",
            @"C:\repo",
            "dotnet test",
            "Verification timed out.");

        Assert.Equal("dotnet test", task.Command);
        Assert.Equal("124", task.ExitCodeText);
        Assert.Equal("True", task.TimedOutText);
        Assert.Equal("5s", task.TimeoutText);
        Assert.Equal("partial verification stdout", task.Stdout);
        Assert.Equal("partial verification stderr", task.Stderr);
        Assert.Contains("partial verification stdout", task.CombinedOutput);
        Assert.Contains("partial verification stderr", task.CombinedOutput);
        Assert.Equal(verificationLogPath, task.VerificationLogPath);
        Assert.Equal("Verification timed out.", task.LastFailureReason);
    }

    [Fact]
    public void GetReportOpenMessage_ReturnsFriendlyMessageForMissingReport()
    {
        var missingReportPath = Path.Combine(
            Path.GetTempPath(),
            "agentbatchrunner-tests",
            Guid.NewGuid().ToString("N"),
            "final-report.md");

        var message = ReportAvailability.GetReportOpenMessage(missingReportPath);

        Assert.Equal(ReportAvailability.MissingReportMessage, message);
    }
}
