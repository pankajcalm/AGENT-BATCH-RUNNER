namespace AgentBatchRunner.Models;

public sealed class PipelineFile
{
    public string FilePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public BatchConfig Config { get; set; } = new();

    public PipelineMetadata? Metadata => Config.Pipeline;

    public bool IsLegacy => Metadata is null;

    public string PipelineId => !string.IsNullOrWhiteSpace(Metadata?.Id)
        ? Metadata.Id
        : RelativePath;

    public string Title => !string.IsNullOrWhiteSpace(Metadata?.Title)
        ? Metadata.Title
        : Config.Project;

    public string Phase => Metadata?.Phase ?? string.Empty;

    public int? Order => Metadata?.Order;

    public IReadOnlyList<string> Dependencies => Metadata?.DependsOn ?? [];
}

public sealed class PipelineFolderDiscoveryResult
{
    public string FolderPath { get; set; } = string.Empty;

    public DateTimeOffset DiscoveredAt { get; set; }

    public List<PipelineFile> Files { get; set; } = [];

    public List<string> Errors { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public bool IsValid => Errors.Count == 0;
}

public sealed class PipelinePlan
{
    public string FolderPath { get; set; } = string.Empty;

    public List<PipelineFile> Files { get; set; } = [];

    public List<string> Errors { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public bool IsValid => Errors.Count == 0;
}
