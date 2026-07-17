using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;

namespace AgentBatchRunner.Tests;

public sealed class DryRunAgentAdapterTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsSuccessfulResultWithoutExternalProcess()
    {
        var adapter = new DryRunAgentAdapter(new ConsoleLogger());

        var result = await adapter.ExecuteAsync(
            new AgentExecutionRequest
            {
                PromptId = "P001",
                Prompt = "Do the thing.",
                AttemptNumber = 1,
                RepoPath = Environment.CurrentDirectory
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("dryrun", result.AgentName);
        Assert.Contains("Do the thing.", result.StandardOutput);
        Assert.Equal("dryrun-P001", result.SessionId);
    }
}
