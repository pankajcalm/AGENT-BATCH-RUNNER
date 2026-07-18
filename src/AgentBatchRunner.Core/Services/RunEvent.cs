using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class RunEvent
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public RunEventKind Kind { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? RunId { get; set; }

    public string? PromptId { get; set; }

    public string? Title { get; set; }

    public string? Agent { get; set; }

    public string? BaseAgent { get; set; }

    public string? EffectiveAgent { get; set; }

    public string? AttemptAgent { get; set; }

    public AgentRoutingReason? RoutingReason { get; set; }

    public string? SourceAgent { get; set; }

    public string? ReplacementAgent { get; set; }

    public IReadOnlyList<string> AffectedPromptIds { get; set; } = [];

    public bool IsAutomaticRoutingChange { get; set; }

    public int? AttemptNumber { get; set; }

    public int? MaxAttempts { get; set; }

    public RunStatus? Status { get; set; }

    public string? Command { get; set; }

    public string? WorkingDirectory { get; set; }

    public int? ExitCode { get; set; }

    public TimeSpan? Duration { get; set; }

    public bool TimedOut { get; set; }

    public TimeSpan? Timeout { get; set; }

    public string? StandardOutput { get; set; }

    public string? StandardError { get; set; }

    public string? CombinedOutput { get; set; }

    public string? Path { get; set; }

    public string? FailureReason { get; set; }

    public DateTimeOffset? RateLimitResetAt { get; set; }

    public string? RateLimitReason { get; set; }

    public AgentOutcomeInfo? AgentOutcome { get; set; }
}
