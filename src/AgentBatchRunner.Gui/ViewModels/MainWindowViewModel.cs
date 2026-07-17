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
    private readonly Func<string, ManualAgentLimitInput?>? _manualLimitPrompt;
    private readonly EffectiveAgentPolicy _effectiveAgentPolicy = new();
    private BatchConfig? _config;
    private AgentPreflightResult? _preflightResult;
    private CancellationTokenSource? _runCancellation;
    private string _promptFilePath = string.Empty;
    private string? _selectedRecentPromptFile;
    private string _selectedAgent = AgentRoutingMode.FromYaml;
    private string _projectName = string.Empty;
    private string _repoPath = string.Empty;
    private string _defaultAgent = string.Empty;
    private string _agentTimeoutText = string.Empty;
    private string _verifyTimeoutText = string.Empty;
    private string _dryRunAvailabilityText = "dryrun: Available";
    private string _claudeAvailabilityText = "claude: Available";
    private string _codexAvailabilityText = "codex: Available";
    private string _selectedAgentAvailabilityText = "Available";
    private string _routingModeText = "From YAML";
    private string _routingWarningText = string.Empty;
    private string _preflightStateText = "Not validated";
    private string _toolchainDetailsText = string.Empty;
    private string _currentRunId = string.Empty;
    private string _runStateText = "Idle";
    private string _finalReportPath = string.Empty;
    private string _reportStatusText = string.Empty;
    private string _runFolderPath = string.Empty;
    private bool _isRunning;
    private bool _isLoadingSettings;
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
        Func<string, ManualAgentLimitInput?>? manualLimitPrompt = null,
        IAgentPreflightService? preflightService = null)
    {
        _settingsStore = settingsStore;
        _settings = _settingsStore.Load();
        _manualLimitPrompt = manualLimitPrompt;
        _eventSink = new GuiLogger(dispatcher);
        _eventSink.RunEventReceived += OnRunEventReceived;
        _coordinator = new GuiRunCoordinator(
            _eventSink,
            rateLimitStateStore,
            _effectiveAgentPolicy,
            preflightService);

        BrowseCommand = new RelayCommand(Browse, () => !IsRunning);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => !IsRunning && !string.IsNullOrWhiteSpace(PromptFilePath));
        RunCommand = new AsyncRelayCommand(
            RunAsync,
            () => !IsRunning && _config is not null && _preflightResult?.Succeeded == true);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        ClearRecentFilesCommand = new RelayCommand(ClearRecentFiles, () => !IsRunning && RecentPromptFiles.Count > 0);
        RefreshAgentLimitsCommand = new RelayCommand(RefreshAgentLimits, () => !IsRunning);
        SetLimitManuallyCommand = new RelayCommand(SetLimitManually, () => !IsRunning);
        ClearLimitManuallyCommand = new RelayCommand(
            ClearSelectedAgentLimit,
            () => !IsRunning && AgentRoutingMode.ToOverride(SelectedAgent) is "claude" or "codex");
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

    public ObservableCollection<string> AgentOptions { get; } = new(AgentRoutingMode.Options);

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
                _preflightResult = null;
                SelectedAgent = AgentRoutingMode.FromYaml;
                PromptTasks.Clear();
                SelectedTask = null;
                ProjectName = string.Empty;
                RepoPath = string.Empty;
                DefaultAgent = string.Empty;
                AgentTimeoutText = string.Empty;
                VerifyTimeoutText = string.Empty;
                PreflightStateText = "Not validated";
                ToolchainDetailsText = string.Empty;
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
            var normalizedSelection = AgentRoutingMode.FromOverride(AgentRoutingMode.ToOverride(value));
            if (!SetProperty(ref _selectedAgent, normalizedSelection))
            {
                return;
            }

            _settings.LastSelectedAgent = normalizedSelection;
            if (!_isLoadingSettings)
            {
                SaveSettings();
            }

            _preflightResult = null;
            RoutingModeText = AgentRoutingMode.Describe(AgentRoutingMode.ToOverride(normalizedSelection));
            RoutingWarningText = AgentRoutingMode.ToOverride(normalizedSelection) is { } agentOverride
                ? $"Global override active: {agentOverride}. Overrides the agent for every prompt in this run."
                : string.Empty;
            if (_config is not null && !IsRunning)
            {
                UpdatePromptAgentPreview(_config);
                PreflightStateText = "Routing changed. Validate again before running.";
                ToolchainDetailsText = string.Empty;
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

    public string RoutingModeText
    {
        get => _routingModeText;
        set => SetProperty(ref _routingModeText, value);
    }

    public string RoutingWarningText
    {
        get => _routingWarningText;
        set => SetProperty(ref _routingWarningText, value);
    }

    public string PreflightStateText
    {
        get => _preflightStateText;
        set => SetProperty(ref _preflightStateText, value);
    }

    public string ToolchainDetailsText
    {
        get => _toolchainDetailsText;
        set => SetProperty(ref _toolchainDetailsText, value);
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
            SelectedAgent = AgentRoutingMode.FromYaml;

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
        var agentOverride = AgentRoutingMode.ToOverride(SelectedAgent);
        if (agentOverride is not null)
        {
            SelectedAgentAvailabilityText = AgentRateLimitDisplay.Availability(
                agentOverride,
                _coordinator.GetAgentLimit(agentOverride));
            return;
        }

        if (_config is null)
        {
            SelectedAgentAvailabilityText = "From YAML: validate to check all selected agents.";
            return;
        }

        var availability = _coordinator.ResolveAgents(_config, null)
            .Select(selection => selection.EffectiveAgent)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(agentName => $"{agentName}: {AgentRateLimitDisplay.Availability(agentName, _coordinator.GetAgentLimit(agentName))}");
        SelectedAgentAvailabilityText = string.Join("; ", availability);
    }

    private void ClearSelectedAgentLimit()
    {
        var agentName = AgentRoutingMode.ToOverride(SelectedAgent);
        if (agentName is not ("claude" or "codex"))
        {
            return;
        }

        _coordinator.ClearAgentLimit(agentName);
        RefreshAgentLimits();
        AddLog("INFO", $"Cleared saved rate-limit state for {agentName}.");
    }

    private void SetLimitManually()
    {
        if (_manualLimitPrompt is null)
        {
            AddLog("WARN", "Manual agent-limit dialog is not available.");
            return;
        }

        var selectedOverride = AgentRoutingMode.ToOverride(SelectedAgent);
        var suggestedAgent = selectedOverride is "claude" or "codex" ? selectedOverride : "claude";
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
        var agentNames = _config is null
            ? new[] { AgentRoutingMode.ToOverride(SelectedAgent) }.Where(name => name is not null).Cast<string>()
            : _coordinator.ResolveAgents(_config, AgentRoutingMode.ToOverride(SelectedAgent))
                .Select(selection => selection.EffectiveAgent)
                .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var agentName in agentNames)
        {
            var info = _coordinator.GetAgentLimit(agentName);
            if (info.IsBlocked && (!info.BlockedUntil.HasValue || info.BlockedUntil.Value > DateTimeOffset.Now))
            {
                message = AgentRateLimitDisplay.BlockedMessage(agentName, info);
                return true;
            }
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
            RunStateText = "Preflight";
            PreflightStateText = "Checking required agent executables and versions...";
            AddLog("INFO", $"Valid prompt file for project {config.Project}. Agent preflight started.");

            _preflightResult = await _coordinator.PreflightAsync(
                config,
                AgentRoutingMode.ToOverride(SelectedAgent),
                CancellationToken.None);
            ToolchainDetailsText = FormatToolchainDetails(_preflightResult);
            if (!_preflightResult.Succeeded)
            {
                RunStateText = "PreflightFailed";
                PreflightStateText = _preflightResult.FailureReason ?? "Agent preflight failed.";
                AddLog("ERROR", PreflightStateText);
                RaiseCommandStates();
                return;
            }

            RunStateText = "Validated";
            PreflightStateText = "Ready";
            foreach (var toolchain in _preflightResult.Toolchains)
            {
                AddLog(
                    "INFO",
                    toolchain.Status == AgentPreflightStatus.NotRequired
                        ? $"{toolchain.AgentName}: built-in adapter; no executable required."
                        : $"{toolchain.AgentName}: {toolchain.ExecutablePath} ({toolchain.Version}).");
            }

            AddLog("INFO", "Validation and agent preflight passed. Run is enabled.");
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            _config = null;
            _preflightResult = null;
            RunStateText = "Invalid";
            PreflightStateText = "Failed";
            AddLog("ERROR", ex.Message);
        }
    }

    public Task ValidatePromptFileAsync()
    {
        return ValidateAsync();
    }

    private async Task RunAsync()
    {
        if (_config is null || _preflightResult?.Succeeded != true)
        {
            AddLog("WARN", "Validate the prompt file and pass agent preflight before running.");
            return;
        }

        RefreshAgentLimits();
        if (TryGetSelectedAgentBlockedMessage(out var blockedMessage))
        {
            RunStateText = "RateLimited";
            AddLog("WARN", blockedMessage);
            return;
        }

        ResetRunState();
        IsRunning = true;
        RunStateText = "Running";
        _runCancellation = new CancellationTokenSource();

        try
        {
            var agentOverride = AgentRoutingMode.ToOverride(SelectedAgent);
            AddLog("INFO", agentOverride is null
                ? "Starting run using per-prompt YAML routing."
                : $"Starting run with global agent override '{agentOverride}'.");
            var result = await _coordinator.RunAsync(
                _config,
                agentOverride,
                _preflightResult,
                _runCancellation.Token);
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
            RunStateText = result.FailureKind switch
            {
                RunFailureKind.PreflightFailed => "PreflightFailed",
                RunFailureKind.ToolchainFailure => "ToolchainFailure",
                _ when result.RateLimited > 0 => "RateLimited",
                _ => "Completed"
            };
            AddLog(
                result.FailureKind == RunFailureKind.None ? "INFO" : "ERROR",
                result.FailureKind == RunFailureKind.None
                    ? "Run completed."
                    : result.RunFailureReason ?? "Run stopped because an agent toolchain failed.");
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
        DefaultAgent = config.DefaultAgent ?? "(none)";
        AgentTimeoutText = $"{config.DefaultAgentTimeoutSeconds}s";
        VerifyTimeoutText = $"{config.DefaultVerifyTimeoutSeconds}s";
        UpdatePromptAgentPreview(config);
        RoutingModeText = AgentRoutingMode.Describe(AgentRoutingMode.ToOverride(SelectedAgent));
        RefreshSelectedAgentAvailability();
        SelectedTask = PromptTasks.FirstOrDefault();
    }

    private void UpdatePromptAgentPreview(BatchConfig config)
    {
        var selections = _coordinator.ResolveAgents(config, AgentRoutingMode.ToOverride(SelectedAgent))
            .ToDictionary(selection => selection.PromptId, StringComparer.OrdinalIgnoreCase);
        PromptTasks.Clear();
        foreach (var prompt in config.Prompts)
        {
            PromptTasks.Add(new PromptTaskViewModel
            {
                Id = prompt.Id,
                Title = prompt.Title,
                PromptText = prompt.Prompt,
                Agent = selections[prompt.Id].EffectiveAgent,
                MaxAttempts = prompt.MaxRetries ?? config.DefaultMaxRetries,
                Status = "Pending",
                LastMessage = "Waiting to run.",
                TimedOutText = "False"
            });
        }

        SelectedTask = PromptTasks.FirstOrDefault();
    }

    private static string FormatToolchainDetails(AgentPreflightResult preflight)
    {
        if (preflight.Toolchains.Count == 0)
        {
            return preflight.Succeeded ? "No external agent executable is required." : string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            preflight.Toolchains.Select(toolchain => toolchain.Status == AgentPreflightStatus.NotRequired
                ? $"{toolchain.AgentName}: built-in"
                : $"{toolchain.AgentName}: {toolchain.ExecutablePath} | version {toolchain.Version ?? "unknown"} | {toolchain.Status}"));
    }

    private void ResetRunState()
    {
        CurrentRunId = string.Empty;
        FinalReportPath = string.Empty;
        ReportStatusText = string.Empty;
        RunFolderPath = string.Empty;
        if (_config is not null)
        {
            GuiRunStateResetter.ResetForRun(
                LogEntries,
                PromptTasks,
                _config,
                AgentRoutingMode.ToOverride(SelectedAgent),
                _effectiveAgentPolicy);
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
            case RunEventKind.PreflightStarted:
                PreflightStateText = "Checking required agent executables and versions...";
                break;
            case RunEventKind.AgentPreflightSucceeded:
                PreflightStateText = "Ready";
                break;
            case RunEventKind.PreflightFailed:
                PreflightStateText = runEvent.FailureReason ?? runEvent.Message;
                RunStateText = "PreflightFailed";
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
            case RunEventKind.RunToolchainFailed:
                RunStateText = "ToolchainFailure";
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
            case RunEventKind.AgentToolchainFailed:
            case RunEventKind.VerificationStarted:
            case RunEventKind.VerificationPassed:
            case RunEventKind.VerificationFailed:
            case RunEventKind.VerificationTimedOut:
            case RunEventKind.RetryStarted:
            case RunEventKind.TaskSucceeded:
            case RunEventKind.TaskFailed:
            case RunEventKind.TaskRateLimited:
            case RunEventKind.TaskToolchainFailed:
            case RunEventKind.TaskSkipped:
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
            case RunEventKind.AgentToolchainFailed:
                task.Status = "ToolchainFailure";
                task.LastFailureReason = runEvent.FailureReason ?? runEvent.Message;
                task.LatestAgentOutput = ReadFileIfExists(runEvent.Path);
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
            case RunEventKind.TaskToolchainFailed:
                task.Status = "ToolchainFailure";
                task.CompletedAt = runEvent.Timestamp;
                task.LastFailureReason = runEvent.FailureReason ?? runEvent.Message;
                RefreshKnownAttemptFiles(task);
                break;
            case RunEventKind.TaskSkipped:
                task.Status = "Skipped";
                task.CompletedAt = runEvent.Timestamp;
                task.LastFailureReason = runEvent.FailureReason ?? runEvent.Message;
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
            Agent = runEvent.Agent ?? AgentRoutingMode.ToOverride(SelectedAgent) ?? string.Empty,
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
            return AgentBatchRunner.Infrastructure.Utf8File.ReadAllText(path);
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
            RunEventKind.AgentFailed or RunEventKind.AgentToolchainFailed or RunEventKind.VerificationFailed or
                RunEventKind.TaskFailed or RunEventKind.TaskToolchainFailed or RunEventKind.PreflightFailed or
                RunEventKind.RunToolchainFailed => "ERROR",
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
