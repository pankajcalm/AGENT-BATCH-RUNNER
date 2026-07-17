namespace AgentBatchRunner.Models;

public sealed class PromptTask
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Agent { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public List<string> Verify { get; set; } = [];

    public int? MaxRetries { get; set; }

    public int? AgentTimeoutSeconds { get; set; }

    public int? VerifyTimeoutSeconds { get; set; }
}
