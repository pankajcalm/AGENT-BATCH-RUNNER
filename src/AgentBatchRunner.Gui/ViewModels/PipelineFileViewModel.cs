using AgentBatchRunner.Models;

namespace AgentBatchRunner.Gui.ViewModels;

public sealed class PipelineFileViewModel : ViewModelBase
{
    private PipelineFileStatus _status;
    private string _executionResult = string.Empty;
    private string _reviewVerdict = string.Empty;
    private string _recommendedNextFile = string.Empty;
    private string _lastMessage = string.Empty;
    private string _durationText = string.Empty;

    public int Order { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string FilePath { get; private set; } = string.Empty;

    public string PipelineId { get; private set; } = string.Empty;

    public string Phase { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string ExecutionAgent { get; private set; } = string.Empty;

    public string ReviewAgent { get; private set; } = string.Empty;

    public string Dependencies { get; private set; } = string.Empty;

    public string Gate { get; private set; } = string.Empty;

    public PipelineFileStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ExecutionResult
    {
        get => _executionResult;
        private set => SetProperty(ref _executionResult, value);
    }

    public string ReviewVerdict
    {
        get => _reviewVerdict;
        private set => SetProperty(ref _reviewVerdict, value);
    }

    public string RecommendedNextFile
    {
        get => _recommendedNextFile;
        private set => SetProperty(ref _recommendedNextFile, value);
    }

    public string LastMessage
    {
        get => _lastMessage;
        private set => SetProperty(ref _lastMessage, value);
    }

    public string DurationText
    {
        get => _durationText;
        private set => SetProperty(ref _durationText, value);
    }

    public string ExecutionRunId { get; private set; } = string.Empty;

    public string ExecutionReportPath { get; private set; } = string.Empty;

    public string GitDiffPath { get; private set; } = string.Empty;

    public string ReviewYamlPath { get; private set; } = string.Empty;

    public string ReviewRunId { get; private set; } = string.Empty;

    public string ReviewReportPath { get; private set; } = string.Empty;

    public string ReviewResultPath { get; private set; } = string.Empty;

    public string FindingsText { get; private set; } = string.Empty;

    public string RequiredDecisionsText { get; private set; } = string.Empty;

    public string ManualReason { get; private set; } = string.Empty;

    public string ManualTimestampText { get; private set; } = string.Empty;

    public string ManualEvidencePath { get; private set; } = string.Empty;

    public string DependencySatisfactionText { get; private set; } = string.Empty;

    public string GateOverrideText { get; private set; } = string.Empty;

    public string MissingPrerequisitesText { get; private set; } = string.Empty;

    public string ManualStatusIndicator { get; private set; } = string.Empty;

    public void UpdateFrom(PipelineFileRunState state)
    {
        Order = state.QueueOrder;
        FileName = state.FileName;
        FilePath = state.FilePath;
        PipelineId = state.PipelineId;
        Phase = state.Phase;
        Title = state.Title;
        ExecutionAgent = state.ExecutionAgent;
        ReviewAgent = state.ReviewAgent;
        Dependencies = string.Join(", ", state.DependencyIds);
        Gate = state.Gate?.Id ?? string.Empty;
        Status = state.Status;
        ExecutionResult = state.ExecutionStatus?.ToString() ?? "Pending";
        ReviewVerdict = state.ReviewVerdict?.ToString() ?? "Pending";
        RecommendedNextFile = state.RecommendedNextFile ?? string.Empty;
        LastMessage = state.LastMessage;
        DurationText = state.Duration.HasValue ? $"{state.Duration.Value.TotalSeconds:0}s" : string.Empty;
        ExecutionRunId = state.ExecutionRunId ?? string.Empty;
        ExecutionReportPath = state.ExecutionReportPath ?? string.Empty;
        GitDiffPath = state.GitDiffPath ?? string.Empty;
        ReviewYamlPath = state.ReviewYamlPath ?? string.Empty;
        ReviewRunId = state.ReviewRunId ?? string.Empty;
        ReviewReportPath = state.ReviewReportPath ?? string.Empty;
        ReviewResultPath = state.ReviewResultPath ?? string.Empty;
        FindingsText = state.Findings.Count == 0
            ? "No findings recorded."
            : string.Join(
                Environment.NewLine,
                state.Findings.Select(finding =>
                    $"[{finding.Severity}] {finding.Id}: {finding.Title}" +
                    (string.IsNullOrWhiteSpace(finding.Detail) ? string.Empty : Environment.NewLine + finding.Detail)));
        RequiredDecisionsText = state.RequiredDecisions.Count == 0
            ? "No human decisions recorded."
            : string.Join(Environment.NewLine, state.RequiredDecisions.Select(decision => $"- {decision}"));
        ManualReason = state.ManualReason ?? string.Empty;
        ManualTimestampText = state.ManualTimestamp?.ToLocalTime().ToString("g") ?? string.Empty;
        ManualEvidencePath = state.ManualEvidencePath ?? string.Empty;
        DependencySatisfactionText = state.Status is PipelineFileStatus.SkippedByUser or PipelineFileStatus.ManuallyCompleted
            ? state.ManualSatisfiesDependencies ? "Accepted" : "Not satisfied"
            : string.Empty;
        GateOverrideText = state.Status is PipelineFileStatus.SkippedByUser or PipelineFileStatus.ManuallyCompleted
            ? state.ManualGateApproved ? "Approved manually" : "Not approved"
            : string.Empty;
        MissingPrerequisitesText = string.Join(
            ", ",
            state.MissingDependencyIds
                .Concat(state.MissingGatePrerequisiteIds)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        ManualStatusIndicator = state.Status switch
        {
            PipelineFileStatus.SkippedByUser => "Skipped - dependency not satisfied",
            PipelineFileStatus.ManuallyCompleted when state.Gate is not null && !state.ManualGateApproved =>
                "Manually completed - gate not approved",
            PipelineFileStatus.ManuallyCompleted when state.ManualSatisfiesDependencies =>
                "Manually completed - dependency accepted",
            PipelineFileStatus.ManuallyCompleted => "Manually completed - dependency not satisfied",
            _ => string.Empty
        };

        OnPropertyChanged(nameof(Order));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(PipelineId));
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ExecutionAgent));
        OnPropertyChanged(nameof(ReviewAgent));
        OnPropertyChanged(nameof(Dependencies));
        OnPropertyChanged(nameof(Gate));
        OnPropertyChanged(nameof(ExecutionRunId));
        OnPropertyChanged(nameof(ExecutionReportPath));
        OnPropertyChanged(nameof(GitDiffPath));
        OnPropertyChanged(nameof(ReviewYamlPath));
        OnPropertyChanged(nameof(ReviewRunId));
        OnPropertyChanged(nameof(ReviewReportPath));
        OnPropertyChanged(nameof(ReviewResultPath));
        OnPropertyChanged(nameof(FindingsText));
        OnPropertyChanged(nameof(RequiredDecisionsText));
        OnPropertyChanged(nameof(ManualReason));
        OnPropertyChanged(nameof(ManualTimestampText));
        OnPropertyChanged(nameof(ManualEvidencePath));
        OnPropertyChanged(nameof(DependencySatisfactionText));
        OnPropertyChanged(nameof(GateOverrideText));
        OnPropertyChanged(nameof(MissingPrerequisitesText));
        OnPropertyChanged(nameof(ManualStatusIndicator));
    }

    public static PipelineFileViewModel FromPlan(PipelineFile file, int queueOrder)
    {
        var executionAgents = file.Config.Prompts
            .Select(prompt => prompt.Agent ?? file.Config.DefaultAgent ?? "(not configured)")
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var viewModel = new PipelineFileViewModel
        {
            Order = queueOrder,
            FileName = file.FileName,
            FilePath = file.FilePath,
            PipelineId = file.PipelineId,
            Phase = file.Phase,
            Title = file.Title,
            ExecutionAgent = string.Join(", ", executionAgents),
            ReviewAgent = file.Metadata?.Review.Agent ??
                          (file.Metadata?.Review.Required == true ? "From YAML" : "Optional"),
            Dependencies = string.Join(", ", file.Dependencies),
            Gate = file.Metadata?.Gate?.Id ?? string.Empty,
            Status = PipelineFileStatus.Pending,
            ExecutionResult = "Pending",
            ReviewVerdict = "Pending",
            LastMessage = file.IsLegacy
                ? "Legacy YAML; manual next-file selection applies."
                : "Planned."
        };
        return viewModel;
    }
}
