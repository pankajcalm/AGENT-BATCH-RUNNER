using System.Windows;
using System.Windows.Controls;
using AgentBatchRunner.Gui.ViewModels;

namespace AgentBatchRunner.Gui;

public partial class AgentSwitchDialog : Window
{
    public AgentSwitchDialog(ManualAgentSwitchContext context)
    {
        InitializeComponent();
        SourceAgentBox.ItemsSource = context.SourceAgents;
        ReplacementAgentBox.ItemsSource = context.ReplacementAgents;
        SourceAgentBox.SelectedItem = context.SourceAgents.FirstOrDefault(agent =>
            string.Equals(agent, context.SuggestedSourceAgent, StringComparison.OrdinalIgnoreCase)) ??
            context.SourceAgents.FirstOrDefault();
        ReplacementAgentBox.SelectedItem = context.ReplacementAgents.FirstOrDefault(agent =>
            !string.Equals(agent, SourceAgentBox.SelectedItem as string, StringComparison.OrdinalIgnoreCase));
        ScopeText.Text = $"Pending prompts currently routed to the selected source agent ({context.PendingPromptCount} pending total).";
        RetryRateLimitedTaskBox.Visibility = context.CanRetryRateLimitedTask
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryRateLimitedTaskBox.IsChecked = context.CanRetryRateLimitedTask;
        UpdateConfirmState();
    }

    public ManualAgentSwitchInput? Result { get; private set; }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateConfirmState();
    }

    private void UpdateConfirmState()
    {
        if (ConfirmButton is null)
        {
            return;
        }

        ConfirmButton.IsEnabled = SourceAgentBox.SelectedItem is string source &&
                                  ReplacementAgentBox.SelectedItem is string replacement &&
                                  !string.Equals(source, replacement, StringComparison.OrdinalIgnoreCase);
    }

    private void ConfirmClick(object sender, RoutedEventArgs e)
    {
        if (SourceAgentBox.SelectedItem is not string source ||
            ReplacementAgentBox.SelectedItem is not string replacement ||
            string.Equals(source, replacement, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Result = new ManualAgentSwitchInput
        {
            SourceAgent = source,
            ReplacementAgent = replacement,
            RetryRateLimitedTask = RetryRateLimitedTaskBox.IsChecked == true
        };
        DialogResult = true;
    }
}
