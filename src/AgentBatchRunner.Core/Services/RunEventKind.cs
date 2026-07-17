namespace AgentBatchRunner.Services;

public enum RunEventKind
{
    RunStarted,
    PreflightStarted,
    AgentPreflightSucceeded,
    PreflightFailed,
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
    AgentToolchainFailed,
    VerificationStarted,
    VerificationPassed,
    VerificationFailed,
    VerificationTimedOut,
    RetryStarted,
    TaskSucceeded,
    TaskFailed,
    TaskRateLimited,
    TaskToolchainFailed,
    TaskSkipped,
    RunRateLimited,
    RunToolchainFailed,
    ReportGenerated
}
