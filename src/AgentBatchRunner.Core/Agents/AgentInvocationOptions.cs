namespace AgentBatchRunner.Agents;

/// <summary>
/// Per-invocation knobs that control how an agent CLI is launched. Defaults are intentionally
/// neutral (no extra flags, default timeout); <see cref="Services.BatchRunner"/> populates them from the
/// batch configuration so the policy defaults live in one place.
/// </summary>
public sealed class AgentInvocationOptions
{
    /// <summary>Agent CLI timeout in seconds. 0 means no timeout.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Claude <c>--permission-mode</c> value; null/empty omits the flag.</summary>
    public string? ClaudePermissionMode { get; set; }

    /// <summary>When true, pass Claude <c>--dangerously-skip-permissions</c> (overrides the mode).</summary>
    public bool ClaudeDangerouslySkipPermissions { get; set; }

    /// <summary>Codex <c>--sandbox</c> value for the initial invocation; null/empty omits the flag.</summary>
    public string? CodexSandbox { get; set; }

    /// <summary>When true, pass Codex <c>--full-auto</c> (overrides the sandbox flag).</summary>
    public bool CodexFullAuto { get; set; }
}
