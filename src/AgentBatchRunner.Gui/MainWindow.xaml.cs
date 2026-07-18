using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentBatchRunner.Gui.Models;
using AgentBatchRunner.Gui.Services;
using AgentBatchRunner.Gui.ViewModels;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Gui;

public partial class MainWindow : Window
{
    private ManualAgentLimitInput? PromptForManualAgentLimit(string suggestedAgent)
    {
        var dialog = new ManualAgentLimitDialog(suggestedAgent) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private ManualAgentSwitchInput? PromptForManualAgentSwitch(ManualAgentSwitchContext context)
    {
        var dialog = new AgentSwitchDialog(context) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private string? PromptForPipelineFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select AgentBatchRunner pipeline folder",
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private bool ConfirmPipelineNext(string message)
    {
        return MessageBox.Show(
                   this,
                   message,
                   "Confirm next pipeline file",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Question,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private PipelineManualActionRequest? PromptForPipelineManualAction(
        PipelineManualActionDialogContext context)
    {
        var dialog = new PipelineManualActionDialog(context) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private PipelineStartFromDialogResult? PromptForPipelineStartFrom(PipelineStartFromPlan plan)
    {
        var dialog = new PipelineStartFromDialog(plan) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainWindowViewModel(
            Dispatcher,
            new GuiSettingsStore(),
            null,
            PromptForManualAgentLimit,
            manualSwitchPrompt: PromptForManualAgentSwitch,
            pipelineFolderPrompt: PromptForPipelineFolder,
            pipelineConfirmation: ConfirmPipelineNext,
            pipelineManualActionPrompt: PromptForPipelineManualAction,
            pipelineStartFromPrompt: PromptForPipelineStartFrom);
        DataContext = viewModel;
        Loaded += (_, _) => RestoreWindowPlacement(viewModel.CurrentSettings);
        Closing += (_, _) => viewModel.SaveWindowPlacement(
            RestoreBounds.Width > 0 ? RestoreBounds.Width : Width,
            RestoreBounds.Height > 0 ? RestoreBounds.Height : Height,
            RestoreBounds.Left,
            RestoreBounds.Top);
    }

    private void RestoreWindowPlacement(GuiSettings settings)
    {
        var maxWidth = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
        var maxHeight = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
        if (settings.WindowWidth is > 0)
        {
            Width = Math.Clamp(settings.WindowWidth.Value, MinWidth, maxWidth);
        }

        if (settings.WindowHeight is > 0)
        {
            Height = Math.Clamp(settings.WindowHeight.Value, MinHeight, maxHeight);
        }

        if (settings.WindowLeft is not { } left || settings.WindowTop is not { } top)
        {
            return;
        }

        if (!IsSafeWindowPosition(left, top, Width, Height))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
    }

    private static bool IsSafeWindowPosition(double left, double top, double width, double height)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top))
        {
            return false;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        var visibleWidth = Math.Min(Math.Max(width, 100), 300);
        var visibleHeight = Math.Min(Math.Max(height, 100), 300);

        return left + visibleWidth > virtualLeft &&
               top + visibleHeight > virtualTop &&
               left < virtualRight - 50 &&
               top < virtualBottom - 50;
    }

    private void PipelineQueue_OnPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dataGrid || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(dataGrid, source) is DataGridRow row)
        {
            dataGrid.SelectedItem = row.Item;
            row.IsSelected = true;
        }
    }
}
