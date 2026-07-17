namespace AgentBatchRunner.Services;

public static class AgentRateLimitDisplay
{
    public static string Availability(string agentName, AgentRateLimitInfo info)
    {
        if (!info.IsBlocked)
        {
            return "Available";
        }

        return info.BlockedUntil.HasValue
            ? $"Blocked until {FormatLocal(info.BlockedUntil.Value)}"
            : "Blocked until reset time is known";
    }

    public static string BlockedMessage(string agentName, AgentRateLimitInfo info)
    {
        var displayName = DisplayName(agentName);
        return info.BlockedUntil.HasValue
            ? $"{displayName} is rate-limited until {FormatLocal(info.BlockedUntil.Value)}."
            : $"{displayName} is rate-limited. Reset time is unknown.";
    }

    public static string FormatLocal(DateTimeOffset timestamp)
    {
        return timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    }

    private static string DisplayName(string agentName)
    {
        return agentName.Trim().ToLowerInvariant() switch
        {
            "claude" => "Claude",
            "codex" => "Codex",
            "dryrun" => "dryrun",
            _ => agentName
        };
    }
}
