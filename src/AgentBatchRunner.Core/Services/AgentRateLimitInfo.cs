namespace AgentBatchRunner.Services;

public sealed class AgentRateLimitInfo
{
    public string AgentName { get; set; } = string.Empty;

    public bool IsBlocked { get; set; }

    public DateTimeOffset? BlockedUntil { get; set; }

    public DateTimeOffset LastDetectedAt { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string RawMessage { get; set; } = string.Empty;
}
