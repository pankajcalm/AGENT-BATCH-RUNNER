namespace AgentBatchRunner.Models;

public enum AgentPreflightStatus
{
    NotRequired,
    Succeeded,
    Failed
}

public sealed class AgentToolchainInfo
{
    public string AgentName { get; set; } = string.Empty;

    public string? ExecutablePath { get; set; }

    public string? Version { get; set; }

    public AgentPreflightStatus Status { get; set; }

    public string? FailureReason { get; set; }
}

public enum RunFailureKind
{
    None,
    PreflightFailed,
    ToolchainFailure,
    AgentOutcomeBlocked
}
