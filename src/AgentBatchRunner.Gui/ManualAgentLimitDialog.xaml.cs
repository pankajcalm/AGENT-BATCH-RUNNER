using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AgentBatchRunner.Gui.ViewModels;

namespace AgentBatchRunner.Gui;

public partial class ManualAgentLimitDialog : Window
{
    public ManualAgentLimitDialog()
        : this("claude")
    {
    }

    public ManualAgentLimitDialog(string suggestedAgent)
    {
        InitializeComponent();

        SelectAgent(suggestedAgent);
        var defaultUntil = DateTimeOffset.Now.AddHours(1);
        BlockedUntilDatePicker.SelectedDate = defaultUntil.LocalDateTime.Date;
        BlockedUntilTimeTextBox.Text = defaultUntil.ToString("HH:mm", CultureInfo.InvariantCulture);
        ReasonTextBox.Text = "Usage limit reached";
    }

    public ManualAgentLimitInput? Result { get; private set; }

    private void SelectAgent(string suggestedAgent)
    {
        var normalized = (suggestedAgent ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("claude" or "codex"))
        {
            normalized = "claude";
        }

        foreach (var item in AgentComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                AgentComboBox.SelectedItem = item;
                return;
            }
        }

        AgentComboBox.SelectedIndex = 0;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (AgentComboBox.SelectedItem is not ComboBoxItem selectedAgent ||
            string.IsNullOrWhiteSpace(selectedAgent.Content?.ToString()))
        {
            ShowValidation("Select an agent (claude or codex).");
            return;
        }

        if (BlockedUntilDatePicker.SelectedDate is not { } date)
        {
            ShowValidation("Pick a 'blocked until' date.");
            return;
        }

        if (!TryParseTimeOfDay(BlockedUntilTimeTextBox.Text, out var timeOfDay))
        {
            ShowValidation("Enter the time as 24-hour HH:mm, e.g. 13:27.");
            return;
        }

        var blockedUntilLocal = date.Date.Add(timeOfDay);
        var blockedUntil = new DateTimeOffset(blockedUntilLocal, TimeZoneInfo.Local.GetUtcOffset(blockedUntilLocal));
        if (blockedUntil <= DateTimeOffset.Now)
        {
            ShowValidation("The 'blocked until' time must be in the future.");
            return;
        }

        var reason = string.IsNullOrWhiteSpace(ReasonTextBox.Text) ? "Usage limit reached" : ReasonTextBox.Text.Trim();
        Result = new ManualAgentLimitInput(selectedAgent.Content!.ToString()!, blockedUntil, reason);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ShowValidation(string message)
    {
        ValidationTextBlock.Text = message;
        ValidationTextBlock.Visibility = Visibility.Visible;
    }

    private static bool TryParseTimeOfDay(string? text, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return TimeSpan.TryParseExact(text.Trim(), ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out timeOfDay)
            || TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out timeOfDay);
    }
}
