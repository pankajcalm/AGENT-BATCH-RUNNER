namespace AgentBatchRunner.Models;

public sealed class PipelineMetadata
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;

    public int? Order { get; set; }

    public bool Enabled { get; set; } = true;

    public List<string> DependsOn { get; set; } = [];

    public PipelineGateMetadata? Gate { get; set; }

    public PipelineReviewMetadata Review { get; set; } = new();

    public PipelineNextMetadata Next { get; set; } = new();
}

public sealed class PipelineGateMetadata
{
    public string Id { get; set; } = string.Empty;

    public bool RequiredForNextPhase { get; set; }

    public List<string> Criteria { get; set; } = [];
}

public sealed class PipelineReviewMetadata
{
    public bool Required { get; set; }

    public string? Agent { get; set; }

    public bool IndependentAgentPreferred { get; set; }

    public bool AutoGenerate { get; set; } = true;

    public List<string> KnownOwnerDecisions { get; set; } = [];
}

public sealed class PipelineNextMetadata
{
    public string? OnApproved { get; set; }

    public string? OnApprovedWithWarnings { get; set; }

    public string? OnBlocked { get; set; }

    public string? OnNeedsHumanDecision { get; set; }

    public string? OnPrerequisiteMissing { get; set; }

    public string? OnReviewFailed { get; set; }

    public string? OnCanceled { get; set; }

    public string? OnRateLimited { get; set; }
}
