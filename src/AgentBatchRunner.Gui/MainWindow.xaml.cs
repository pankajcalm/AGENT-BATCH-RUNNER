using System.Windows;
using AgentBatchRunner.Gui.Models;
using AgentBatchRunner.Gui.Services;
using AgentBatchRunner.Gui.ViewModels;

namespace AgentBatchRunner.Gui;

public partial class MainWindow : Window
{
    private ManualAgentLimitInput? PromptForManualAgentLimit(string suggestedAgent)
    {
        var dialog = new ManualAgentLimitDialog(suggestedAgent) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainWindowViewModel(Dispatcher, new GuiSettingsStore(), null, PromptForManualAgentLimit);
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
}
