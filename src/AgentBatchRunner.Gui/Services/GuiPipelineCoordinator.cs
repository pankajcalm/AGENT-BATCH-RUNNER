using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Gui.Services;

public interface IGuiPipelineCoordinator
{
    event EventHandler<PipelineEvent>? PipelineEventReceived;

    event EventHandler<RunEvent>? RunEventReceived;

    Task<PipelinePlan> PlanAsync(string folderPath, CancellationToken cancellationToken);

    Task<PipelineRunState> CreateAsync(
        string folderPath,
        PipelineRunOptions options,
        CancellationToken cancellationToken);

    Task<PipelineRunState> RunSelectedAsync(
        PipelineRunState state,
        string fileReference,
        PipelineRunControl control,
        CancellationToken cancellationToken);

    Task<PipelineRunState> RunPipelineAsync(
        PipelineRunState state,
        string? initialFileReference,
        PipelineRunControl control,
        CancellationToken cancellationToken);

    Task<PipelineRunState> RunRecommendedNextAsync(
        PipelineRunState state,
        bool userConfirmed,
        PipelineRunControl control,
        CancellationToken cancellationToken);

    Task<PipelineRunState> SkipAsync(
        PipelineRunState state,
        string fileReference,
        PipelineManualActionRequest request,
        CancellationToken cancellationToken);

    Task<PipelineRunState> CompleteManuallyAsync(
        PipelineRunState state,
        string fileReference,
        PipelineManualActionRequest request,
        CancellationToken cancellationToken);

    PipelineStartFromPlan PlanStartFrom(PipelineRunState state, string fileReference);

    Task<PipelineRunState> StartFromSelectedAsync(
        PipelineRunState state,
        string fileReference,
        PipelineStartFromRequest request,
        PipelineRunControl control,
        CancellationToken cancellationToken);

    Task<PipelineRunState> UndoManualStatusAsync(
        PipelineRunState state,
        string fileReference,
        string actor,
        string overrideSource,
        CancellationToken cancellationToken);

    Task<PipelineRunState> ResumeAsync(string pipelineRunDirectory, CancellationToken cancellationToken);

    string? FindLatestPipelineDirectory(string repoPath);
}

public sealed class GuiPipelineCoordinator : IGuiPipelineCoordinator
{
    private readonly PipelineFolderRunner _runner;
    private readonly PipelineStateStore _stateStore;

    public GuiPipelineCoordinator(
        System.Windows.Threading.Dispatcher dispatcher,
        AgentRateLimitStateStore? rateLimitStateStore = null)
    {
        var limits = rateLimitStateStore ?? new AgentRateLimitStateStore();
        var runEvents = new GuiLogger(dispatcher);
        var pipelineEvents = new GuiPipelineEventSink(dispatcher);
        runEvents.RunEventReceived += (_, runEvent) => RunEventReceived?.Invoke(this, runEvent);
        pipelineEvents.PipelineEventReceived += (_, pipelineEvent) =>
            PipelineEventReceived?.Invoke(this, pipelineEvent);

        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var runStateStore = new RunStateStore();
        var effectiveAgentPolicy = new EffectiveAgentPolicy();
        var fallbackPolicy = new RateLimitFallbackPolicy();
        var loader = new PromptFileLoader(effectiveAgentPolicy, fallbackPolicy);
        var preflight = new AgentPreflightService(processRunner, new AgentExecutableResolver());
        var verification = new VerificationRunner(processRunner, logger, runEvents);
        var agentFactory = new AgentAdapterFactory(processRunner, logger);
        var batchRunner = new BatchRunner(
            loader,
            new GitCheckpointManager(processRunner, logger),
            verification,
            runStateStore,
            new ReportGenerator(runStateStore),
            agentFactory,
            logger,
            runEvents,
            new AgentRateLimitDetector(),
            limits,
            effectiveAgentPolicy,
            preflight,
            fallbackPolicy: fallbackPolicy);
        var reviewRunner = new PipelineReviewRunner(
            loader,
            agentFactory,
            preflight,
            verification,
            processRunner,
            runStateStore,
            new PipelineReviewResultParser(),
            new PipelineReviewReportGenerator(runStateStore),
            new AgentRateLimitDetector(),
            limits);
        _stateStore = new PipelineStateStore();
        _runner = new PipelineFolderRunner(
            new PipelineFolderDiscovery(loader),
            new PipelinePlanBuilder(),
            loader,
            batchRunner,
            runStateStore,
            new PipelineReviewYamlGenerator(),
            reviewRunner,
            new NextPipelineFileSelector(),
            _stateStore,
            new PipelineReportGenerator(_stateStore),
            effectiveAgentPolicy,
            limits,
            pipelineEvents);
    }

    public event EventHandler<PipelineEvent>? PipelineEventReceived;

    public event EventHandler<RunEvent>? RunEventReceived;

    public Task<PipelinePlan> PlanAsync(string folderPath, CancellationToken cancellationToken)
    {
        return _runner.PlanAsync(folderPath, cancellationToken);
    }

    public Task<PipelineRunState> CreateAsync(
        string folderPath,
        PipelineRunOptions options,
        CancellationToken cancellationToken)
    {
        return _runner.CreateAsync(folderPath, options, cancellationToken);
    }

    public Task<PipelineRunState> RunSelectedAsync(
        PipelineRunState state,
        string fileReference,
        PipelineRunControl control,
        CancellationToken cancellationToken)
    {
        return _runner.RunSelectedAsync(state, fileReference, control, cancellationToken);
    }

    public Task<PipelineRunState> RunPipelineAsync(
        PipelineRunState state,
        string? initialFileReference,
        PipelineRunControl control,
        CancellationToken cancellationToken)
    {
        return _runner.RunPipelineAsync(state, initialFileReference, control, cancellationToken);
    }

    public Task<PipelineRunState> RunRecommendedNextAsync(
        PipelineRunState state,
        bool userConfirmed,
        PipelineRunControl control,
        CancellationToken cancellationToken)
    {
        return _runner.RunRecommendedNextAsync(state, userConfirmed, control, cancellationToken);
    }

    public Task<PipelineRunState> SkipAsync(
        PipelineRunState state,
        string fileReference,
        PipelineManualActionRequest request,
        CancellationToken cancellationToken)
    {
        return _runner.SkipAsync(state, fileReference, request, cancellationToken);
    }

    public Task<PipelineRunState> CompleteManuallyAsync(
        PipelineRunState state,
        string fileReference,
        PipelineManualActionRequest request,
        CancellationToken cancellationToken)
    {
        return _runner.CompleteManuallyAsync(state, fileReference, request, cancellationToken);
    }

    public PipelineStartFromPlan PlanStartFrom(PipelineRunState state, string fileReference)
    {
        return _runner.PlanStartFrom(state, fileReference);
    }

    public Task<PipelineRunState> StartFromSelectedAsync(
        PipelineRunState state,
        string fileReference,
        PipelineStartFromRequest request,
        PipelineRunControl control,
        CancellationToken cancellationToken)
    {
        return _runner.StartFromSelectedAsync(state, fileReference, request, control, cancellationToken);
    }

    public Task<PipelineRunState> UndoManualStatusAsync(
        PipelineRunState state,
        string fileReference,
        string actor,
        string overrideSource,
        CancellationToken cancellationToken)
    {
        return _runner.UndoManualStatusAsync(
            state,
            fileReference,
            actor,
            overrideSource,
            cancellationToken);
    }

    public Task<PipelineRunState> ResumeAsync(
        string pipelineRunDirectory,
        CancellationToken cancellationToken)
    {
        return _runner.ResumeAsync(pipelineRunDirectory, cancellationToken);
    }

    public string? FindLatestPipelineDirectory(string repoPath)
    {
        return _stateStore.FindLatestPipelineDirectory(repoPath);
    }
}
