namespace AgentBatchRunner.Models;

public enum PipelineReviewVerdict
{
    Approved,
    ApprovedWithWarnings,
    Blocked,
    NeedsHumanDecision,
    PrerequisiteMissing,
    ReviewFailed,
    Canceled,
    RateLimited
}

public enum PipelineFindingSeverity
{
    Info,
    Warning,
    Medium,
    High,
    Critical
}

public sealed class PipelineReviewResult
{
    public string SchemaVersion { get; set; } = "1.0";

    public string SourcePipelineFile { get; set; } = string.Empty;

    public string ExecutionRunId { get; set; } = string.Empty;

    public string ReviewRunId { get; set; } = string.Empty;

    public string ExecutionStatus { get; set; } = string.Empty;

    public PipelineReviewVerdict ReviewVerdict { get; set; }

    public string? GateId { get; set; }

    public bool GateApproved { get; set; }

    public string Summary { get; set; } = string.Empty;

    public List<PipelineReviewFinding> Findings { get; set; } = [];

    public List<string> RequiredDecisions { get; set; } = [];

    public string? RecommendedNextFile { get; set; }

    public bool CanAutoAdvance { get; set; }

    public string? FailureReason { get; set; }

    public string? ReviewYamlPath { get; set; }

    public string? ReviewResultPath { get; set; }

    public string? ReviewReportPath { get; set; }
}

public sealed class PipelineReviewFinding
{
    public string Id { get; set; } = string.Empty;

    public PipelineFindingSeverity Severity { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public bool RequiresHumanDecision { get; set; }
}

public sealed class GeneratedPipelineReview
{
    public int Iteration { get; set; }

    public string ReviewPromptId { get; set; } = string.Empty;

    public string ReviewAgent { get; set; } = string.Empty;

    public string ReviewYamlPath { get; set; } = string.Empty;

    public string ReviewResultPath { get; set; } = string.Empty;

    public string ReviewReportPath { get; set; } = string.Empty;

    public string ReviewPrompt { get; set; } = string.Empty;
}

public sealed class PipelineReviewGenerationRequest
{
    public PipelineFile SourceFile { get; set; } = new();

    public RunResult ExecutionResult { get; set; } = new();

    public string ExecutionRunDirectory { get; set; } = string.Empty;

    public string PipelineRunDirectory { get; set; } = string.Empty;

    public string ReviewAgent { get; set; } = string.Empty;

    public int ReviewIteration { get; set; }

    public List<string> KnownOwnerDecisions { get; set; } = [];

    public PipelineReviewResult? PreviousReview { get; set; }
}

public sealed class PipelineReviewExecutionResult
{
    public string ReviewRunDirectory { get; set; } = string.Empty;

    public GeneratedPipelineReview GeneratedReview { get; set; } = new();

    public PipelineReviewResult ReviewResult { get; set; } = new();

    public Agents.AgentExecutionResult? AgentResult { get; set; }

    public VerificationResult? VerificationResult { get; set; }

    public bool ProductFilesChanged { get; set; }
}
