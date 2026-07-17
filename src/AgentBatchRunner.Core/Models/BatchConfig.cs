namespace AgentBatchRunner.Models;

public sealed class BatchConfig
{
    public string Project { get; set; } = string.Empty;

    public string RepoPath { get; set; } = string.Empty;

    public string? DefaultAgent { get; set; }

    public string? CodexExecutablePath { get; set; }

    public string? ClaudeExecutablePath { get; set; }

    public string MinimumCodexVersion { get; set; } = "0.144.5";

    public string? RunAgentOverride { get; set; }

    public List<AgentToolchainInfo> ResolvedAgentToolchains { get; set; } = [];

    public int DefaultMaxRetries { get; set; } = 3;

    public int DefaultAgentTimeoutSeconds { get; set; } = 1800;

    public int DefaultVerifyTimeoutSeconds { get; set; } = 900;

    /// <summary>
    /// Legacy generic command timeout. Retained for compatibility with older normalized run
    /// configs; agent and verification commands now use the explicit timeout fields above.
    /// </summary>
    public int DefaultCommandTimeoutSeconds { get; set; }

    /// <summary>
    /// Claude <c>--permission-mode</c> value for unattended runs (e.g. <c>acceptEdits</c>,
    /// <c>plan</c>, <c>bypassPermissions</c>). Empty disables the flag. Ignored when
    /// <see cref="ClaudeDangerouslySkipPermissions"/> is true.
    /// </summary>
    public string ClaudePermissionMode { get; set; } = "acceptEdits";

    /// <summary>
    /// When true, Claude is invoked with <c>--dangerously-skip-permissions</c> instead of a
    /// permission mode. Off by default; opt in only for fully trusted, sandboxed environments.
    /// </summary>
    public bool ClaudeDangerouslySkipPermissions { get; set; }

    /// <summary>
    /// Codex <c>--sandbox</c> value for the initial invocation (e.g. <c>workspace-write</c>,
    /// <c>read-only</c>, <c>danger-full-access</c>). Empty disables the flag. Ignored when
    /// <see cref="CodexFullAuto"/> is true.
    /// </summary>
    public string CodexSandbox { get; set; } = "workspace-write";

    /// <summary>
    /// When true, Codex is invoked with <c>--full-auto</c> instead of an explicit sandbox flag.
    /// </summary>
    public bool CodexFullAuto { get; set; }

    public List<PromptTask> Prompts { get; set; } = [];
}
