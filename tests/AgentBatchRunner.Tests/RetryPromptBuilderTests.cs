using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class RetryPromptBuilderTests
{
    [Fact]
    public void Build_IncludesOriginalPromptCommandExitCodeAndOutput()
    {
        var result = RetryPromptBuilder.Build(
            originalPrompt: "Make the build pass.",
            failedCommand: "dotnet build",
            exitCode: 1,
            output: "error CS1002: ; expected");

        Assert.Contains("Make the build pass.", result);
        Assert.Contains("dotnet build", result);
        Assert.Contains("1", result);
        Assert.Contains("error CS1002", result);
        Assert.Contains("Please fix the cause of the failure.", result);
    }

    [Fact]
    public void Build_TruncatesLargeOutputToLast24000Characters()
    {
        var hugeOutput = new string('x', 50_000) + "TAIL_MARKER";

        var result = RetryPromptBuilder.Build("Task", "cmd", 2, hugeOutput);

        Assert.Contains("TAIL_MARKER", result); // tail is preserved
        Assert.Contains("Output truncated to the last 24000 characters.", result);
        // The retry prompt must stay close to the cap, not echo the whole 50k output.
        Assert.True(result.Length < 25_000, $"Expected truncated prompt, got {result.Length} chars.");
    }

    [Fact]
    public void Build_RedactsSecretsInOutput()
    {
        var result = RetryPromptBuilder.Build("Task", "cmd", 1, "password=hunter2 failed to connect");

        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Build_IncludesTimeoutDetailsAndPartialOutput()
    {
        var result = RetryPromptBuilder.Build(
            "Task",
            "dotnet test",
            124,
            "partial stdout and stderr",
            timedOut: true,
            timeout: TimeSpan.FromSeconds(3));

        Assert.Contains("dotnet test", result);
        Assert.Contains("124", result);
        Assert.Contains("timed out after 3s", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partial stdout and stderr", result);
        Assert.Contains("partial stdout/stderr", result);
    }
}
