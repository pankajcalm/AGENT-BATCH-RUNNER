using AgentBatchRunner.Infrastructure;

namespace AgentBatchRunner.Services;

public static class RetryPromptBuilder
{
    private const int MaxOutputCharacters = 24000;

    public static string Build(
        string originalPrompt,
        string failedCommand,
        int exitCode,
        string output,
        bool timedOut = false,
        TimeSpan? timeout = null)
    {
        var redactedOutput = SensitiveDataRedactor.Redact(output);
        if (redactedOutput.Length > MaxOutputCharacters)
        {
            redactedOutput = redactedOutput[^MaxOutputCharacters..] + Environment.NewLine + "[Output truncated to the last 24000 characters.]";
        }

        return
            $"""
            The previous attempt failed verification.

            Original task:
            {originalPrompt}

            Failed verification command:
            {failedCommand}

            Exit code:
            {exitCode}

            {(timedOut ? BuildTimeoutSection(timeout) : string.Empty)}

            Output:
            {redactedOutput}

            Please fix the cause of the failure.
            Do not make unrelated changes.
            Do not restart the task from scratch if the current work is already partially correct.
            """;
    }

    public static string BuildRateLimitFallback(
        string originalPrompt,
        string previousAgent,
        string previousOutput)
    {
        var redactedOutput = Truncate(SensitiveDataRedactor.Redact(previousOutput));
        return
            $"""
            The previous agent became rate-limited while working on this task.

            Original task:
            {originalPrompt}

            Previous agent:
            {previousAgent}

            Previous output:
            {redactedOutput}

            The working tree may contain partially completed changes.
            Inspect the current state and continue the task without discarding correct work.
            Do not make unrelated changes.
            """;
    }

    private static string Truncate(string output)
    {
        return output.Length <= MaxOutputCharacters
            ? output
            : output[^MaxOutputCharacters..] + Environment.NewLine +
              "[Output truncated to the last 24000 characters.]";
    }

    private static string BuildTimeoutSection(TimeSpan? timeout)
    {
        var duration = timeout.HasValue
            ? $"{timeout.Value.TotalSeconds:0}s"
            : "the configured timeout";

        return
            $"""
            Failure type:
            The command timed out after {duration}. The output below is partial stdout/stderr captured before termination.
            """;
    }
}
