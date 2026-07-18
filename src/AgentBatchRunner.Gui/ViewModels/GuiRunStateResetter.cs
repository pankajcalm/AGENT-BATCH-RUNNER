using System.Collections.ObjectModel;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Gui.ViewModels;

public static class GuiRunStateResetter
{
    public static void ResetForRun(
        ObservableCollection<LogEntryViewModel> logEntries,
        ObservableCollection<PromptTaskViewModel> promptTasks,
        BatchConfig config,
        string? agentOverride,
        EffectiveAgentPolicy? effectiveAgentPolicy = null)
    {
        logEntries.Clear();
        promptTasks.Clear();

        var policy = effectiveAgentPolicy ?? new EffectiveAgentPolicy();
        var selections = policy.ResolveAll(config, agentOverride)
            .ToDictionary(selection => selection.PromptId, StringComparer.OrdinalIgnoreCase);
        foreach (var prompt in config.Prompts)
        {
            promptTasks.Add(new PromptTaskViewModel
            {
                Id = prompt.Id,
                Title = prompt.Title,
                PromptText = prompt.Prompt,
                BaseAgent = selections[prompt.Id].BaseAgent,
                EffectiveAgent = selections[prompt.Id].EffectiveAgent,
                RoutingReason = selections[prompt.Id].BaseRoutingReason.ToString(),
                MaxAttempts = prompt.MaxRetries ?? config.DefaultMaxRetries,
                Status = "Pending",
                LastMessage = "Waiting to run.",
                TimedOutText = "False"
            });
        }
    }
}
