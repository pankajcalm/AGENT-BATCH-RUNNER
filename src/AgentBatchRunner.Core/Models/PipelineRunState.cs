namespace AgentBatchRunner.Models;

public enum PipelineExecutionMode
{
    Manual,
    ConfirmEach,
    AutoAdvance
}

public enum PipelineRunStatus
{
    Planning,
    Running,
    Paused,
    Completed,
    Blocked,
    NeedsHumanDecision,
    RateLimited,
    Canceled,
    Failed
}

public enum PipelineFileStatus
{
    Pending,
    Eligible,
    Running,
    ExecutionSucceeded,
    Reviewing,
    CompletedWithoutReview,
    Approved,
    ApprovedWithWarnings,
    Blocked,
    NeedsHumanDecision,
    PrerequisiteMissing,
    ReviewFailed,
    RateLimited,
    Canceled,
    Failed,
    Skipped,
    SkippedByUser,
    ManuallyCompleted
}

public enum PipelineManualActionKind
{
    SkippedByUser,
    ManuallyCompleted,
    StartFromSelected,
    UndoManualStatus
}

public sealed class PipelineRunState
{
    public string PipelineRunId { get; set; } = string.Empty;

    public string FolderPath { get; set; } = string.Empty;

    public string RepoPath { get; set; } = string.Empty;

    public string PipelineRunDirectory { get; set; } = string.Empty;

    public PipelineExecutionMode ExecutionMode { get; set; } = PipelineExecutionMode.ConfirmEach;

    public PipelineRunStatus Status { get; set; } = PipelineRunStatus.Planning;

    public string? ExecutionAgentOverride { get; set; }

    public string? ReviewAgentOverride { get; set; }

    public bool RequireReviewForLegacyFiles { get; set; }

    public bool AutoAdvanceApprovedWithWarnings { get; set; }

    public int MaximumAutomaticTransitions { get; set; } = 20;

    public int AutomaticTransitions { get; set; }

    public string? CurrentFileId { get; set; }

    public string? RecommendedNextFile { get; set; }

    public NextPipelineFileDecision? NextDecision { get; set; }

    public string? StopReason { get; set; }

    public bool PauseRequested { get; set; }

    public bool StopRequested { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public List<PipelineFileRunState> Files { get; set; } = [];

    public List<PipelineTransitionRecord> Transitions { get; set; } = [];

    public List<PipelineManualActionRecord> ManualActionHistory { get; set; } = [];
}

public sealed class PipelineFileRunState
{
    public int QueueOrder { get; set; }

    public string PipelineId { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;

    public int? DeclaredOrder { get; set; }

    public bool IsLegacy { get; set; }

    public string ExecutionAgent { get; set; } = string.Empty;

    public string ReviewAgent { get; set; } = string.Empty;

    public bool ExecutionAgentAvailable { get; set; } = true;

    public bool ReviewAgentAvailable { get; set; } = true;

    public List<string> DependencyIds { get; set; } = [];

    public List<string> GatePrerequisiteFileIds { get; set; } = [];

    public PipelineGateMetadata? Gate { get; set; }

    public PipelineNextMetadata Next { get; set; } = new();

    public bool ReviewRequired { get; set; }

    public PipelineFileStatus Status { get; set; } = PipelineFileStatus.Pending;

    public RunStatus? ExecutionStatus { get; set; }

    public string? ExecutionRunId { get; set; }

    public string? ExecutionRunDirectory { get; set; }

    public string? ExecutionReportPath { get; set; }

    public string? GitDiffPath { get; set; }

    public PipelineReviewVerdict? ReviewVerdict { get; set; }

    public string? ReviewYamlPath { get; set; }

    public string? ReviewRunId { get; set; }

    public string? ReviewReportPath { get; set; }

    public string? ReviewResultPath { get; set; }

    public List<PipelineReviewResult> ReviewHistory { get; set; } = [];

    public List<PipelineReviewFinding> Findings { get; set; } = [];

    public List<string> RequiredDecisions { get; set; } = [];

    public string? RecommendedNextFile { get; set; }

    public string LastMessage { get; set; } = string.Empty;

    public string? ManualReason { get; set; }

    public string? ManualEvidencePath { get; set; }

    public string? ManualNotes { get; set; }

    public DateTimeOffset? ManualTimestamp { get; set; }

    public string? ManualActor { get; set; }

    public string? ManualOverrideSource { get; set; }

    public bool ManualSatisfiesDependencies { get; set; }

    public bool ManualGateApproved { get; set; }

    public string? ActiveManualActionId { get; set; }

    public List<string> MissingDependencyIds { get; set; } = [];

    public List<string> MissingGatePrerequisiteIds { get; set; } = [];

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public TimeSpan? Duration => StartedAt.HasValue && CompletedAt.HasValue
        ? CompletedAt.Value - StartedAt.Value
        : null;

    public bool IsApproved => ReviewVerdict == PipelineReviewVerdict.Approved;

    public bool HasCompletedExecution => !string.IsNullOrWhiteSpace(ExecutionRunId);
}

public sealed class PipelineRunOptions
{
    public PipelineExecutionMode ExecutionMode { get; set; } = PipelineExecutionMode.ConfirmEach;

    public string? ExecutionAgentOverride { get; set; }

    public string? ReviewAgentOverride { get; set; }

    public bool RequireReviewForLegacyFiles { get; set; }

    public bool AutoAdvanceApprovedWithWarnings { get; set; }

    public int MaximumAutomaticTransitions { get; set; } = 20;
}

public sealed class PipelineTransitionRecord
{
    public DateTimeOffset Timestamp { get; set; }

    public string Event { get; set; } = string.Empty;

    public string? PipelineFileId { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class PipelineManualActionRecord
{
    public string AuditId { get; set; } = Guid.NewGuid().ToString("N");

    public string FileId { get; set; } = string.Empty;

    public PipelineManualActionKind Action { get; set; }

    public PipelineFileStatus PreviousStatus { get; set; }

    public PipelineFileStatus NewStatus { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? EvidencePath { get; set; }

    public string? Notes { get; set; }

    public bool SatisfiesDependencies { get; set; }

    public bool GateApproved { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string OverrideSource { get; set; } = string.Empty;

    public string? ReversesAuditId { get; set; }

    public List<string> AffectedFileIds { get; set; } = [];
}

public sealed class PipelineManualActionRequest
{
    public string Reason { get; set; } = string.Empty;

    public string? EvidencePath { get; set; }

    public string? Notes { get; set; }

    public bool SatisfiesDependencies { get; set; }

    public bool GateApproved { get; set; }

    public string Actor { get; set; } = string.Empty;

    public string OverrideSource { get; set; } = string.Empty;
}

public sealed class PipelineStartFromRequest
{
    public string Reason { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public string OverrideSource { get; set; } = string.Empty;

    public bool Confirmed { get; set; }
}

public sealed class PipelineStartFromPlan
{
    public string TargetFileId { get; set; } = string.Empty;

    public string TargetFilePath { get; set; } = string.Empty;

    public bool CanStart { get; set; }

    public string Reason { get; set; } = string.Empty;

    public List<string> UnmetPrerequisiteIds { get; set; } = [];

    public List<PipelineStartFromImpact> EarlierFiles { get; set; } = [];
}

public sealed class PipelineStartFromImpact
{
    public string FileId { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public PipelineFileStatus CurrentStatus { get; set; }

    public bool WillBeSkipped { get; set; }

    public bool IsRequiredPrerequisite { get; set; }

    public string Description { get; set; } = string.Empty;
}

public sealed class NextPipelineFileDecision
{
    public string? FilePath { get; set; }

    public string Reason { get; set; } = string.Empty;

    public bool CanAutoRun { get; set; }

    public bool RequiresHumanConfirmation { get; set; }

    public bool IsReReview { get; set; }

    public List<string> CandidateFilePaths { get; set; } = [];
}
