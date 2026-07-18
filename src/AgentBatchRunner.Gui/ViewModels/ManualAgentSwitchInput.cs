namespace AgentBatchRunner.Gui.ViewModels;

public sealed class ManualAgentSwitchContext
{
    public IReadOnlyList<string> SourceAgents { get; init; } = [];

    public IReadOnlyList<string> ReplacementAgents { get; init; } = [];

    public string SuggestedSourceAgent { get; init; } = string.Empty;

    public int PendingPromptCount { get; init; }

    public bool CanRetryRateLimitedTask { get; init; }
}

public sealed class ManualAgentSwitchInput
{
    public string SourceAgent { get; init; } = string.Empty;

    public string ReplacementAgent { get; init; } = string.Empty;

    public bool RetryRateLimitedTask { get; init; }
}
