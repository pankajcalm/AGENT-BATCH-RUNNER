using System.Windows;
using AgentBatchRunner.Gui.ViewModels;
using AgentBatchRunner.Models;
using Microsoft.Win32;

namespace AgentBatchRunner.Gui;

public partial class PipelineManualActionDialog : Window
{
    private readonly PipelineManualActionDialogContext _context;

    public PipelineManualActionDialog(PipelineManualActionDialogContext context)
    {
        _context = context;
        InitializeComponent();
        FileText.Text = context.FilePath;
        if (context.Mode == PipelineManualDialogMode.Skip)
        {
            Title = "Skip Pipeline File";
            HeadingText.Text = "Skip selected pipeline file";
            ConfirmButton.Content = "Skip Selected";
            SatisfyDependenciesCheckBox.Visibility = Visibility.Collapsed;
            ApproveGateCheckBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "Mark Pipeline File Completed";
            HeadingText.Text = "Mark selected file completed manually";
            ConfirmButton.Content = "Mark Completed";
            ApproveGateCheckBox.Visibility = context.HasGate
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    public PipelineManualActionRequest? Result { get; private set; }

    private void BrowseEvidence_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select manual completion evidence",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            EvidenceTextBox.Text = dialog.FileName;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ReasonTextBox.Text))
        {
            ValidationText.Text = "A reason is required.";
            ReasonTextBox.Focus();
            return;
        }

        if (ApproveGateCheckBox.IsChecked == true && string.IsNullOrWhiteSpace(EvidenceTextBox.Text))
        {
            ValidationText.Text = "Manual gate approval requires an evidence or report path.";
            EvidenceTextBox.Focus();
            return;
        }

        Result = new PipelineManualActionRequest
        {
            Reason = ReasonTextBox.Text.Trim(),
            EvidencePath = string.IsNullOrWhiteSpace(EvidenceTextBox.Text)
                ? null
                : EvidenceTextBox.Text.Trim(),
            Notes = string.IsNullOrWhiteSpace(NotesTextBox.Text) ? null : NotesTextBox.Text.Trim(),
            SatisfiesDependencies = _context.Mode == PipelineManualDialogMode.CompleteManually &&
                                    SatisfyDependenciesCheckBox.IsChecked == true,
            GateApproved = _context.Mode == PipelineManualDialogMode.CompleteManually &&
                           ApproveGateCheckBox.IsChecked == true,
            Actor = Environment.UserName,
            OverrideSource = _context.Mode == PipelineManualDialogMode.Skip
                ? "WPF Skip Selected"
                : "WPF Mark Completed Manually"
        };
        DialogResult = true;
    }
}
