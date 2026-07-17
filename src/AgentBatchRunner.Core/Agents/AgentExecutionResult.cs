namespace AgentBatchRunner.Agents;

public sealed class AgentExecutionResult
{
    public string AgentName { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public int ExitCode { get; set; }

    public TimeSpan Duration { get; set; }

    public bool TimedOut { get; set; }

    public TimeSpan? Timeout { get; set; }

    public string StandardOutput { get; set; } = string.Empty;

    public string StandardError { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    public bool IsRateLimited { get; set; }

    public DateTimeOffset? RateLimitResetAt { get; set; }

    public string? RateLimitReason { get; set; }

    public bool Succeeded => ExitCode == 0 && !IsRateLimited;

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
