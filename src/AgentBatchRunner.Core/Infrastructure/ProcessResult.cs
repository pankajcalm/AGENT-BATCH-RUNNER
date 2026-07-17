namespace AgentBatchRunner.Infrastructure;

public sealed class ProcessResult
{
    public string Command { get; set; } = string.Empty;

    public int ExitCode { get; set; }

    public TimeSpan Duration { get; set; }

    public bool TimedOut { get; set; }

    public TimeSpan? Timeout { get; set; }

    public string StandardOutput { get; set; } = string.Empty;

    public string StandardError { get; set; } = string.Empty;

    public bool Succeeded => ExitCode == 0;

    public string CombinedOutput
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StandardError))
            {
                return StandardOutput;
            }

            if (string.IsNullOrWhiteSpace(StandardOutput))
            {
                return StandardError;
            }

            return StandardOutput + Environment.NewLine + StandardError;
        }
    }

    public static ProcessResult Failed(string command, int exitCode, string error, TimeSpan duration)
    {
        return new ProcessResult
        {
            Command = SensitiveDataRedactor.Redact(command),
            ExitCode = exitCode,
            Duration = duration,
            StandardError = SensitiveDataRedactor.Redact(error)
        };
    }

    public static ProcessResult TimedOutResult(
        string command,
        string standardOutput,
        string standardError,
        TimeSpan duration,
        TimeSpan timeout)
    {
        var timeoutMessage = $"Command timed out after {timeout.TotalSeconds:0}s and was terminated.";
        var stderr = string.IsNullOrWhiteSpace(standardError)
            ? timeoutMessage
            : standardError.TrimEnd() + Environment.NewLine + timeoutMessage;

        return new ProcessResult
        {
            Command = SensitiveDataRedactor.Redact(command),
            ExitCode = 124,
            Duration = duration,
            TimedOut = true,
            Timeout = timeout,
            StandardOutput = SensitiveDataRedactor.Redact(standardOutput),
            StandardError = SensitiveDataRedactor.Redact(stderr)
        };
    }
}
