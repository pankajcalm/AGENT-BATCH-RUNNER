using System.Collections.ObjectModel;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Gui.ViewModels;

public static class GuiRunStateResetter
{
    public static void ResetForRun(
        ObservableCollection<LogEntryViewModel> logEntries,
        ObservableCollection<PromptTaskViewModel> promptTasks,
        BatchConfig config,
        string selectedAgent)
    {
        logEntries.Clear();
        promptTasks.Clear();

        foreach (var prompt in config.Prompts)
        {
            promptTasks.Add(new PromptTaskViewModel
            {
                Id = prompt.Id,
                Title = prompt.Title,
                PromptText = prompt.Prompt,
                Agent = selectedAgent,
                MaxAttempts = prompt.MaxRetries ?? config.DefaultMaxRetries,
                Status = "Pending",
                LastMessage = "Waiting to run.",
                TimedOutText = "False"
            });
        }
    }
}
