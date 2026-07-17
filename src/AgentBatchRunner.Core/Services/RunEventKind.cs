namespace AgentBatchRunner.Services;

public enum RunEventKind
{
    RunStarted,
    RunCompleted,
    RunCanceled,
    TaskPending,
    TaskStarted,
    CheckpointCreated,
    AttemptStarted,
    AgentStarted,
    AgentCompleted,
    AgentFailed,
    AgentTimedOut,
    AgentRateLimited,
    VerificationStarted,
    VerificationPassed,
    VerificationFailed,
    VerificationTimedOut,
    RetryStarted,
    TaskSucceeded,
    TaskFailed,
    TaskRateLimited,
    RunRateLimited,
    ReportGenerated
}
