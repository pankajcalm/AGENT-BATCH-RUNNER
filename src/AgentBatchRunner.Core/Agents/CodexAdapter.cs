using AgentBatchRunner.Infrastructure;

namespace AgentBatchRunner.Agents;

public sealed class CodexAdapter(ProcessRunner processRunner, ConsoleLogger logger, string? executablePath = null) : IAgentAdapter
{
    public const string ExecutableName = "codex";

    public string Name => "codex";

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (UsesLastSessionFallback(request))
        {
            logger.Warning(
                "Codex retry requested but no session id was captured; resuming the most recent " +
                "Codex session via 'resume --last'. This may attach to an unrelated session if other " +
                "Codex runs happened concurrently.");
        }

        var arguments = BuildArguments(request);
        var processResult = await processRunner.RunExecutableAsync(
            request.ExecutablePath ?? executablePath ?? ExecutableName,
            arguments,
            request.RepoPath,
            cancellationToken,
            timeout: TimeoutFor(request));

        var sessionId = CodexSessionParser.TryExtractSessionId(processResult.StandardOutput);
        return new AgentExecutionResult
        {
            AgentName = Name,
            Command = processResult.Command,
            ExitCode = processResult.ExitCode,
            Duration = processResult.Duration,
            TimedOut = processResult.TimedOut,
            Timeout = processResult.Timeout,
            StandardOutput = processResult.StandardOutput,
            StandardError = processResult.StandardError,
            SessionId = sessionId ?? request.SessionId
        };
    }

    /// <summary>
    /// True when this request is a retry that has no captured session id and will therefore fall
    /// back to the non-deterministic <c>resume --last</c> behavior.
    /// </summary>
    public static bool UsesLastSessionFallback(AgentExecutionRequest request)
    {
        return request.ShouldResumeSession && string.IsNullOrWhiteSpace(request.SessionId);
    }

    /// <summary>
    /// Builds the <c>codex</c> CLI arguments for a request. On retries it resumes an exact session
    /// when one was captured, otherwise it falls back to <c>resume --last</c>. Pure and side-effect
    /// free so the resume behavior can be unit tested.
    /// </summary>
    public static List<string> BuildArguments(AgentExecutionRequest request)
    {
        var arguments = new List<string> { "exec" };

        if (request.ShouldResumeSession)
        {
            // Retries resume the original session, which already carries its sandbox/approval
            // settings, so the sandbox flags are only applied to the initial invocation below.
            arguments.Add("resume");
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                arguments.Add(request.SessionId);
            }
            else
            {
                arguments.Add("--last");
            }
        }
        else if (request.Options.CodexFullAuto)
        {
            arguments.Add("--full-auto");
        }
        else if (!string.IsNullOrWhiteSpace(request.Options.CodexSandbox))
        {
            arguments.Add("--sandbox");
            arguments.Add(request.Options.CodexSandbox);
        }

        arguments.Add(request.Prompt);
        return arguments;
    }

    private static TimeSpan? TimeoutFor(AgentExecutionRequest request)
    {
        return request.Options.TimeoutSeconds > 0
            ? TimeSpan.FromSeconds(request.Options.TimeoutSeconds)
            : null;
    }
}
