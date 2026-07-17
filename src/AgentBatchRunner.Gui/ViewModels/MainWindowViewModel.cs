using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using AgentBatchRunner.Gui.Models;
using AgentBatchRunner.Gui.Services;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;
using Microsoft.Win32;

namespace AgentBatchRunner.Gui.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly GuiLogger _eventSink;
    private readonly GuiRunCoordinator _coordinator;
    private readonly GuiSettingsStore _settingsStore;
    private readonly GuiSettings _settings;
    private readonly bool _settingsFileExists;
    private readonly Func<string, ManualAgentLimitInput?>? _manualLimitPrompt;
    private BatchConfig? _config;
    private CancellationTokenSource? _runCancellation;
    private string _promptFilePath = string.Empty;
    private string? _selectedRecentPromptFile;
    private string _selectedAgent = "dryrun";
    private string _projectName = string.Empty;
    private string _repoPath = string.Empty;
    private string _defaultAgent = string.Empty;
    private string _agentTimeoutText = string.Empty;
    private string _verifyTimeoutText = string.Empty;
    private string _dryRunAvailabilityText = "dryrun: Available";
    private string _claudeAvailabilityText = "claude: Available";
    private string _codexAvailabilityText = "codex: Available";
    private string _selectedAgentAvailabilityText = "Available";
    private string _currentRunId = string.Empty;
    private string _runStateText = "Idle";
    private string _finalReportPath = string.Empty;
    private string _reportStatusText = string.Empty;
    private string _runFolderPath = string.Empty;
    private bool _isRunning;
    private bool _isLoadingSettings;
    private bool _hasStoredAgentSelection;
    private bool _suppressRecentSelection;
    private PromptTaskViewModel? _selectedTask;

    public MainWindowViewModel(Dispatcher dispatcher)
        : this(dispatcher, new GuiSettingsStore())
    {
    }

    public MainWindowViewModel(
        Dispatcher dispatcher,
        GuiSettingsStore settingsStore,
        AgentRateLimitStateStore? rateLimitStateStore = null,
        Func<string, ManualAgentLimitInput?>? manualLimitPrompt = null)
    {
        _settingsStore = settingsStore;
        _settings = _settingsStore.Load();
        _settingsFileExists = File.Exists(_settingsStore.SettingsPath);
        _manualLimitPrompt = manualLimitPrompt;
        _eventSink = new GuiLogger(dispatcher);
        _eventSink.RunEventReceived += OnRunEventReceived;
        _coordinator = new GuiRunCoordinator(_eventSink, rateLimitStateStore);

        BrowseCommand = new RelayCommand(Browse, () => !IsRunning);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => !IsRunning && !string.IsNullOrWhiteSpace(PromptFilePath));
        RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning && !string.IsNullOrWhiteSpace(PromptFilePath));
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        ClearRecentFilesCommand = new RelayCommand(ClearRecentFiles, () => !IsRunning && RecentPromptFiles.Count > 0);
        RefreshAgentLimitsCommand = new RelayCommand(RefreshAgentLimits, () => !IsRunning);
        SetLimitManuallyCommand = new RelayCommand(SetLimitManually, () => !IsRunning);
        ClearLimitManuallyCommand = new RelayCommand(ClearSelectedAgentLimit, () => !IsRunning && !string.Equals(SelectedAgent, "dryrun", StringComparison.OrdinalIgnoreCase));
        OpenReportCommand = new RelayCommand(OpenReport, () => CanTryOpenReport());
        OpenRunFolderCommand = new RelayCommand(() => OpenPath(RunFolderPath), () => Directory.Exists(RunFolderPath));
        OpenTaskFolderCommand = new RelayCommand(
            () => OpenPath(SelectedTask?.TaskOutputFolder ?? string.Empty),
            () => Directory.Exists(SelectedTask?.TaskOutputFolder ?? string.Empty));
        OpenLatestAttemptFolderCommand = new RelayCommand(
            () => OpenPath(SelectedTask?.LatestAttemptFolder ?? string.Empty),
            () => Directory.Exists(SelectedTask?.LatestAttemptFolder ?? string.Empty));

        LoadSettingsIntoViewModel();
        RefreshAgentLimits();
    }

    public ObservableCollection<string> AgentOptions { get; } = ["dryrun", "claude", "codex"];

    public ObservableCollection<PromptTaskViewModel> PromptTasks { get; } = [];

    public ObservableCollection<LogEntryViewModel> LogEntries { get; } = [];

    public ObservableCollection<string> RecentPromptFiles { get; } = [];

    public GuiSettings CurrentSettings => _settings;

    public RelayCommand BrowseCommand { get; }

    public AsyncRelayCommand ValidateCommand { get; }

    public AsyncRelayCommand RunCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ClearRecentFilesCommand { get; }

    public RelayCommand RefreshAgentLimitsCommand { get; }

    public RelayCommand SetLimitManuallyCommand { get; }

    public RelayCommand ClearLimitManuallyCommand { get; }

    public RelayCommand OpenReportCommand { get; }

    public RelayCommand OpenRunFolderCommand { get; }

    public RelayCommand OpenTaskFolderCommand { get; }

    public RelayCommand OpenLatestAttemptFolderCommand { get; }

    public bool HasRecentFiles => RecentPromptFiles.Count > 0;

    public string? SelectedRecentPromptFile
    {
        get => _selectedRecentPromptFile;
        set
        {
            if (!SetProperty(ref _selectedRecentPromptFile, value) || _suppressRecentSelection)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
            {
                PromptFilePath = value;
            }
        }
    }

    public string PromptFilePath
    {
        get => _promptFilePath;
        set
        {
            if (SetProperty(ref _promptFilePath, value))
            {
                if (!_isLoadingSettings)
                {
                    AddRecentPromptFile(value);
                    SaveSettings();
                }

                _config = null;
                PromptTasks.Clear();
                SelectedTask = null;
                ProjectName = string.Empty;
                RepoPath = string.Empty;
                DefaultAgent = string.Empty;
                AgentTimeoutText = string.Empty;
                VerifyTimeoutText = string.Empty;
                RunStateText = "Idle";
                RaiseCommandStates();
            }
        }
    }

    public string SelectedAgent
    {
        get => _selectedAgent;
        set
        {
            if (!SetProperty(ref _selectedAgent, value))
            {
                return;
            }

            _hasStoredAgentSelection = true;
            _settings.LastSelectedAgent = value;
            if (!_isLoadingSettings)
            {
                SaveSettings();
            }

            if (!IsRunning)
            {
                foreach (var task in PromptTasks)
                {
                    task.Agent = value;
                }
            }

            RefreshSelectedAgentAvailability();
            RaiseCommandStates();
        }
    }

    public string ProjectName
    {
        get => _projectName;
        set => SetProperty(ref _projectName, value);
    }

    public string RepoPath
    {
        get => _repoPath;
        set => SetProperty(ref _repoPath, value);
    }

    public string DefaultAgent
    {
        get => _defaultAgent;
        set => SetProperty(ref _defaultAgent, value);
    }

    public string AgentTimeoutText
    {
        get => _agentTimeoutText;
        set => SetProperty(ref _agentTimeoutText, value);
    }

    public string VerifyTimeoutText
    {
        get => _verifyTimeoutText;
        set => SetProperty(ref _verifyTimeoutText, value);
    }

    public string DryRunAvailabilityText
    {
        get => _dryRunAvailabilityText;
        set => SetProperty(ref _dryRunAvailabilityText, value);
    }

    public string ClaudeAvailabilityText
    {
        get => _claudeAvailabilityText;
        set => SetProperty(ref _claudeAvailabilityText, value);
    }

    public string CodexAvailabilityText
    {
        get => _codexAvailabilityText;
        set => SetProperty(ref _codexAvailabilityText, value);
    }

    public string SelectedAgentAvailabilityText
    {
        get => _selectedAgentAvailabilityText;
        set => SetProperty(ref _selectedAgentAvailabilityText, value);
    }

    public string CurrentRunId
    {
        get => _currentRunId;
        set => SetProperty(ref _currentRunId, value);
    }

    public string RunStateText
    {
        get => _runStateText;
        set => SetProperty(ref _runStateText, value);
    }

    public string FinalReportPath
    {
        get => _finalReportPath;
        set
        {
            if (SetProperty(ref _finalReportPath, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string ReportStatusText
    {
        get => _reportStatusText;
        set => SetProperty(ref _reportStatusText, value);
    }

    public string RunFolderPath
    {
        get => _runFolderPath;
        set
        {
            if (SetProperty(ref _runFolderPath, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public PromptTaskViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                RaiseCommandStates();
            }
        }
    }

    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select AgentBatchRunner prompt file",
            Filter = "YAML prompt files (*.yaml;*.yml)|*.yaml;*.yml|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            PromptFilePath = dialog.FileName;
        }
    }

    private void LoadSettingsIntoViewModel()
    {
        _isLoadingSettings = true;
        try
        {
            RefreshRecentFiles();

            if (_settingsFileExists && AgentOptions.Contains(_settings.LastSelectedAgent))
            {
                SelectedAgent = _settings.LastSelectedAgent;
                _hasStoredAgentSelection = true;
            }

            if (!string.IsNullOrWhiteSpace(_settings.LastPromptFilePath) && File.Exists(_settings.LastPromptFilePath))
            {
                PromptFilePath = _settings.LastPromptFilePath;
                SetSelectedRecentPromptFile(_settings.LastPromptFilePath);
            }
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void AddRecentPromptFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        _settingsStore.AddRecentFile(_settings, filePath);
        RefreshRecentFiles();
        SetSelectedRecentPromptFile(_settings.LastPromptFilePath);
    }

    private void ClearRecentFiles()
    {
        _settingsStore.ClearRecentFiles(_settings);
        RefreshRecentFiles();
        SetSelectedRecentPromptFile(null);
        SaveSettings();
    }

    private void RefreshAgentLimits()
    {
        DryRunAvailabilityText = FormatAgentAvailability("dryrun");
        ClaudeAvailabilityText = FormatAgentAvailability("claude");
        CodexAvailabilityText = FormatAgentAvailability("codex");
        RefreshSelectedAgentAvailability();
    }

    private void RefreshSelectedAgentAvailability()
    {
        SelectedAgentAvailabilityText = AgentRateLimitDisplay.Availability(
            SelectedAgent,
            _coordinator.GetAgentLimit(SelectedAgent));
    }

    private void ClearSelectedAgentLimit()
    {
        _coordinator.ClearAgentLimit(SelectedAgent);
        RefreshAgentLimits();
        AddLog("INFO", $"Cleared saved rate-limit state for {SelectedAgent}.");
    }

    private void SetLimitManually()
    {
        if (_manualLimitPrompt is null)
        {
            AddLog("WARN", "Manual agent-limit dialog is not available.");
            return;
        }

        var suggestedAgent = SelectedAgent is "claude" or "codex" ? SelectedAgent : "claude";
        var input = _manualLimitPrompt(suggestedAgent);
        if (input is null)
        {
            return;
        }

        ApplyManualAgentLimit(input.Agent, input.BlockedUntil, input.Reason);
    }

    /// <summary>
    /// Persists a manual block for an agent and refreshes availability. Only claude and codex can be
    /// blocked; dryrun and the unrelated agent are left untouched. Exposed for tests and the dialog.
    /// </summary>
    public void ApplyManualAgentLimit(string agentName, DateTimeOffset blockedUntil, string reason)
    {
        var normalized = (agentName ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not ("claude" or "codex"))
        {
            AddLog("WARN", $"Cannot manually block agent '{agentName}'. Only claude and codex can be blocked.");
            return;
        }

        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "Usage limit reached" : reason.Trim();
        _coordinator.SetAgentLimit(normalized, blockedUntil, effectiveReason);
        RefreshAgentLimits();
        AddLog("INFO", $"Manually blocked {normalized} until {AgentRateLimitDisplay.FormatLocal(blockedUntil)}. Reason: {effectiveReason}");
    }

    private bool TryGetSelectedAgentBlockedMessage(out string message)
    {
        var info = _coordinator.GetAgentLimit(SelectedAgent);
        if (info.IsBlocked && (!info.BlockedUntil.HasValue || info.BlockedUntil.Value > DateTimeOffset.Now))
        {
            message = AgentRateLimitDisplay.BlockedMessage(SelectedAgent, info);
            return true;
        }

        message = string.Empty;
        return false;
    }

    private string FormatAgentAvailability(string agentName)
    {
        return $"{agentName}: {AgentRateLimitDisplay.Availability(agentName, _coordinator.GetAgentLimit(agentName))}";
    }

    private void RefreshRecentFiles()
    {
        _suppressRecentSelection = true;
        try
        {
            RecentPromptFiles.Clear();
            foreach (var recentFile in _settings.RecentPromptFiles)
            {
                RecentPromptFiles.Add(recentFile);
            }
        }
        finally
        {
            _suppressRecentSelection = false;
        }

        OnPropertyChanged(nameof(HasRecentFiles));
        ClearRecentFilesCommand.RaiseCanExecuteChanged();
    }

    private void SetSelectedRecentPromptFile(string? filePath)
    {
        _suppressRecentSelection = true;
        try
        {
            SelectedRecentPromptFile = string.IsNullOrWhiteSpace(filePath)
                ? null
                : RecentPromptFiles.FirstOrDefault(path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _suppressRecentSelection = false;
        }
    }

    public void SaveWindowPlacement(double width, double height, double left, double top)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0 ||
            double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
        {
            return;
        }

        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        if (!double.IsNaN(left) && !double.IsInfinity(left) &&
            !double.IsNaN(top) && !double.IsInfinity(top))
        {
            _settings.WindowLeft = left;
            _settings.WindowTop = top;
        }

        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            AddLog("WARN", $"Could not save GUI settings: {ex.Message}");
        }
    }

    private async Task ValidateAsync()
    {
        LogEntries.Clear();
        AddLog("INFO", "Validating prompt file.");

        try
        {
            var config = await _coordinator.LoadConfigAsync(PromptFilePath, CancellationToken.None);
            var validation = _coordinator.Validate(config);
            if (!validation.IsValid)
            {
                _config = null;
                RunStateText = "Invalid";
                PromptTasks.Clear();
                foreach (var error in validation.Errors)
                {
                    AddLog("ERROR", error);
                }

                return;
            }

            _config = config;
            ApplyConfig(config);
            RunStateText = "Validated";
            AddLog("INFO", $"Valid prompt file for project {config.Project}.");
        }
        catch (Exception ex)
        {
            _config = null;
            RunStateText = "Invalid";
            AddLog("ERROR", ex.Message);
        }
    }

    private async Task RunAsync()
    {
        RefreshAgentLimits();
        if (TryGetSelectedAgentBlockedMessage(out var blockedMessage))
        {
            RunStateText = "RateLimited";
            AddLog("WARN", blockedMessage);
            return;
        }

        if (_config is null)
        {
            await ValidateAsync();
            if (_config is null)
            {
                return;
            }

            RefreshAgentLimits();
            if (TryGetSelectedAgentBlockedMessage(out blockedMessage))
            {
                RunStateText = "RateLimited";
                AddLog("WARN", blockedMessage);
                return;
            }
        }

        ResetRunState();
        IsRunning = true;
        RunStateText = "Running";
        _runCancellation = new CancellationTokenSource();

        try
        {
            AddLog("INFO", $"Starting run with agent override '{SelectedAgent}'.");
            var result = await _coordinator.RunAsync(_config, SelectedAgent, _runCancellation.Token);
            CurrentRunId = result.RunId;
            if (string.IsNullOrWhiteSpace(RunFolderPath))
            {
                RunFolderPath = Path.Combine(result.RepoPath, ".agentbatchrunner", "runs", result.RunId);
            }

            if (string.IsNullOrWhiteSpace(FinalReportPath))
            {
                FinalReportPath = Path.Combine(RunFolderPath, "final-report.md");
            }

            ReportStatusText = File.Exists(FinalReportPath)
                ? string.Empty
                : ReportAvailability.MissingReportMessage;
            RunStateText = "Completed";
            AddLog("INFO", "Run completed.");
        }
        catch (OperationCanceledException)
        {
            RunStateText = "Canceled";
            MarkActiveTasksCanceled();
            AddLog("WARN", "Run canceled. No git reset/delete/force-push was performed.");
        }
        catch (Exception ex)
        {
            RunStateText = "Failed";
            AddLog("ERROR", ex.Message);
        }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
            IsRunning = false;
        }
    }

    private void Cancel()
    {
        AddLog("WARN", "Cancellation requested.");
        _runCancellation?.Cancel();
    }

    private void ApplyConfig(BatchConfig config)
    {
        ProjectName = config.Project;
        RepoPath = config.RepoPath;
        DefaultAgent = config.DefaultAgent;
        AgentTimeoutText = $"{config.DefaultAgentTimeoutSeconds}s";
        VerifyTimeoutText = $"{config.DefaultVerifyTimeoutSeconds}s";
        if (!_hasStoredAgentSelection)
        {
            SelectedAgent = AgentOptions.Contains(config.DefaultAgent) ? config.DefaultAgent : "dryrun";
        }

        PromptTasks.Clear();
        foreach (var prompt in config.Prompts)
        {
            PromptTasks.Add(new PromptTaskViewModel
            {
                Id = prompt.Id,
                Title = prompt.Title,
                PromptText = prompt.Prompt,
                Agent = SelectedAgent,
                MaxAttempts = prompt.MaxRetries ?? config.DefaultMaxRetries,
                Status = "Pending",
                LastMessage = "Waiting to run."
            });
        }

        SelectedTask = PromptTasks.FirstOrDefault();
    }

    private void ResetRunState()
    {
        CurrentRunId = string.Empty;
        FinalReportPath = string.Empty;
        ReportStatusText = string.Empty;
        RunFolderPath = string.Empty;
        if (_config is not null)
        {
            GuiRunStateResetter.ResetForRun(LogEntries, PromptTasks, _config, SelectedAgent);
            SelectedTask = PromptTasks.FirstOrDefault();
            return;
        }

        LogEntries.Clear();
        PromptTasks.Clear();
        SelectedTask = null;
    }

    private void OnRunEventReceived(object? sender, RunEvent runEvent)
    {
        AddLog(LevelFor(runEvent), runEvent.Message);

        switch (runEvent.Kind)
        {
            case RunEventKind.RunStarted:
                CurrentRunId = runEvent.RunId ?? string.Empty;
                RunFolderPath = runEvent.Path ?? string.Empty;
                RunStateText = "Running";
                break;
            case RunEventKind.ReportGenerated:
                FinalReportPath = runEvent.Path ?? string.Empty;
                RunFolderPath = Path.GetDirectoryName(FinalReportPath) ?? RunFolderPath;
                ReportStatusText = string.Empty;
                break;
            case RunEventKind.RunCompleted:
                RunStateText = "Completed";
                break;
            case RunEventKind.RunRateLimited:
                RunStateText = "RateLimited";
                RefreshAgentLimits();
                break;
            case RunEventKind.RunCanceled:
                RunStateText = "Canceled";
                MarkActiveTasksCanceled();
                break;
            case RunEventKind.TaskPending:
            case RunEventKind.TaskStarted:
            case RunEventKind.AttemptStarted:
            case RunEventKind.CheckpointCreated:
            case RunEventKind.AgentCompleted:
            case RunEventKind.AgentStarted:
            case RunEventKind.AgentFailed:
            case RunEventKind.AgentTimedOut:
            case RunEventKind.AgentRateLimited:
            case RunEventKind.VerificationStarted:
            case RunEventKind.VerificationPassed:
            case RunEventKind.VerificationFailed:
            case RunEventKind.VerificationTimedOut:
            case RunEventKind.RetryStarted:
            case RunEventKind.TaskSucceeded:
            case RunEventKind.TaskFailed:
            case RunEventKind.TaskRateLimited:
                UpdateTask(runEvent);
                break;
        }
    }

    private void UpdateTask(RunEvent runEvent)
    {
        if (string.IsNullOrWhiteSpace(runEvent.PromptId))
        {
            return;
        }

        var task = GetOrCreateTask(runEvent);
        PromptTaskDiagnosticsMapper.ApplyRunEvent(task, runEvent);
        task.Agent = runEvent.Agent ?? task.Agent;
        task.LastMessage = runEvent.Message;
        if (runEvent.MaxAttempts.HasValue)
        {
            task.MaxAttempts = runEvent.MaxAttempts.Value;
        }

        if (runEvent.AttemptNumber.HasValue)
        {
            task.CurrentAttempt = runEvent.AttemptNumber.Value;
        }

        switch (runEvent.Kind)
        {
            case RunEventKind.TaskPending:
                task.Status = "Pending";
                break;
            case RunEventKind.TaskStarted:
                task.Status = "Running";
                task.StartedAt = runEvent.Timestamp;
                task.TaskOutputFolder = runEvent.Path ?? task.TaskOutputFolder;
                break;
            case RunEventKind.AttemptStarted:
                task.Status = "Running";
                break;
            case RunEventKind.AgentStarted:
                task.Status = "Running";
                break;
            case RunEventKind.AgentTimedOut:
                task.Status = "TimedOut";
                task.LastFailureReason = runEvent.Message;
                task.LatestAgentOutput = ReadFileIfExists(runEvent.Path);
                break;
            case RunEventKind.AgentRateLimited:
                task.Status = "RateLimited";
                task.LastFailureReason = runEvent.Message;
                task.LatestAgentOutput = ReadFileIfExists(runEvent.Path);
                RefreshAgentLimits();
                break;
            case RunEventKind.AgentFailed:
                task.Status = "Failed";
                task.LastFailureReason = runEvent.Message;
                task.LatestAgentOutput = ReadFileIfExists(runEvent.Path);
                break;
            case RunEventKind.AgentCompleted:
                task.Status = "Running";
                task.LatestAgentOutput = ReadFileIfExists(runEvent.Path);
                break;
            case RunEventKind.VerificationStarted:
                task.Status = "Running";
                task.FailedCommand = runEvent.Command ?? task.FailedCommand;
                break;
            case RunEventKind.VerificationPassed:
                task.Status = "Running";
                task.LatestVerificationOutput = ReadFileIfExists(runEvent.Path);
                break;
            case RunEventKind.VerificationTimedOut:
                task.Status = "TimedOut";
                task.FailedCommand = runEvent.Command ?? task.FailedCommand;
                task.LastFailureReason = runEvent.Message;
                task.LatestVerificationOutput = ReadFileIfExists(runEvent.Path);
                break;
            case RunEventKind.VerificationFailed:
                task.Status = "Failed";
                task.FailedCommand = runEvent.Command ?? task.FailedCommand;
                task.LastFailureReason = runEvent.Message;
                task.LatestVerificationOutput = ReadFileIfExists(runEvent.Path);
                break;
            case RunEventKind.RetryStarted:
                task.Status = "Running";
                break;
            case RunEventKind.TaskSucceeded:
                task.Status = runEvent.Status?.ToString() ?? "Succeeded";
                task.CompletedAt = runEvent.Timestamp;
                RefreshKnownAttemptFiles(task);
                break;
            case RunEventKind.TaskFailed:
                task.Status = runEvent.TimedOut ? "TimedOut" : runEvent.Status?.ToString() ?? "NeedsHumanReview";
                task.CompletedAt = runEvent.Timestamp;
                task.LastFailureReason = runEvent.FailureReason ?? task.LastFailureReason;
                RefreshKnownAttemptFiles(task);
                break;
            case RunEventKind.TaskRateLimited:
                task.Status = "RateLimited";
                task.CompletedAt = runEvent.Timestamp;
                task.LastFailureReason = runEvent.FailureReason ?? runEvent.Message;
                RefreshKnownAttemptFiles(task);
                RefreshAgentLimits();
                break;
        }

        RaiseCommandStates();
    }

    private PromptTaskViewModel GetOrCreateTask(RunEvent runEvent)
    {
        var existing = PromptTasks.FirstOrDefault(t => string.Equals(t.Id, runEvent.PromptId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var created = new PromptTaskViewModel
        {
            Id = runEvent.PromptId ?? string.Empty,
            Title = runEvent.Title ?? string.Empty,
            Agent = runEvent.Agent ?? SelectedAgent,
            MaxAttempts = runEvent.MaxAttempts ?? 0,
            Status = "Pending"
        };
        PromptTasks.Add(created);
        return created;
    }

    private void MarkActiveTasksCanceled()
    {
        foreach (var task in PromptTasks.Where(t => t.Status is "Pending" or "Running" or "Failed" or "TimedOut"))
        {
            if (task.Status is "Pending" or "Running")
            {
                task.Status = "Canceled";
                task.CompletedAt = DateTimeOffset.Now;
                task.LastMessage = "Canceled by user.";
            }
        }
    }

    private void RefreshKnownAttemptFiles(PromptTaskViewModel task)
    {
        if (string.IsNullOrWhiteSpace(task.TaskOutputFolder) || task.CurrentAttempt <= 0)
        {
            return;
        }

        var attemptFolder = Path.Combine(task.TaskOutputFolder, "attempts", $"attempt-{task.CurrentAttempt}");
        var agentOutput = Path.Combine(attemptFolder, "agent-output.txt");
        var verificationOutput = Path.Combine(attemptFolder, "verification.log");
        task.LatestAttemptFolder = attemptFolder;
        task.AttemptStatusFilePath = Path.Combine(attemptFolder, "status.json");
        if (File.Exists(agentOutput))
        {
            task.AgentOutputFilePath = agentOutput;
            task.LatestAgentOutput = ReadFileIfExists(agentOutput);
        }

        if (File.Exists(verificationOutput))
        {
            task.VerificationLogPath = verificationOutput;
            task.LatestVerificationOutput = ReadFileIfExists(verificationOutput);
        }
    }

    private static string ReadFileIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return $"Could not read {path}: {ex.Message}";
        }
    }

    private static string LevelFor(RunEvent runEvent)
    {
        return runEvent.Kind switch
        {
            RunEventKind.AgentFailed or RunEventKind.VerificationFailed or RunEventKind.TaskFailed => "ERROR",
            RunEventKind.AgentTimedOut or RunEventKind.AgentRateLimited or RunEventKind.VerificationTimedOut or
                RunEventKind.RetryStarted or RunEventKind.RunCanceled or RunEventKind.TaskRateLimited or
                RunEventKind.RunRateLimited => "WARN",
            _ => "INFO"
        };
    }

    private void AddLog(string level, string message)
    {
        LogEntries.Add(new LogEntryViewModel(DateTimeOffset.Now, level, message));
    }

    private void OpenReport()
    {
        var reportPath = !string.IsNullOrWhiteSpace(FinalReportPath)
            ? FinalReportPath
            : string.IsNullOrWhiteSpace(RunFolderPath)
                ? string.Empty
                : Path.Combine(RunFolderPath, "final-report.md");

        var message = ReportAvailability.GetReportOpenMessage(reportPath);
        if (!string.IsNullOrWhiteSpace(message))
        {
            ReportStatusText = message;
            AddLog("WARN", message);
            return;
        }

        ReportStatusText = string.Empty;
        OpenPath(reportPath);
    }

    private bool CanTryOpenReport()
    {
        return !string.IsNullOrWhiteSpace(FinalReportPath) || !string.IsNullOrWhiteSpace(RunFolderPath);
    }

    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void RaiseCommandStates()
    {
        BrowseCommand.RaiseCanExecuteChanged();
        ValidateCommand.RaiseCanExecuteChanged();
        RunCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ClearRecentFilesCommand.RaiseCanExecuteChanged();
        RefreshAgentLimitsCommand.RaiseCanExecuteChanged();
        SetLimitManuallyCommand.RaiseCanExecuteChanged();
        ClearLimitManuallyCommand.RaiseCanExecuteChanged();
        OpenReportCommand.RaiseCanExecuteChanged();
        OpenRunFolderCommand.RaiseCanExecuteChanged();
        OpenTaskFolderCommand.RaiseCanExecuteChanged();
        OpenLatestAttemptFolderCommand.RaiseCanExecuteChanged();
    }
}
