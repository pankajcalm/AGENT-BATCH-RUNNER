using AgentBatchRunner.Infrastructure;

namespace AgentBatchRunner.Agents;

public interface IAgentAdapterProvider
{
    IAgentAdapter Create(string agent);
}

public class AgentAdapterFactory(ProcessRunner processRunner, ConsoleLogger logger) : IAgentAdapterProvider
{
    private static readonly HashSet<string> SupportedAgents = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude",
        "codex",
        "dryrun"
    };

    public static bool IsSupportedAgent(string? agent)
    {
        return !string.IsNullOrWhiteSpace(agent) && SupportedAgents.Contains(agent);
    }

    public virtual IAgentAdapter Create(string agent)
    {
        return agent.ToLowerInvariant() switch
        {
            "claude" => new ClaudeCodeAdapter(processRunner, logger),
            "codex" => new CodexAdapter(processRunner, logger),
            "dryrun" => new DryRunAgentAdapter(logger),
            _ => throw new InvalidOperationException($"Unsupported agent '{agent}'.")
        };
    }
}
