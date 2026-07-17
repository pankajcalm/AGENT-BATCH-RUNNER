using System.Windows.Threading;
using AgentBatchRunner.Gui.Services;
using AgentBatchRunner.Gui.ViewModels;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class GuiRateLimitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-26T12:00:00-04:00");
    private static readonly DateTimeOffset Future = DateTimeOffset.Parse("2026-06-26T13:27:00-04:00");

    [Fact]
    public void Coordinator_SetAgentLimit_PersistsBlockedState()
    {
        using var workspace = TestWorkspace.Create();
        var store = CreateStore(workspace.Root);
        var coordinator = new GuiRunCoordinator(new GuiLogger(Dispatcher.CurrentDispatcher), store);

        coordinator.SetAgentLimit("codex", Future, "Codex messages exhausted");

        var reloaded = CreateStore(workspace.Root);
        Assert.True(reloaded.TryGetBlocked("codex", out var info));
        Assert.Equal(Future, info.BlockedUntil);
        Assert.Equal("Codex messages exhausted", info.Reason);
    }

    [Fact]
    public void Coordinator_ClearAgentLimit_RemovesBlockedState()
    {
        using var workspace = TestWorkspace.Create();
        var store = CreateStore(workspace.Root);
        var coordinator = new GuiRunCoordinator(new GuiLogger(Dispatcher.CurrentDispatcher), store);
        coordinator.SetAgentLimit("codex", Future, "Codex messages exhausted");

        coordinator.ClearAgentLimit("codex");

        Assert.False(CreateStore(workspace.Root).TryGetBlocked("codex", out _));
    }

    [Fact]
    public void ViewModel_ApplyManualAgentLimit_BlocksAgentAndRefreshesAvailability()
    {
        using var workspace = TestWorkspace.Create();
        var store = CreateStore(workspace.Root);
        var viewModel = CreateViewModel(workspace.Root, store);

        viewModel.ApplyManualAgentLimit("codex", Future, "Codex messages exhausted");

        // Persisted to the store.
        Assert.True(store.TryGetBlocked("codex", out _));
        // The GUI view model refreshed availability for the blocked agent.
        Assert.Contains("Blocked until", viewModel.CodexAvailabilityText);
        // The other agent and dryrun stay available.
        Assert.Contains("Available", viewModel.ClaudeAvailabilityText);
        Assert.DoesNotContain("Blocked", viewModel.ClaudeAvailabilityText);
        Assert.Contains("Available", viewModel.DryRunAvailabilityText);
    }

    [Fact]
    public void ViewModel_ApplyManualAgentLimit_DoesNotBlockDryrun()
    {
        using var workspace = TestWorkspace.Create();
        var store = CreateStore(workspace.Root);
        var viewModel = CreateViewModel(workspace.Root, store);

        viewModel.ApplyManualAgentLimit("dryrun", Future, "should be ignored");

        Assert.False(store.TryGetBlocked("dryrun", out _));
        Assert.Contains("Available", viewModel.DryRunAvailabilityText);
    }

    private static AgentRateLimitStateStore CreateStore(string root)
    {
        return new AgentRateLimitStateStore(Path.Combine(root, "agent-rate-limits.json"), () => Now);
    }

    private static MainWindowViewModel CreateViewModel(string root, AgentRateLimitStateStore store)
    {
        var settingsStore = new GuiSettingsStore(Path.Combine(root, "gui-settings.json"));
        return new MainWindowViewModel(Dispatcher.CurrentDispatcher, settingsStore, store);
    }
}
