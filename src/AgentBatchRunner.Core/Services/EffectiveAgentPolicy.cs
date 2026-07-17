using AgentBatchRunner.Agents;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class EffectiveAgentPolicy
{
    public EffectiveAgentSelection Resolve(BatchConfig config, PromptTask prompt, string? runOverride)
    {
        if (!TryResolve(config, prompt, runOverride, out var selection, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return selection!;
    }

    public bool TryResolve(
        BatchConfig config,
        PromptTask prompt,
        string? runOverride,
        out EffectiveAgentSelection? selection,
        out string error)
    {
        var normalizedOverride = NormalizeOptional(runOverride);
        var configuredAgent = NormalizeOptional(prompt.Agent);
        var defaultAgent = NormalizeOptional(config.DefaultAgent);

        foreach (var candidate in new[]
                 {
                     (Value: normalizedOverride, Label: "run-level override"),
                     (Value: configuredAgent, Label: $"prompt '{prompt.Id}' agent"),
                     (Value: defaultAgent, Label: "defaultAgent")
                 })
        {
            if (candidate.Value is not null && !AgentAdapterFactory.IsSupportedAgent(candidate.Value))
            {
                selection = null;
                error = $"Unsupported {candidate.Label} '{candidate.Value}'. Use claude, codex, or dryrun.";
                return false;
            }
        }

        var effectiveAgent = normalizedOverride ?? configuredAgent ?? defaultAgent;
        if (effectiveAgent is null)
        {
            selection = null;
            error = $"Prompt '{prompt.Id}' has no agent and no usable defaultAgent.";
            return false;
        }

        selection = new EffectiveAgentSelection
        {
            PromptId = prompt.Id,
            ConfiguredAgent = configuredAgent,
            DefaultAgent = defaultAgent,
            RunOverride = normalizedOverride,
            EffectiveAgent = effectiveAgent
        };
        error = string.Empty;
        return true;
    }

    public IReadOnlyList<EffectiveAgentSelection> ResolveAll(
        BatchConfig config,
        string? runOverride,
        IReadOnlySet<string>? skipPromptIds = null)
    {
        return config.Prompts
            .Where(prompt => skipPromptIds?.Contains(prompt.Id) != true)
            .Select(prompt => Resolve(config, prompt, runOverride))
            .ToList();
    }

    public IReadOnlyList<string> ResolveDistinctAgents(
        BatchConfig config,
        string? runOverride,
        IReadOnlySet<string>? skipPromptIds = null)
    {
        return ResolveAll(config, runOverride, skipPromptIds)
            .Select(selection => selection.EffectiveAgent)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? NormalizeOptional(string? agent)
    {
        return string.IsNullOrWhiteSpace(agent) ? null : agent.Trim().ToLowerInvariant();
    }
}

public sealed class EffectiveAgentSelection
{
    public string PromptId { get; init; } = string.Empty;

    public string? ConfiguredAgent { get; init; }

    public string? DefaultAgent { get; init; }

    public string? RunOverride { get; init; }

    public string EffectiveAgent { get; init; } = string.Empty;
}
