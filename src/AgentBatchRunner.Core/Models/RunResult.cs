namespace AgentBatchRunner.Models;

public sealed class RunResult
{
    public string RunId { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string RepoPath { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? DefaultAgent { get; set; }

    public string? AgentOverride { get; set; }

    public RunFailureKind FailureKind { get; set; }

    public string? RunFailureReason { get; set; }

    public List<AgentToolchainInfo> Toolchains { get; set; } = [];

    public List<AgentRoutingChange> RoutingChanges { get; set; } = [];

    public List<TaskRunResult> Tasks { get; set; } = [];

    public int TotalPrompts => Tasks.Count;

    public int Succeeded => Tasks.Count(t => t.Status == RunStatus.Succeeded);

    public int UnverifiedSuccess => Tasks.Count(t => t.Status == RunStatus.UnverifiedSuccess);

    public int Failed => Tasks.Count(t => t.Status == RunStatus.Failed);

    public int NeedsHumanReview => Tasks.Count(t => t.Status == RunStatus.NeedsHumanReview);

    public int RateLimited => Tasks.Count(t => t.Status == RunStatus.RateLimited);

    public int ToolchainFailures => Tasks.Count(t => t.Status == RunStatus.ToolchainFailure);

    public int Blocked => Tasks.Count(t => t.Status == RunStatus.Blocked);

    public int NeedsHumanDecision => Tasks.Count(t => t.Status == RunStatus.NeedsHumanDecision);

    public int PrerequisiteMissing => Tasks.Count(t => t.Status == RunStatus.PrerequisiteMissing);

    public int Skipped => Tasks.Count(t => t.Status == RunStatus.Skipped);

    public int TimedOutTasks => Tasks.Count(t => t.TimedOut);

    public int TimedOutAttempts => Tasks.Sum(t => t.Attempts.Count(a => a.TimedOut));

    public int AgentSwitches => RoutingChanges.Count;
}

public sealed class TaskRunResult
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Agent { get; set; } = string.Empty;

    public string BaseAgent { get; set; } = string.Empty;

    public string EffectiveAgent { get; set; } = string.Empty;

    public AgentRoutingReason RoutingReason { get; set; }

    public string? LatestAttemptAgent { get; set; }

    public int AgentSwitchCount { get; set; }

    public int RetryAttemptsConsumed { get; set; }

    public string? ConfiguredAgent { get; set; }

    public string? DefaultAgent { get; set; }

    public string? AgentOverride { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Pending;

    public string? CheckpointId { get; set; }

    public string TaskDirectory { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public List<AttemptResult> Attempts { get; set; } = [];

    public bool TimedOut { get; set; }

    public string? LastFailedVerificationCommand { get; set; }

    public int? LastFailedExitCode { get; set; }

    public string? LastFailedLogPath { get; set; }

    public string? LastFailureReason { get; set; }

    public DateTimeOffset? RateLimitResetAt { get; set; }

    public string? RateLimitReason { get; set; }

    public string? RateLimitedSourceAgent { get; set; }

    public string? FallbackAgent { get; set; }

    public AgentOutcomeInfo? AgentOutcome { get; set; }

    public string? RecommendedNextFile { get; set; }
}
