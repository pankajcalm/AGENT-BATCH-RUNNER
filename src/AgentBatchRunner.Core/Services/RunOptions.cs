using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class RunOptions
{
    public string? RunId { get; set; }

    public string? AgentOverride { get; set; }

    public RunResult? ExistingResult { get; set; }

    public ISet<string> SkipPromptIds { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
