namespace AgentBatchRunner.Models;

public sealed class VerificationResult
{
    public bool Succeeded { get; set; }

    /// <summary>
    /// True when there were no verification commands to run, so the attempt could not be
    /// automatically verified. Such a result is never treated as a pass.
    /// </summary>
    public bool Unverified { get; set; }

    public bool TimedOut { get; set; }

    public TimeSpan? Timeout { get; set; }

    public string? FailedCommand { get; set; }

    public int? FailedExitCode { get; set; }

    public TimeSpan Duration { get; set; }

    public string LogPath { get; set; } = string.Empty;

    public List<CommandResult> Commands { get; set; } = [];
}

public sealed class CommandResult
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
}
