using AgentBatchRunner.Services;

namespace AgentBatchRunner.Gui.ViewModels;

public static class GuiRoutingChangeMapper
{
    public static void Apply(
        IEnumerable<PromptTaskViewModel> promptTasks,
        RunEvent runEvent)
    {
        foreach (var promptId in runEvent.AffectedPromptIds)
        {
            var task = promptTasks.FirstOrDefault(item =>
                string.Equals(item.Id, promptId, StringComparison.OrdinalIgnoreCase));
            if (task is null || task.Status is "Succeeded" or "UnverifiedSuccess")
            {
                continue;
            }

            task.EffectiveAgent = runEvent.ReplacementAgent ?? runEvent.EffectiveAgent ?? task.EffectiveAgent;
            task.RoutingReason = runEvent.RoutingReason?.ToString() ?? task.RoutingReason;
            task.LastMessage = $"Pending route changed from {runEvent.SourceAgent} to {runEvent.ReplacementAgent}.";
        }
    }
}
