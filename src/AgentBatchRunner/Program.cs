using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Services;
using System.Text;

namespace AgentBatchRunner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        var logger = new ConsoleLogger();
        var processRunner = new ProcessRunner();
        var stateStore = new RunStateStore();
        var rateLimitDetector = new AgentRateLimitDetector();
        var rateLimitStateStore = new AgentRateLimitStateStore();
        var effectiveAgentPolicy = new EffectiveAgentPolicy();
        var executableResolver = new AgentExecutableResolver();
        var preflightService = new AgentPreflightService(processRunner, executableResolver);
        var reportGenerator = new ReportGenerator(stateStore);
        var gitCheckpointManager = new GitCheckpointManager(processRunner, logger);
        var verificationRunner = new VerificationRunner(processRunner, logger);
        var agentFactory = new AgentAdapterFactory(processRunner, logger);
        var loader = new PromptFileLoader(effectiveAgentPolicy);
        var batchRunner = new BatchRunner(
            loader,
            gitCheckpointManager,
            verificationRunner,
            stateStore,
            reportGenerator,
            agentFactory,
            logger,
            rateLimitDetector: rateLimitDetector,
            rateLimitStateStore: rateLimitStateStore,
            effectiveAgentPolicy: effectiveAgentPolicy,
            agentPreflightService: preflightService);

        var app = new CommandLineApp(
            loader,
            batchRunner,
            stateStore,
            reportGenerator,
            rateLimitStateStore,
            logger,
            effectiveAgentPolicy: effectiveAgentPolicy);
        return await app.RunAsync(args, CancellationToken.None);
    }
}
