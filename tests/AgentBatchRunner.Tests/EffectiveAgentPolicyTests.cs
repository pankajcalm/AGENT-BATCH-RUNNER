using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class EffectiveAgentPolicyTests
{
    private readonly EffectiveAgentPolicy _policy = new();

    [Fact]
    public void ResolveAll_FromYaml_PreservesMixedPromptAgentsInOrder()
    {
        var config = CreateConfig(
            new PromptTask { Id = "P001", Agent = "claude" },
            new PromptTask { Id = "P002", Agent = "codex" });

        var agents = _policy.ResolveAll(config, runOverride: null)
            .Select(selection => selection.EffectiveAgent)
            .ToList();

        Assert.Equal(["claude", "codex"], agents);
    }

    [Fact]
    public void Resolve_FromYaml_UsesDefaultOnlyWhenPromptAgentIsMissing()
    {
        var config = CreateConfig(
            new PromptTask { Id = "P001", Agent = "claude" },
            new PromptTask { Id = "P002" });

        var selections = _policy.ResolveAll(config, runOverride: null);

        Assert.Equal("claude", selections[0].EffectiveAgent);
        Assert.Equal("codex", selections[1].EffectiveAgent);
    }

    [Fact]
    public void ResolveAll_ExplicitOverride_ReplacesEveryPromptAndIsClearlyDescribed()
    {
        var config = CreateConfig(
            new PromptTask { Id = "P001", Agent = "claude" },
            new PromptTask { Id = "P002", Agent = "codex" });

        var selections = _policy.ResolveAll(config, "dryrun");

        Assert.All(selections, selection => Assert.Equal("dryrun", selection.EffectiveAgent));
        Assert.Contains("Overrides the agent for every prompt", AgentRoutingMode.Describe("dryrun"));
        Assert.Equal("dryrun", AgentRoutingMode.ToOverride("Global override: dryrun"));
    }

    [Fact]
    public void Resolve_NoPromptOrDefaultAgent_ReturnsActionableValidationError()
    {
        var config = CreateConfig(new PromptTask { Id = "P001" });
        config.DefaultAgent = null;

        var resolved = _policy.TryResolve(config, config.Prompts[0], null, out _, out var error);

        Assert.False(resolved);
        Assert.Contains("no agent", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("defaultAgent", error, StringComparison.OrdinalIgnoreCase);
    }

    private static BatchConfig CreateConfig(params PromptTask[] prompts)
    {
        foreach (var prompt in prompts)
        {
            prompt.Title = "Task";
            prompt.Prompt = "Do the work.";
        }

        return new BatchConfig
        {
            Project = "Demo",
            RepoPath = @"C:\repo",
            DefaultAgent = "codex",
            Prompts = [.. prompts]
        };
    }
}
