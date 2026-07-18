using System.Windows.Threading;
using AgentBatchRunner.Gui.Services;
using AgentBatchRunner.Gui.ViewModels;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class GuiRoutingTests
{
    [Fact]
    public async Task Validate_FromYaml_ShowsMixedEffectiveAgentsAndResolvedToolchains()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var yamlPath = WriteMixedYaml(temp.Root, "mixed.yaml", repo.Root);
        var preflight = new RecordingPreflightService(new AgentPreflightResult
        {
            Succeeded = true,
            Toolchains =
            [
                new AgentToolchainInfo
                {
                    AgentName = "claude",
                    ExecutablePath = @"C:\Tools\Claude\claude.exe",
                    Version = "2.4.1",
                    Status = AgentPreflightStatus.Succeeded
                },
                new AgentToolchainInfo
                {
                    AgentName = "codex",
                    ExecutablePath = @"C:\Program Files\OpenAI\Codex\codex.exe",
                    Version = "0.144.5",
                    Status = AgentPreflightStatus.Succeeded
                }
            ]
        });
        var viewModel = CreateViewModel(temp.Root, preflight);
        viewModel.PromptFilePath = yamlPath;

        await viewModel.ValidatePromptFileAsync();

        Assert.Equal(AgentRoutingMode.FromYaml, viewModel.SelectedAgent);
        Assert.Equal(["claude", "codex"], viewModel.PromptTasks.Select(task => task.Agent));
        Assert.Equal(["claude", "codex"], Assert.Single(preflight.AgentSets));
        Assert.Contains("From YAML", viewModel.RoutingModeText);
        Assert.Empty(viewModel.RoutingWarningText);
        Assert.Contains(@"C:\Tools\Claude\claude.exe", viewModel.ToolchainDetailsText);
        Assert.Contains(@"C:\Program Files\OpenAI\Codex\codex.exe", viewModel.ToolchainDetailsText);
        Assert.True(viewModel.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExplicitOverride_ReplacesPreviewAgentsShowsWarningAndRequiresNewPreflight()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var viewModel = CreateViewModel(
            temp.Root,
            new RecordingPreflightService(new AgentPreflightResult { Succeeded = true }));
        viewModel.PromptFilePath = WriteMixedYaml(temp.Root, "mixed.yaml", repo.Root);
        await viewModel.ValidatePromptFileAsync();

        viewModel.SelectedAgent = "codex";

        Assert.Equal("Global override: codex", viewModel.SelectedAgent);
        Assert.All(viewModel.PromptTasks, task => Assert.Equal("codex", task.Agent));
        Assert.Contains("Overrides the agent for every prompt", viewModel.RoutingWarningText);
        Assert.False(viewModel.RunCommand.CanExecute(null));
        Assert.Contains("Validate again", viewModel.PreflightStateText);
    }

    [Fact]
    public void SelectingDifferentYaml_ResetsPreviousExplicitOverrideToFromYaml()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var firstPath = WriteMixedYaml(temp.Root, "first.yaml", repo.Root);
        var secondPath = WriteMixedYaml(temp.Root, "second.yaml", repo.Root);
        var viewModel = CreateViewModel(
            temp.Root,
            new RecordingPreflightService(new AgentPreflightResult { Succeeded = true }));
        viewModel.PromptFilePath = firstPath;
        viewModel.SelectedAgent = "claude";

        viewModel.PromptFilePath = secondPath;

        Assert.Equal(AgentRoutingMode.FromYaml, viewModel.SelectedAgent);
        Assert.Empty(viewModel.RoutingWarningText);
        Assert.Empty(viewModel.PromptTasks);
        Assert.False(viewModel.RunCommand.CanExecute(null));
    }

    [Fact]
    public void RoutingChangeMapper_UpdatesPendingRowsAndLeavesCompletedRowsUnchanged()
    {
        var completed = new PromptTaskViewModel
        {
            Id = "P001",
            BaseAgent = "claude",
            EffectiveAgent = "claude",
            RoutingReason = "Yaml",
            Status = "Succeeded"
        };
        var pending = new PromptTaskViewModel
        {
            Id = "P002",
            BaseAgent = "claude",
            EffectiveAgent = "claude",
            RoutingReason = "Yaml",
            Status = "Pending"
        };

        GuiRoutingChangeMapper.Apply(
            [completed, pending],
            new RunEvent
            {
                Kind = RunEventKind.AgentSwitchApplied,
                SourceAgent = "claude",
                ReplacementAgent = "codex",
                EffectiveAgent = "codex",
                RoutingReason = AgentRoutingReason.ManualPendingOverride,
                AffectedPromptIds = ["P001", "P002"]
            });

        Assert.Equal("claude", completed.EffectiveAgent);
        Assert.Equal("codex", pending.EffectiveAgent);
        Assert.Equal("ManualPendingOverride", pending.RoutingReason);
    }

    [Fact]
    public void GlobalAgentSelector_CannotChangeWhileRunIsActive()
    {
        using var temp = TestWorkspace.Create();
        var viewModel = CreateViewModel(
            temp.Root,
            new RecordingPreflightService(new AgentPreflightResult { Succeeded = true }));
        viewModel.SelectedAgent = "claude";

        viewModel.IsRunning = true;
        viewModel.SelectedAgent = "codex";

        Assert.Equal("Global override: claude", viewModel.SelectedAgent);
        Assert.False(viewModel.CanSelectAgent);
    }

    [Fact]
    public async Task Validate_AutoFallback_PreflightsFallbackNotUsedByBaseRouting()
    {
        using var repo = TestWorkspace.CreateGitRepository();
        using var temp = TestWorkspace.Create();
        var yamlPath = Path.Combine(temp.Root, "fallback.yaml");
        Utf8File.WriteAllText(
            yamlPath,
            $$"""
            project: Fallback routing
            repoPath: '{{repo.Root}}'
            defaultAgent: claude
            autoSwitchOnRateLimit: true
            rateLimitFallbacks:
              claude:
                - codex
            prompts:
              - id: P001
                title: Claude task
                prompt: First task.
                verify: []
            """);
        var preflight = new RecordingPreflightService(new AgentPreflightResult { Succeeded = true });
        var viewModel = CreateViewModel(temp.Root, preflight);
        viewModel.PromptFilePath = yamlPath;

        await viewModel.ValidatePromptFileAsync();

        Assert.Equal(["claude", "codex"], Assert.Single(preflight.AgentSets));
    }

    private static MainWindowViewModel CreateViewModel(string root, IAgentPreflightService preflightService)
    {
        return new MainWindowViewModel(
            Dispatcher.CurrentDispatcher,
            new GuiSettingsStore(Path.Combine(root, "gui-settings.json")),
            new AgentRateLimitStateStore(Path.Combine(root, "agent-rate-limits.json")),
            preflightService: preflightService);
    }

    private static string WriteMixedYaml(string root, string fileName, string repoPath)
    {
        var path = Path.Combine(root, fileName);
        Utf8File.WriteAllText(
            path,
            $$"""
            project: Mixed routing
            repoPath: '{{repoPath}}'
            defaultAgent: codex
            prompts:
              - id: P001
                title: Claude task
                agent: claude
                prompt: First task.
                verify: []
              - id: P002
                title: Codex task
                agent: codex
                prompt: Second task.
                verify: []
            """);
        return path;
    }

    private sealed class RecordingPreflightService(AgentPreflightResult result) : IAgentPreflightService
    {
        public List<IReadOnlyList<string>> AgentSets { get; } = [];

        public Task<AgentPreflightResult> RunAsync(
            BatchConfig config,
            IReadOnlyCollection<string> effectiveAgents,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            AgentSets.Add(effectiveAgents.ToList());
            return Task.FromResult(result);
        }
    }
}
