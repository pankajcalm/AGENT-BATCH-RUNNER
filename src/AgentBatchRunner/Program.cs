using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Services;

namespace AgentBatchRunner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var stateStore = new RunStateStore();
        var rateLimitDetector = new AgentRateLimitDetector();
        var rateLimitStateStore = new AgentRateLimitStateStore();
        var reportGenerator = new ReportGenerator(stateStore);
        var gitCheckpointManager = new GitCheckpointManager(processRunner, logger);
        var verificationRunner = new VerificationRunner(processRunner, logger);
        var agentFactory = new AgentAdapterFactory(processRunner, logger);
        var loader = new PromptFileLoader();
        var batchRunner = new BatchRunner(
            loader,
            gitCheckpointManager,
            verificationRunner,
            stateStore,
            reportGenerator,
            agentFactory,
            logger,
            rateLimitDetector: rateLimitDetector,
            rateLimitStateStore: rateLimitStateStore);

        var app = new CommandLineApp(loader, batchRunner, stateStore, reportGenerator, rateLimitStateStore, logger);
        return await app.RunAsync(args, CancellationToken.None);
    }
}
