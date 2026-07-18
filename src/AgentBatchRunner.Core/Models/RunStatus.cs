namespace AgentBatchRunner.Models;

public enum RunStatus
{
    Pending,
    Running,
    Succeeded,

    /// <summary>
    /// The agent reported success but the prompt had no verification commands, so the result
    /// could not be automatically verified. Treated as a (weaker) success, never as a failure.
    /// </summary>
    UnverifiedSuccess,
    Failed,
    ToolchainFailure,
    RateLimited,
    Blocked,
    NeedsHumanDecision,
    PrerequisiteMissing,
    Canceled,
    TimedOut,
    NeedsHumanReview,
    Skipped
}
