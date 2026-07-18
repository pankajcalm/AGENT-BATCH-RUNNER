using AgentBatchRunner.Agents;

namespace AgentBatchRunner.Models;

public sealed class AttemptResult
{
    public int AttemptNumber { get; set; }

    public string AttemptAgent { get; set; } = string.Empty;

    public bool ConsumesRetry { get; set; } = true;

    public int AgentSwitchNumber { get; set; }

    public AgentRoutingReason RoutingReason { get; set; }

    public string? PreviousAgent { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Pending;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string AttemptDirectory { get; set; } = string.Empty;

    public bool TimedOut { get; set; }

    public string? TimeoutReason { get; set; }

    public AgentExecutionResult? AgentResult { get; set; }

    public AgentOutcomeInfo? AgentOutcome { get; set; }

    public VerificationResult? VerificationResult { get; set; }
}
