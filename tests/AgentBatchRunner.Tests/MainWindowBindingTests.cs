using System.Runtime.CompilerServices;
using System.Windows.Threading;
using System.Xml.Linq;
using AgentBatchRunner.Gui;
using AgentBatchRunner.Gui.ViewModels;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Tests;

public sealed class MainWindowBindingTests
{
    [Fact]
    public void PipelineReadOnlyDetails_UseOneWayBindings()
    {
        var document = XDocument.Load(GetMainWindowXamlPath());
        var xamlNamespace = document.Root!.Name.Namespace;
        var bindings = document
            .Descendants(xamlNamespace + "TextBox")
            .Where(element => string.Equals(
                (string?)element.Attribute("IsReadOnly"),
                "True",
                StringComparison.OrdinalIgnoreCase))
            .Select(element => (string?)element.Attribute("Text"))
            .Where(value => value?.Contains("SelectedFile.", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Contains("{Binding SelectedFile.FindingsText, Mode=OneWay}", bindings);
        Assert.Contains("{Binding SelectedFile.RequiredDecisionsText, Mode=OneWay}", bindings);
    }

    [Fact]
    public void PipelineManualActions_AppearInToolbarAndContextMenu()
    {
        var document = XDocument.Load(GetMainWindowXamlPath());
        var commandValues = document.Descendants()
            .Select(element => (string?)element.Attribute("Command"))
            .Where(value => value is not null)
            .ToArray();

        foreach (var command in new[]
                 {
                     "SkipSelectedCommand",
                     "MarkCompletedManuallyCommand",
                     "StartFromSelectedCommand",
                     "UndoManualStatusCommand"
                 })
        {
            Assert.True(
                commandValues.Count(value => value!.Contains(command, StringComparison.Ordinal)) >= 2,
                $"{command} must be available from both the toolbar and queue context menu.");
        }
    }

    [Fact]
    public void SelectingPipelineFile_DoesNotWriteToReadOnlyDetailProperties()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
                viewModel.FolderPipeline.LoadState(new PipelineRunState
                {
                    PipelineRunId = "binding-smoke",
                    RepoPath = Path.GetTempPath(),
                    PipelineRunDirectory = Path.GetTempPath(),
                    Status = PipelineRunStatus.Paused,
                    Files =
                    [
                        new PipelineFileRunState
                        {
                            QueueOrder = 1,
                            PipelineId = "BINDING",
                            FileName = "binding.yaml",
                            FilePath = Path.Combine(Path.GetTempPath(), "binding.yaml"),
                            Status = PipelineFileStatus.Blocked,
                            Findings =
                            [
                                new PipelineReviewFinding
                                {
                                    Id = "B-1",
                                    Severity = PipelineFindingSeverity.High,
                                    Title = "Binding smoke finding"
                                }
                            ],
                            RequiredDecisions = ["Confirm the next action."]
                        }
                    ]
                });

                Dispatcher.CurrentDispatcher.Invoke(
                    DispatcherPriority.DataBind,
                    () => { });

                Assert.Equal("BINDING", viewModel.FolderPipeline.SelectedFile?.PipelineId);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "WPF binding smoke test did not finish.");
        Assert.Null(failure);
    }

    private static string GetMainWindowXamlPath([CallerFilePath] string testFilePath = "")
    {
        return Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFilePath)!,
            "..",
            "..",
            "src",
            "AgentBatchRunner.Gui",
            "MainWindow.xaml"));
    }
}
