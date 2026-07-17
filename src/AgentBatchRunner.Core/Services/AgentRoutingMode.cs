namespace AgentBatchRunner.Services;

public static class AgentRoutingMode
{
    public const string FromYaml = "From YAML (recommended)";

    private const string OverridePrefix = "Global override: ";

    public static IReadOnlyList<string> Options { get; } =
    [
        FromYaml,
        OverridePrefix + "dryrun",
        OverridePrefix + "claude",
        OverridePrefix + "codex"
    ];

    public static string? ToOverride(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection) ||
            string.Equals(selection, FromYaml, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(selection, "from-yaml", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return selection.StartsWith(OverridePrefix, StringComparison.OrdinalIgnoreCase)
            ? EffectiveAgentPolicy.NormalizeOptional(selection[OverridePrefix.Length..])
            : EffectiveAgentPolicy.NormalizeOptional(selection);
    }

    public static string FromOverride(string? agentOverride)
    {
        var normalized = EffectiveAgentPolicy.NormalizeOptional(agentOverride);
        return normalized is null ? FromYaml : OverridePrefix + normalized;
    }

    public static string Describe(string? agentOverride)
    {
        var normalized = EffectiveAgentPolicy.NormalizeOptional(agentOverride);
        return normalized is null
            ? "From YAML: prompt agent, then defaultAgent."
            : $"Global override: {normalized}. Overrides the agent for every prompt in this run.";
    }
}
