using AgentBatchRunner.Agents;

namespace AgentBatchRunner.Services;

public sealed class AgentToolchainFailureDetector
{
    public string? Detect(string agentName, AgentExecutionResult result)
    {
        if (result.IsRateLimited)
        {
            return null;
        }

        if (result.ExitCode == -1)
        {
            return $"The resolved {agentName} executable could not be launched: {result.CombinedOutput.Trim()}";
        }

        if (string.Equals(agentName, "codex", StringComparison.OrdinalIgnoreCase) &&
            result.CombinedOutput.Contains("requires a newer version of Codex", StringComparison.OrdinalIgnoreCase))
        {
            return "Codex rejected the invocation because the selected model requires a newer Codex version.";
        }

        if (result.CombinedOutput.Contains("Windows Subsystem for Linux", StringComparison.OrdinalIgnoreCase) ||
            result.CombinedOutput.Contains("install WSL", StringComparison.OrdinalIgnoreCase))
        {
            return $"The resolved {agentName} launcher returned WSL guidance instead of executing the agent.";
        }

        if (result.Succeeded)
        {
            return null;
        }

        return null;
    }
}
