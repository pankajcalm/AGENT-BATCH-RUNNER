using AgentBatchRunner.Infrastructure;

namespace AgentBatchRunner.Agents;

public sealed class ClaudeCodeAdapter(ProcessRunner processRunner, ConsoleLogger logger) : IAgentAdapter
{
    public const string ExecutableName = "claude";

    public string Name => "claude";

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AttemptNumber > 1 && string.IsNullOrWhiteSpace(request.SessionId))
        {
            logger.Warning("Claude retry requested but no session_id was captured; starting a fresh Claude session.");
        }

        var arguments = BuildArguments(request);
        var processResult = await processRunner.RunExecutableAsync(
            request.ExecutablePath ?? ExecutableName,
            arguments,
            request.RepoPath,
            cancellationToken,
            timeout: TimeoutFor(request));

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
            SessionId = ClaudeSessionParser.TryExtractSessionId(processResult.StandardOutput)
        };
    }

    /// <summary>
    /// Builds the <c>claude</c> CLI arguments for a request. A captured session id is only
    /// resumed on retries (attempt &gt; 1); otherwise a fresh session is started. Pure and
    /// side-effect free so the resume behavior can be unit tested.
    /// </summary>
    public static List<string> BuildArguments(AgentExecutionRequest request)
    {
        var arguments = new List<string>();
        if (request.AttemptNumber > 1 && !string.IsNullOrWhiteSpace(request.SessionId))
        {
            arguments.Add("--resume");
            arguments.Add(request.SessionId);
        }

        // Unattended permission handling: skip-permissions takes precedence over an explicit mode.
        if (request.Options.ClaudeDangerouslySkipPermissions)
        {
            arguments.Add("--dangerously-skip-permissions");
        }
        else if (!string.IsNullOrWhiteSpace(request.Options.ClaudePermissionMode))
        {
            arguments.Add("--permission-mode");
            arguments.Add(request.Options.ClaudePermissionMode);
        }

        arguments.Add("-p");
        arguments.Add(request.Prompt);
        arguments.Add("--output-format");
        arguments.Add("json");
        return arguments;
    }

    private static TimeSpan? TimeoutFor(AgentExecutionRequest request)
    {
        return request.Options.TimeoutSeconds > 0
            ? TimeSpan.FromSeconds(request.Options.TimeoutSeconds)
            : null;
    }
}
