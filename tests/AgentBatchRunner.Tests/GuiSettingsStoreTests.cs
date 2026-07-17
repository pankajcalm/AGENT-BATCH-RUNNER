using System.Text.Json;
using System.Windows.Threading;
using AgentBatchRunner.Gui.Models;
using AgentBatchRunner.Gui.Services;
using AgentBatchRunner.Gui.ViewModels;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class GuiSettingsStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSettings()
    {
        using var workspace = TestWorkspace.Create();
        var promptFile = CreatePromptFile(workspace.Root, "prompts.yaml");
        var settingsPath = Path.Combine(workspace.Root, "gui-settings.json");
        var store = new GuiSettingsStore(settingsPath);
        var settings = new GuiSettings
        {
            LastPromptFilePath = promptFile,
            LastSelectedAgent = "codex",
            WindowWidth = 1440,
            WindowHeight = 900,
            WindowLeft = 100,
            WindowTop = 80,
            RecentPromptFiles = [promptFile]
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(promptFile, loaded.LastPromptFilePath);
        Assert.Equal("Global override: codex", loaded.LastSelectedAgent);
        Assert.Equal(1440, loaded.WindowWidth);
        Assert.Equal(900, loaded.WindowHeight);
        Assert.Equal(100, loaded.WindowLeft);
        Assert.Equal(80, loaded.WindowTop);
        Assert.Equal([promptFile], loaded.RecentPromptFiles);
    }

    [Fact]
    public void Load_MissingSettingsFile_ReturnsDefaults()
    {
        using var workspace = TestWorkspace.Create();
        var store = new GuiSettingsStore(Path.Combine(workspace.Root, "missing-settings.json"));

        var settings = store.Load();

        Assert.Null(settings.LastPromptFilePath);
        Assert.Equal(AgentRoutingMode.FromYaml, settings.LastSelectedAgent);
        Assert.Empty(settings.RecentPromptFiles);
    }

    [Fact]
    public void Load_RemovesMissingAndDuplicateRecentFiles()
    {
        using var workspace = TestWorkspace.Create();
        var existingPrompt = CreatePromptFile(workspace.Root, "existing.yaml");
        var missingPrompt = Path.Combine(workspace.Root, "missing.yaml");
        var settingsPath = Path.Combine(workspace.Root, "gui-settings.json");
        WriteSettingsJson(
            settingsPath,
            new
            {
                lastSelectedAgent = "claude",
                recentPromptFiles = new[] { existingPrompt, missingPrompt, existingPrompt }
            });
        var store = new GuiSettingsStore(settingsPath);

        var settings = store.Load();

        var recentFile = Assert.Single(settings.RecentPromptFiles);
        Assert.Equal(existingPrompt, recentFile);
    }

    [Fact]
    public void Save_CapsRecentFilesAtTen()
    {
        using var workspace = TestWorkspace.Create();
        var promptFiles = Enumerable.Range(1, 12)
            .Select(index => CreatePromptFile(workspace.Root, $"prompt-{index:00}.yaml"))
            .ToList();
        var settingsPath = Path.Combine(workspace.Root, "gui-settings.json");
        var store = new GuiSettingsStore(settingsPath);
        var settings = new GuiSettings
        {
            RecentPromptFiles = [.. promptFiles]
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(GuiSettingsStore.MaxRecentFiles, loaded.RecentPromptFiles.Count);
        Assert.Equal(promptFiles.Take(GuiSettingsStore.MaxRecentFiles), loaded.RecentPromptFiles);
    }

    [Fact]
    public void Constructor_StartsFromYamlEvenWhenExplicitOverrideWasStored()
    {
        using var workspace = TestWorkspace.Create();
        var settingsPath = Path.Combine(workspace.Root, "gui-settings.json");
        var store = new GuiSettingsStore(settingsPath);
        store.Save(new GuiSettings { LastSelectedAgent = "codex" });

        var viewModel = new MainWindowViewModel(Dispatcher.CurrentDispatcher, store);

        Assert.Equal(AgentRoutingMode.FromYaml, viewModel.SelectedAgent);
    }

    [Fact]
    public void Constructor_ShowsBlockedAgentAvailability()
    {
        using var workspace = TestWorkspace.Create();
        var settingsStore = new GuiSettingsStore(Path.Combine(workspace.Root, "gui-settings.json"));
        var rateLimitStore = new AgentRateLimitStateStore(
            Path.Combine(workspace.Root, "agent-rate-limits.json"),
            () => DateTimeOffset.Parse("2026-06-26T12:00:00Z"));
        rateLimitStore.SetBlocked(new AgentRateLimitInfo
        {
            AgentName = "codex",
            IsBlocked = true,
            BlockedUntil = DateTimeOffset.Parse("2026-06-26T13:00:00Z"),
            LastDetectedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            Reason = "Too many requests.",
            RawMessage = "Too many requests."
        });

        var viewModel = new MainWindowViewModel(Dispatcher.CurrentDispatcher, settingsStore, rateLimitStore);
        viewModel.SelectedAgent = "codex";

        Assert.Contains("codex: Blocked until", viewModel.CodexAvailabilityText);
        Assert.Contains("Blocked until", viewModel.SelectedAgentAvailabilityText);
        Assert.Equal("Global override: codex", viewModel.SelectedAgent);
    }

    private static string CreatePromptFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "project: Test");
        return path;
    }

    private static void WriteSettingsJson(string settingsPath, object settings)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));
    }
}
