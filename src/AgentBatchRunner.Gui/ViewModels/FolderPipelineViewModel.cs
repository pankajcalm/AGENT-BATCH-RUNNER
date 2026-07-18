using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using AgentBatchRunner.Gui.Services;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;
using Microsoft.Win32;

namespace AgentBatchRunner.Gui.ViewModels;

public sealed class FolderPipelineViewModel : ViewModelBase
{
    private readonly Dispatcher _dispatcher;
    private readonly IGuiPipelineCoordinator _coordinator;
    private readonly Func<string?> _folderPicker;
    private readonly Func<string, bool> _confirmation;
    private readonly Func<PipelineManualActionDialogContext, PipelineManualActionRequest?> _manualActionPrompt;
    private readonly Func<PipelineStartFromPlan, PipelineStartFromDialogResult?> _startFromPrompt;
    private PipelinePlan? _plan;
    private PipelineRunState? _state;
    private PipelineRunControl? _control;
    private CancellationTokenSource? _cancellation;
    private string _folderPath = string.Empty;
    private string _repoPath = string.Empty;
    private string _pipelineRunId = string.Empty;
    private string _pipelineStateText = "Not scanned";
    private string _lastMessage = string.Empty;
    private string _pipelineReportPath = string.Empty;
    private string _selectedExecutionAgent = AgentRoutingMode.FromYaml;
    private string _selectedReviewAgent = AgentRoutingMode.FromYaml;
    private PipelineExecutionMode _selectedMode = PipelineExecutionMode.ConfirmEach;
    private bool _isRunning;
    private PipelineFileViewModel? _selectedFile;

    public FolderPipelineViewModel(
        Dispatcher dispatcher,
        IGuiPipelineCoordinator? coordinator = null,
        Func<string?>? folderPicker = null,
        Func<string, bool>? confirmation = null,
        Func<PipelineManualActionDialogContext, PipelineManualActionRequest?>? manualActionPrompt = null,
        Func<PipelineStartFromPlan, PipelineStartFromDialogResult?>? startFromPrompt = null)
    {
        _dispatcher = dispatcher;
        _coordinator = coordinator ?? new GuiPipelineCoordinator(dispatcher);
        _folderPicker = folderPicker ?? BrowseForFolder;
        _confirmation = confirmation ?? (_ => true);
        _manualActionPrompt = manualActionPrompt ?? (_ => null);
        _startFromPrompt = startFromPrompt ?? (_ => null);
        _coordinator.PipelineEventReceived += OnPipelineEventReceived;
        _coordinator.RunEventReceived += OnRunEventReceived;

        SelectFolderCommand = new RelayCommand(SelectFolder, () => !IsRunning);
        ScanFolderCommand = new AsyncRelayCommand(ScanAsync, CanScan);
        RefreshQueueCommand = new AsyncRelayCommand(ScanAsync, CanScan);
        RunSelectedCommand = new AsyncRelayCommand(RunSelectedAsync, CanRunSelected);
        RunRecommendedNextCommand = new AsyncRelayCommand(RunRecommendedNextAsync, CanRunRecommendedNext);
        ApproveNextCommand = new AsyncRelayCommand(ApproveNextAsync, CanApproveNext);
        RunPipelineCommand = new AsyncRelayCommand(RunPipelineAsync, CanRunPipeline);
        PausePipelineCommand = new RelayCommand(PauseAfterCurrent, () => IsRunning);
        StopPipelineCommand = new RelayCommand(StopPipeline, () => IsRunning);
        ResumePipelineCommand = new AsyncRelayCommand(ResumePipelineAsync, () => !IsRunning && !string.IsNullOrWhiteSpace(RepoPath));
        SkipSelectedCommand = new AsyncRelayCommand(SkipSelectedAsync, CanSkipSelected);
        MarkCompletedManuallyCommand = new AsyncRelayCommand(MarkCompletedManuallyAsync, CanMarkCompletedManually);
        StartFromSelectedCommand = new AsyncRelayCommand(StartFromSelectedAsync, CanStartFromSelected);
        UndoManualStatusCommand = new AsyncRelayCommand(UndoManualStatusAsync, CanUndoManualStatus);
        OpenSourceYamlCommand = new RelayCommand(() => OpenPath(SelectedFile?.FilePath), () => File.Exists(SelectedFile?.FilePath));
        OpenExecutionReportCommand = new RelayCommand(() => OpenPath(SelectedFile?.ExecutionReportPath), () => File.Exists(SelectedFile?.ExecutionReportPath));
        OpenReviewYamlCommand = new RelayCommand(() => OpenPath(SelectedFile?.ReviewYamlPath), () => File.Exists(SelectedFile?.ReviewYamlPath));
        OpenReviewReportCommand = new RelayCommand(() => OpenPath(SelectedFile?.ReviewReportPath), () => File.Exists(SelectedFile?.ReviewReportPath));
        OpenRunFolderCommand = new RelayCommand(() => OpenPath(_state?.PipelineRunDirectory), () => Directory.Exists(_state?.PipelineRunDirectory));
        OpenGitDiffCommand = new RelayCommand(() => OpenPath(SelectedFile?.GitDiffPath), () => File.Exists(SelectedFile?.GitDiffPath));
    }

    public ObservableCollection<PipelineFileViewModel> Files { get; } = [];

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = [];

    public ObservableCollection<PipelineExecutionMode> Modes { get; } =
        [PipelineExecutionMode.Manual, PipelineExecutionMode.ConfirmEach, PipelineExecutionMode.AutoAdvance];

    public ObservableCollection<string> AgentOptions { get; } = new(AgentRoutingMode.Options);

    public string FolderPath
    {
        get => _folderPath;
        set
        {
            if (SetProperty(ref _folderPath, value))
            {
                _plan = null;
                _state = null;
                RepoPath = string.Empty;
                PipelineRunId = string.Empty;
                PipelineStateText = "Not scanned";
                RaiseCommandStates();
            }
        }
    }

    public string RepoPath
    {
        get => _repoPath;
        private set => SetProperty(ref _repoPath, value);
    }

    public string PipelineRunId
    {
        get => _pipelineRunId;
        private set => SetProperty(ref _pipelineRunId, value);
    }

    public string PipelineStateText
    {
        get => _pipelineStateText;
        private set => SetProperty(ref _pipelineStateText, value);
    }

    public string LastMessage
    {
        get => _lastMessage;
        private set => SetProperty(ref _lastMessage, value);
    }

    public string PipelineReportPath
    {
        get => _pipelineReportPath;
        private set => SetProperty(ref _pipelineReportPath, value);
    }

    public string SelectedExecutionAgent
    {
        get => _selectedExecutionAgent;
        set => SetProperty(ref _selectedExecutionAgent, value);
    }

    public string SelectedReviewAgent
    {
        get => _selectedReviewAgent;
        set => SetProperty(ref _selectedReviewAgent, value);
    }

    public PipelineExecutionMode SelectedMode
    {
        get => _selectedMode;
        set => SetProperty(ref _selectedMode, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public PipelineFileViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public RelayCommand SelectFolderCommand { get; }
    public AsyncRelayCommand ScanFolderCommand { get; }
    public AsyncRelayCommand RefreshQueueCommand { get; }
    public AsyncRelayCommand RunSelectedCommand { get; }
    public AsyncRelayCommand RunRecommendedNextCommand { get; }
    public AsyncRelayCommand ApproveNextCommand { get; }
    public AsyncRelayCommand RunPipelineCommand { get; }
    public RelayCommand PausePipelineCommand { get; }
    public RelayCommand StopPipelineCommand { get; }
    public AsyncRelayCommand ResumePipelineCommand { get; }
    public AsyncRelayCommand SkipSelectedCommand { get; }
    public AsyncRelayCommand MarkCompletedManuallyCommand { get; }
    public AsyncRelayCommand StartFromSelectedCommand { get; }
    public AsyncRelayCommand UndoManualStatusCommand { get; }
    public RelayCommand OpenSourceYamlCommand { get; }
    public RelayCommand OpenExecutionReportCommand { get; }
    public RelayCommand OpenReviewYamlCommand { get; }
    public RelayCommand OpenReviewReportCommand { get; }
    public RelayCommand OpenRunFolderCommand { get; }
    public RelayCommand OpenGitDiffCommand { get; }

    public async Task ScanFolderAsync()
    {
        await ScanAsync();
    }

    public async Task SkipSelectedFileAsync()
    {
        await SkipSelectedAsync();
    }

    public async Task MarkSelectedCompletedManuallyAsync()
    {
        await MarkCompletedManuallyAsync();
    }

    public async Task StartPipelineFromSelectedAsync()
    {
        await StartFromSelectedAsync();
    }

    public async Task UndoSelectedManualStatusAsync()
    {
        await UndoManualStatusAsync();
    }

    public void LoadState(PipelineRunState state)
    {
        _state = state;
        RepoPath = state.RepoPath;
        PipelineRunId = state.PipelineRunId;
        PipelineStateText = state.Status.ToString();
        PipelineReportPath = Path.Combine(state.PipelineRunDirectory, "pipeline-report.md");
        LastMessage = state.StopReason ?? state.NextDecision?.Reason ?? string.Empty;
        var selectedId = SelectedFile?.PipelineId;
        var byId = Files.ToDictionary(file => file.PipelineId, StringComparer.OrdinalIgnoreCase);
        foreach (var fileState in state.Files.OrderBy(file => file.QueueOrder))
        {
            if (!byId.TryGetValue(fileState.PipelineId, out var viewModel))
            {
                viewModel = new PipelineFileViewModel();
                Files.Add(viewModel);
            }

            viewModel.UpdateFrom(fileState);
        }

        for (var index = Files.Count - 1; index >= 0; index--)
        {
            if (!state.Files.Any(file => string.Equals(file.PipelineId, Files[index].PipelineId, StringComparison.OrdinalIgnoreCase)))
            {
                Files.RemoveAt(index);
            }
        }

        SelectedFile = !string.IsNullOrWhiteSpace(selectedId)
            ? Files.FirstOrDefault(file => string.Equals(file.PipelineId, selectedId, StringComparison.OrdinalIgnoreCase))
            : Files.FirstOrDefault();
        RaiseCommandStates();
    }

    private void SelectFolder()
    {
        var selected = _folderPicker();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            FolderPath = selected;
        }
    }

    private async Task ScanAsync()
    {
        LogEntries.Clear();
        Files.Clear();
        SelectedFile = null;
        try
        {
            _plan = await _coordinator.PlanAsync(FolderPath, CancellationToken.None);
            foreach (var error in _plan.Errors)
            {
                AddLog("ERROR", error);
            }

            foreach (var warning in _plan.Warnings)
            {
                AddLog("WARN", warning);
            }

            if (!_plan.IsValid)
            {
                PipelineStateText = "Validation failed";
                LastMessage = string.Join(" ", _plan.Errors);
                return;
            }

            foreach (var item in _plan.Files.Select((file, index) => (file, index)))
            {
                Files.Add(PipelineFileViewModel.FromPlan(item.file, item.index + 1));
            }

            RepoPath = _plan.Files.FirstOrDefault()?.Config.RepoPath ?? string.Empty;
            PipelineStateText = "Planned";
            LastMessage = $"{Files.Count} eligible pipeline file(s) discovered.";
            SelectedFile = Files.FirstOrDefault();
            AddLog("INFO", LastMessage);
        }
        catch (Exception ex)
        {
            PipelineStateText = "Scan failed";
            LastMessage = ex.Message;
            AddLog("ERROR", ex.Message);
        }
        finally
        {
            RaiseCommandStates();
        }
    }

    private async Task RunSelectedAsync()
    {
        var selectedId = SelectedFile?.PipelineId
                         ?? throw new InvalidOperationException("Select a pipeline file first.");
        await RunOperationAsync(async token =>
        {
            var state = await EnsureStateAsync(token);
            return await _coordinator.RunPipelineAsync(state, selectedId, _control!, token);
        });
    }

    private async Task RunPipelineAsync()
    {
        await RunOperationAsync(async token =>
        {
            var state = await EnsureStateAsync(token);
            return await _coordinator.RunPipelineAsync(state, null, _control!, token);
        });
    }

    private async Task RunRecommendedNextAsync()
    {
        await RunRecommendedCoreAsync();
    }

    private async Task ApproveNextAsync()
    {
        await RunRecommendedCoreAsync();
    }

    private async Task RunRecommendedCoreAsync()
    {
        if (_state?.NextDecision is not { FilePath: not null } decision)
        {
            return;
        }

        if (decision.RequiresHumanConfirmation &&
            !_confirmation($"Run {Path.GetFileName(decision.FilePath)}?\n\n{decision.Reason}"))
        {
            AddLog("INFO", "Next pipeline file was not approved by the user.");
            return;
        }

        await RunOperationAsync(token =>
            _coordinator.RunRecommendedNextAsync(_state, true, _control!, token));
    }

    private async Task ResumePipelineAsync()
    {
        var directory = _coordinator.FindLatestPipelineDirectory(RepoPath);
        if (directory is null)
        {
            AddLog("WARN", "No saved pipeline run was found for this repository.");
            return;
        }

        await RunOperationAsync(token => _coordinator.ResumeAsync(directory, token));
    }

    private async Task SkipSelectedAsync()
    {
        var selected = SelectedFile;
        if (selected is null)
        {
            return;
        }

        var request = _manualActionPrompt(new PipelineManualActionDialogContext
        {
            Mode = PipelineManualDialogMode.Skip,
            PipelineFileId = selected.PipelineId,
            FilePath = selected.FilePath,
            HasGate = !string.IsNullOrWhiteSpace(selected.Gate)
        });
        if (request is null)
        {
            return;
        }

        await RunOperationAsync(async token =>
        {
            var state = await EnsureStateAsync(token);
            return await _coordinator.SkipAsync(state, selected.PipelineId, request, token);
        });
    }

    private async Task MarkCompletedManuallyAsync()
    {
        var selected = SelectedFile;
        if (selected is null)
        {
            return;
        }

        var request = _manualActionPrompt(new PipelineManualActionDialogContext
        {
            Mode = PipelineManualDialogMode.CompleteManually,
            PipelineFileId = selected.PipelineId,
            FilePath = selected.FilePath,
            HasGate = !string.IsNullOrWhiteSpace(selected.Gate)
        });
        if (request is null)
        {
            return;
        }

        await RunOperationAsync(async token =>
        {
            var state = await EnsureStateAsync(token);
            return await _coordinator.CompleteManuallyAsync(
                state,
                selected.PipelineId,
                request,
                token);
        });
    }

    private async Task StartFromSelectedAsync()
    {
        var selected = SelectedFile;
        if (selected is null)
        {
            return;
        }

        PipelineRunState state;
        PipelineStartFromPlan plan;
        try
        {
            state = await EnsureStateAsync(CancellationToken.None);
            plan = _coordinator.PlanStartFrom(state, selected.PipelineId);
        }
        catch (Exception ex)
        {
            LastMessage = ex.Message;
            AddLog("ERROR", ex.Message);
            return;
        }

        var response = _startFromPrompt(plan);
        if (!string.IsNullOrWhiteSpace(response?.SelectPrerequisiteId))
        {
            SelectedFile = Files.FirstOrDefault(file => string.Equals(
                file.PipelineId,
                response.SelectPrerequisiteId,
                StringComparison.OrdinalIgnoreCase));
            AddLog("INFO", $"Selected unmet prerequisite {response.SelectPrerequisiteId}.");
            return;
        }

        if (response?.Confirmed != true)
        {
            return;
        }

        await RunOperationAsync(token => _coordinator.StartFromSelectedAsync(
            state,
            selected.PipelineId,
            new PipelineStartFromRequest
            {
                Reason = $"User confirmed starting from {selected.FileName}.",
                Actor = Environment.UserName,
                OverrideSource = "WPF Start From Selected",
                Confirmed = true
            },
            _control!,
            token));
    }

    private async Task UndoManualStatusAsync()
    {
        var selected = SelectedFile;
        if (selected is null ||
            !_confirmation($"Undo the manual status for {selected.FileName}? The prior persisted status will be restored only if safe."))
        {
            return;
        }

        await RunOperationAsync(async token =>
        {
            var state = await EnsureStateAsync(token);
            return await _coordinator.UndoManualStatusAsync(
                state,
                selected.PipelineId,
                Environment.UserName,
                "WPF Undo Manual Status",
                token);
        });
    }

    private async Task<PipelineRunState> EnsureStateAsync(CancellationToken cancellationToken)
    {
        if (_state is not null)
        {
            return _state;
        }

        _state = await _coordinator.CreateAsync(
            FolderPath,
            new PipelineRunOptions
            {
                ExecutionMode = SelectedMode,
                ExecutionAgentOverride = AgentRoutingMode.ToOverride(SelectedExecutionAgent),
                ReviewAgentOverride = AgentRoutingMode.ToOverride(SelectedReviewAgent)
            },
            cancellationToken);
        LoadState(_state);
        return _state;
    }

    private async Task RunOperationAsync(
        Func<CancellationToken, Task<PipelineRunState>> operation)
    {
        IsRunning = true;
        _control = new PipelineRunControl();
        _cancellation = new CancellationTokenSource();
        try
        {
            var state = await operation(_cancellation.Token);
            LoadState(state);
        }
        catch (OperationCanceledException)
        {
            AddLog("WARN", "Pipeline canceled safely. Worktree changes were preserved.");
            if (_state is not null)
            {
                LoadState(_state);
            }
        }
        catch (Exception ex)
        {
            LastMessage = ex.Message;
            AddLog("ERROR", ex.Message);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _control = null;
            IsRunning = false;
        }
    }

    private void PauseAfterCurrent()
    {
        _control?.RequestPauseAfterCurrentFile();
        AddLog("INFO", "Pause requested; it will apply after the current YAML file and review finish.");
    }

    private void StopPipeline()
    {
        _control?.RequestStop();
        _cancellation?.Cancel();
        AddLog("WARN", "Pipeline stop requested. No reset or cleanup will be performed.");
    }

    private void OnPipelineEventReceived(object? sender, PipelineEvent pipelineEvent)
    {
        AddLog("INFO", $"{pipelineEvent.Kind}: {pipelineEvent.Message}");
        if (_state is not null)
        {
            LoadState(_state);
        }
    }

    private void OnRunEventReceived(object? sender, RunEvent runEvent)
    {
        var level = runEvent.Kind is
            RunEventKind.AgentFailed or
            RunEventKind.VerificationFailed or
            RunEventKind.PreflightFailed or
            RunEventKind.RunToolchainFailed
            ? "ERROR"
            : runEvent.Kind is
                RunEventKind.AgentRateLimited or
                RunEventKind.AgentTimedOut or
                RunEventKind.VerificationTimedOut or
                RunEventKind.TaskBlocked
                ? "WARN"
                : "INFO";
        AddLog(level, $"{runEvent.Kind}: {runEvent.Message}");
    }

    private void AddLog(string level, string message)
    {
        if (_dispatcher.CheckAccess())
        {
            LogEntries.Add(new LogEntryViewModel(DateTimeOffset.Now, level, message));
            return;
        }

        _dispatcher.Invoke(() => LogEntries.Add(new LogEntryViewModel(DateTimeOffset.Now, level, message)));
    }

    private bool CanScan()
    {
        return !IsRunning && !string.IsNullOrWhiteSpace(FolderPath);
    }

    private bool CanRunSelected()
    {
        return !IsRunning && _plan?.IsValid == true && SelectedFile is not null &&
               _state?.Status is not (
                   PipelineRunStatus.Blocked or
                   PipelineRunStatus.NeedsHumanDecision or
                   PipelineRunStatus.RateLimited or
                   PipelineRunStatus.Canceled or
                   PipelineRunStatus.Failed or
                   PipelineRunStatus.Completed) &&
               SelectedFile.Status is PipelineFileStatus.Pending or PipelineFileStatus.Eligible;
    }

    private bool CanRunPipeline()
    {
        return !IsRunning && _plan?.IsValid == true && Files.Count > 0 &&
               _state?.Status is not (
                   PipelineRunStatus.Blocked or
                   PipelineRunStatus.NeedsHumanDecision or
                   PipelineRunStatus.RateLimited or
                   PipelineRunStatus.Canceled or
                   PipelineRunStatus.Failed or
                   PipelineRunStatus.Completed);
    }

    private bool CanRunRecommendedNext()
    {
        return !IsRunning && _state?.NextDecision?.FilePath is not null &&
               _state.Status == PipelineRunStatus.Paused;
    }

    private bool CanApproveNext()
    {
        return !IsRunning && _state?.NextDecision?.FilePath is not null &&
               _state.Status is PipelineRunStatus.Paused or PipelineRunStatus.Blocked;
    }

    private bool CanSkipSelected()
    {
        return !IsRunning && _plan?.IsValid == true && SelectedFile?.Status is
            PipelineFileStatus.Pending or PipelineFileStatus.Eligible;
    }

    private bool CanMarkCompletedManually()
    {
        return !IsRunning && _plan?.IsValid == true && SelectedFile?.Status is
            PipelineFileStatus.Pending or
            PipelineFileStatus.Eligible or
            PipelineFileStatus.ExecutionSucceeded or
            PipelineFileStatus.CompletedWithoutReview or
            PipelineFileStatus.Blocked or
            PipelineFileStatus.NeedsHumanDecision or
            PipelineFileStatus.PrerequisiteMissing or
            PipelineFileStatus.ReviewFailed or
            PipelineFileStatus.Failed or
            PipelineFileStatus.Canceled;
    }

    private bool CanStartFromSelected()
    {
        return !IsRunning && _plan?.IsValid == true && SelectedFile?.Status is
            PipelineFileStatus.Pending or PipelineFileStatus.Eligible;
    }

    private bool CanUndoManualStatus()
    {
        return !IsRunning && _state is not null && SelectedFile?.Status is
            PipelineFileStatus.SkippedByUser or PipelineFileStatus.ManuallyCompleted;
    }

    private void RaiseCommandStates()
    {
        SelectFolderCommand.RaiseCanExecuteChanged();
        ScanFolderCommand.RaiseCanExecuteChanged();
        RefreshQueueCommand.RaiseCanExecuteChanged();
        RunSelectedCommand.RaiseCanExecuteChanged();
        RunRecommendedNextCommand.RaiseCanExecuteChanged();
        ApproveNextCommand.RaiseCanExecuteChanged();
        RunPipelineCommand.RaiseCanExecuteChanged();
        PausePipelineCommand.RaiseCanExecuteChanged();
        StopPipelineCommand.RaiseCanExecuteChanged();
        ResumePipelineCommand.RaiseCanExecuteChanged();
        SkipSelectedCommand.RaiseCanExecuteChanged();
        MarkCompletedManuallyCommand.RaiseCanExecuteChanged();
        StartFromSelectedCommand.RaiseCanExecuteChanged();
        UndoManualStatusCommand.RaiseCanExecuteChanged();
        OpenSourceYamlCommand.RaiseCanExecuteChanged();
        OpenExecutionReportCommand.RaiseCanExecuteChanged();
        OpenReviewYamlCommand.RaiseCanExecuteChanged();
        OpenReviewReportCommand.RaiseCanExecuteChanged();
        OpenRunFolderCommand.RaiseCanExecuteChanged();
        OpenGitDiffCommand.RaiseCanExecuteChanged();
    }

    private static string? BrowseForFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select AgentBatchRunner pipeline folder",
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
