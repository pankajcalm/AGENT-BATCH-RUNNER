using AgentBatchRunner.Models;

namespace AgentBatchRunner.Gui.ViewModels;

public enum PipelineManualDialogMode
{
    Skip,
    CompleteManually
}

public sealed class PipelineManualActionDialogContext
{
    public PipelineManualDialogMode Mode { get; init; }

    public string PipelineFileId { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public bool HasGate { get; init; }
}

public sealed class PipelineStartFromDialogResult
{
    public bool Confirmed { get; init; }

    public string? SelectPrerequisiteId { get; init; }
}
