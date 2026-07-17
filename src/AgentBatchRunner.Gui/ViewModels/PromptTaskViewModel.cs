namespace AgentBatchRunner.Gui.ViewModels;

public sealed class PromptTaskViewModel : ViewModelBase
{
    private string _agent = string.Empty;
    private int _currentAttempt;
    private int _maxAttempts;
    private string _status = "Pending";
    private string _lastMessage = string.Empty;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private string _taskOutputFolder = string.Empty;
    private string _latestAgentOutput = string.Empty;
    private string _latestVerificationOutput = string.Empty;
    private string _failedCommand = string.Empty;
    private string _lastFailureReason = string.Empty;
    private string _command = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _exitCodeText = string.Empty;
    private string _timedOutText = string.Empty;
    private string _timeoutText = string.Empty;
    private string _stdout = string.Empty;
    private string _stderr = string.Empty;
    private string _combinedOutput = string.Empty;
    private string _exceptionDetails = string.Empty;
    private string _attemptStatusFilePath = string.Empty;
    private string _agentOutputFilePath = string.Empty;
    private string _verificationLogPath = string.Empty;
    private string _latestAttemptFolder = string.Empty;

    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string PromptText { get; init; } = string.Empty;

    public string Agent
    {
        get => _agent;
        set => SetProperty(ref _agent, value);
    }

    public int CurrentAttempt
    {
        get => _currentAttempt;
        set
        {
            if (SetProperty(ref _currentAttempt, value))
            {
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public int MaxAttempts
    {
        get => _maxAttempts;
        set => SetProperty(ref _maxAttempts, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string LastMessage
    {
        get => _lastMessage;
        set => SetProperty(ref _lastMessage, value);
    }

    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        set
        {
            if (SetProperty(ref _startedAt, value))
            {
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public DateTimeOffset? CompletedAt
    {
        get => _completedAt;
        set
        {
            if (SetProperty(ref _completedAt, value))
            {
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public string DurationText
    {
        get
        {
            if (StartedAt is null)
            {
                return string.Empty;
            }

            var end = CompletedAt ?? DateTimeOffset.Now;
            var duration = end - StartedAt.Value;
            return duration.TotalSeconds < 1 ? "<1s" : $"{duration.TotalSeconds:0}s";
        }
    }

    public string TaskOutputFolder
    {
        get => _taskOutputFolder;
        set => SetProperty(ref _taskOutputFolder, value);
    }

    public string LatestAgentOutput
    {
        get => _latestAgentOutput;
        set => SetProperty(ref _latestAgentOutput, value);
    }

    public string LatestVerificationOutput
    {
        get => _latestVerificationOutput;
        set => SetProperty(ref _latestVerificationOutput, value);
    }

    public string Command
    {
        get => _command;
        set => SetProperty(ref _command, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public string ExitCodeText
    {
        get => _exitCodeText;
        set => SetProperty(ref _exitCodeText, value);
    }

    public string TimedOutText
    {
        get => _timedOutText;
        set => SetProperty(ref _timedOutText, value);
    }

    public string TimeoutText
    {
        get => _timeoutText;
        set => SetProperty(ref _timeoutText, value);
    }

    public string Stdout
    {
        get => _stdout;
        set => SetProperty(ref _stdout, value);
    }

    public string Stderr
    {
        get => _stderr;
        set => SetProperty(ref _stderr, value);
    }

    public string CombinedOutput
    {
        get => _combinedOutput;
        set => SetProperty(ref _combinedOutput, value);
    }

    public string ExceptionDetails
    {
        get => _exceptionDetails;
        set => SetProperty(ref _exceptionDetails, value);
    }

    public string AttemptStatusFilePath
    {
        get => _attemptStatusFilePath;
        set => SetProperty(ref _attemptStatusFilePath, value);
    }

    public string AgentOutputFilePath
    {
        get => _agentOutputFilePath;
        set => SetProperty(ref _agentOutputFilePath, value);
    }

    public string VerificationLogPath
    {
        get => _verificationLogPath;
        set => SetProperty(ref _verificationLogPath, value);
    }

    public string LatestAttemptFolder
    {
        get => _latestAttemptFolder;
        set => SetProperty(ref _latestAttemptFolder, value);
    }

    public string FailedCommand
    {
        get => _failedCommand;
        set
        {
            if (SetProperty(ref _failedCommand, value))
            {
                OnPropertyChanged(nameof(FailureSummary));
            }
        }
    }

    public string LastFailureReason
    {
        get => _lastFailureReason;
        set
        {
            if (SetProperty(ref _lastFailureReason, value))
            {
                OnPropertyChanged(nameof(FailureSummary));
            }
        }
    }

    public string FailureSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FailedCommand) && string.IsNullOrWhiteSpace(LastFailureReason))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(FailedCommand))
            {
                return LastFailureReason;
            }

            if (string.IsNullOrWhiteSpace(LastFailureReason))
            {
                return FailedCommand;
            }

            return $"{FailedCommand} - {LastFailureReason}";
        }
    }
}
