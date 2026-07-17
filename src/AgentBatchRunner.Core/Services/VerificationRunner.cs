using System.Text;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class VerificationRunner(
    ProcessRunner processRunner,
    ConsoleLogger logger,
    IRunEventSink? runEventSink = null)
{
    public async Task<VerificationResult> RunAsync(
        IReadOnlyCollection<string> commands,
        string workingDirectory,
        string attemptDirectory,
        CancellationToken cancellationToken = default,
        TimeSpan? commandTimeout = null,
        string? runId = null,
        string? promptId = null,
        string? title = null,
        string? agent = null,
        int? attemptNumber = null,
        int? maxAttempts = null)
    {
        var started = DateTimeOffset.Now;
        var result = new VerificationResult();
        var log = new StringBuilder();

        if (commands.Count == 0)
        {
            logger.Warning("No verification commands were provided; the attempt cannot be auto-verified.");
            log.AppendLine("No verification commands were provided; the attempt cannot be auto-verified.");
            result.Unverified = true;
            result.Succeeded = false;
            result.Duration = DateTimeOffset.Now - started;
            result.LogPath = Path.Combine(attemptDirectory, "verification.log");
            await Utf8File.WriteAllTextAsync(
                result.LogPath,
                SensitiveDataRedactor.Redact(log.ToString()),
                cancellationToken);
            return result;
        }

        foreach (var command in commands)
        {
            logger.Info($"Verifying: {command}");
            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.VerificationStarted,
                    RunId = runId,
                    PromptId = promptId,
                    Title = title,
                    Agent = agent,
                    AttemptNumber = attemptNumber,
                    MaxAttempts = maxAttempts,
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    Message = $"Verification command started: {command}"
                },
                cancellationToken);
            log.AppendLine($"$ {command}");
            var processResult = await processRunner.RunShellCommandAsync(command, workingDirectory, cancellationToken, commandTimeout);
            var commandResult = new CommandResult
            {
                Command = command,
                ExitCode = processResult.ExitCode,
                Duration = processResult.Duration,
                TimedOut = processResult.TimedOut,
                Timeout = processResult.Timeout,
                StandardOutput = processResult.StandardOutput,
                StandardError = processResult.StandardError
            };
            result.Commands.Add(commandResult);

            log.AppendLine($"Exit code: {commandResult.ExitCode}");
            log.AppendLine($"Duration: {commandResult.Duration}");
            log.AppendLine($"Timed out: {commandResult.TimedOut}");
            if (commandResult.Timeout.HasValue)
            {
                log.AppendLine($"Timeout: {commandResult.Timeout.Value.TotalSeconds:0}s");
            }
            log.AppendLine("STDOUT");
            log.AppendLine(commandResult.StandardOutput);
            log.AppendLine("STDERR");
            log.AppendLine(commandResult.StandardError);
            log.AppendLine();

            if (!commandResult.Succeeded)
            {
                result.Succeeded = false;
                result.FailedCommand = command;
                result.FailedExitCode = commandResult.ExitCode;
                result.TimedOut = commandResult.TimedOut;
                result.Timeout = commandResult.Timeout;
                await PublishAsync(
                    new RunEvent
                    {
                        Kind = commandResult.TimedOut
                            ? RunEventKind.VerificationTimedOut
                            : RunEventKind.VerificationFailed,
                        RunId = runId,
                        PromptId = promptId,
                        Title = title,
                        Agent = agent,
                        AttemptNumber = attemptNumber,
                        MaxAttempts = maxAttempts,
                        Command = command,
                        WorkingDirectory = workingDirectory,
                        ExitCode = commandResult.ExitCode,
                        Duration = commandResult.Duration,
                        TimedOut = commandResult.TimedOut,
                        Timeout = commandResult.Timeout,
                        StandardOutput = commandResult.StandardOutput,
                        StandardError = commandResult.StandardError,
                        CombinedOutput = commandResult.CombinedOutput,
                        Message = commandResult.TimedOut
                            ? $"Verification command timed out after {commandResult.Timeout?.TotalSeconds:0}s: {command}"
                            : $"Verification command failed with exit code {commandResult.ExitCode}: {command}",
                        Path = Path.Combine(attemptDirectory, "verification.log")
                    },
                    cancellationToken);
                break;
            }

            await PublishAsync(
                new RunEvent
                {
                    Kind = RunEventKind.VerificationPassed,
                    RunId = runId,
                    PromptId = promptId,
                    Title = title,
                    Agent = agent,
                    AttemptNumber = attemptNumber,
                    MaxAttempts = maxAttempts,
                    Command = command,
                    WorkingDirectory = workingDirectory,
                    ExitCode = commandResult.ExitCode,
                    Duration = commandResult.Duration,
                    StandardOutput = commandResult.StandardOutput,
                    StandardError = commandResult.StandardError,
                    CombinedOutput = commandResult.CombinedOutput,
                    Message = $"Verification command passed: {command}",
                    Path = Path.Combine(attemptDirectory, "verification.log")
                },
                cancellationToken);
        }

        result.Succeeded = result.Commands.All(c => c.Succeeded);
        result.Duration = DateTimeOffset.Now - started;
        result.LogPath = Path.Combine(attemptDirectory, "verification.log");
        await Utf8File.WriteAllTextAsync(
            result.LogPath,
            SensitiveDataRedactor.Redact(log.ToString()),
            cancellationToken);
        return result;
    }

    private Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        return (runEventSink ?? NullRunEventSink.Instance).OnRunEventAsync(runEvent, cancellationToken);
    }
}
