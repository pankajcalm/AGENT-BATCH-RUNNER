using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class RunAgentRoutingControllerTests
{
    [Fact]
    public void ApplySwitch_OnlyChangesMatchingExplicitPendingPrompts()
    {
        var controller = new RunAgentRoutingController();

        var change = controller.ApplySwitch(
            new AgentSwitchRequest
            {
                SourceAgent = "claude",
                ReplacementAgent = "codex",
                Reason = AgentRoutingReason.ManualPendingOverride,
                AffectedPromptIds = ["P002", "P003"],
                UserConfirmed = true
            },
            [
                Candidate("P001", "claude"),
                Candidate("P002", "claude"),
                Candidate("P003", "codex")
            ]);

        Assert.NotNull(change);
        Assert.Equal(["P002"], change!.AffectedPromptIds);
        Assert.Equal("claude", controller.Resolve("P001", "claude", AgentRoutingReason.Yaml).EffectiveAgent);
        Assert.Equal("codex", controller.Resolve("P002", "claude", AgentRoutingReason.Yaml).EffectiveAgent);
        Assert.Equal(AgentRoutingReason.ManualPendingOverride, controller.Resolve("P002", "claude", AgentRoutingReason.Yaml).RoutingReason);
        Assert.Equal(AgentRoutingReason.Yaml, controller.Resolve("P003", "codex", AgentRoutingReason.Yaml).RoutingReason);
    }

    [Fact]
    public async Task RoutingSnapshot_RoundTripsThroughRunStateStore()
    {
        using var temp = TestWorkspace.Create();
        var runDirectory = Path.Combine(temp.Root, "run");
        Directory.CreateDirectory(runDirectory);
        var controller = new RunAgentRoutingController();
        controller.ApplySwitch(
            new AgentSwitchRequest
            {
                SourceAgent = "claude",
                ReplacementAgent = "codex",
                Reason = AgentRoutingReason.RateLimitFallback,
                IsAutomatic = true,
                AffectedPromptIds = ["P001", "P002"],
                RateLimitResetAt = DateTimeOffset.Parse("2026-07-17T20:00:00-04:00")
            },
            [Candidate("P001", "claude"), Candidate("P002", "claude")]);
        var store = new RunStateStore();

        await store.SaveRoutingAsync(runDirectory, controller.CreateSnapshot("run-1"));
        var loaded = await store.LoadRoutingAsync(runDirectory);
        var restored = new RunAgentRoutingController(loaded);

        Assert.Equal("run-1", loaded.RunId);
        Assert.Single(loaded.Changes);
        Assert.Equal("codex", restored.Resolve("P002", "claude", AgentRoutingReason.Yaml).EffectiveAgent);
        Assert.Equal(AgentRoutingReason.RateLimitFallback, restored.Resolve("P002", "claude", AgentRoutingReason.Yaml).RoutingReason);
    }

    [Fact]
    public async Task QueueSwitchAsync_IsThreadSafeAndDoesNotApplyBeforeBoundary()
    {
        var controller = new RunAgentRoutingController();
        var requests = Enumerable.Range(1, 20).Select(index => new AgentSwitchRequest
        {
            SourceAgent = "claude",
            ReplacementAgent = "codex",
            Reason = AgentRoutingReason.ManualPendingOverride,
            AffectedPromptIds = [$"P{index:000}"]
        });

        await Task.WhenAll(requests.Select(request => Task.Run(() =>
            controller.QueueSwitchAsync(request, CancellationToken.None))));

        Assert.Empty(controller.CreateSnapshot("run").Changes);
        Assert.Equal(20, controller.DequeueSwitches().Count);
    }

    private static AgentRoutingCandidate Candidate(string id, string baseAgent)
    {
        return new AgentRoutingCandidate
        {
            PromptId = id,
            BaseAgent = baseAgent,
            BaseRoutingReason = AgentRoutingReason.Yaml
        };
    }
}
