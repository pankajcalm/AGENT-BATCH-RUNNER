namespace AgentBatchRunner.Models;

public sealed class BatchValidationResult
{
    public List<string> Errors { get; } = [];

    public bool IsValid => Errors.Count == 0;
}
