using System.Windows;
using System.IO;
using AgentBatchRunner.Gui.ViewModels;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Gui;

public partial class PipelineStartFromDialog : Window
{
    private readonly PipelineStartFromPlan _plan;

    public PipelineStartFromDialog(PipelineStartFromPlan plan)
    {
        _plan = plan;
        InitializeComponent();
        TargetText.Text = plan.TargetFilePath;
        foreach (var impact in plan.EarlierFiles)
        {
            ImpactList.Items.Add($"{Path.GetFileName(impact.FilePath)}: {impact.Description}");
        }

        if (plan.EarlierFiles.Count == 0)
        {
            ImpactList.Items.Add("No earlier pending files are affected.");
        }

        ReasonText.Text = plan.Reason;
        StartButton.IsEnabled = plan.CanStart;
        SelectPrerequisiteButton.Visibility = plan.UnmetPrerequisiteIds.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public PipelineStartFromDialogResult Result { get; private set; } = new();

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        Result = new PipelineStartFromDialogResult { Confirmed = true };
        DialogResult = true;
    }

    private void SelectPrerequisite_Click(object sender, RoutedEventArgs e)
    {
        Result = new PipelineStartFromDialogResult
        {
            SelectPrerequisiteId = _plan.UnmetPrerequisiteIds.FirstOrDefault()
        };
        DialogResult = true;
    }
}
