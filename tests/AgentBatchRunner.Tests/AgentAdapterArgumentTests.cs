using AgentBatchRunner.Agents;

namespace AgentBatchRunner.Tests;

public sealed class AgentAdapterArgumentTests
{
    [Fact]
    public void Claude_FirstAttempt_DoesNotResume()
    {
        var args = ClaudeCodeAdapter.BuildArguments(new AgentExecutionRequest
        {
            Prompt = "Do the thing.",
            AttemptNumber = 1,
            SessionId = "abc-123"
        });

        Assert.DoesNotContain("--resume", args);
        Assert.Equal(["-p", "Do the thing.", "--output-format", "json"], args);
    }

    [Fact]
    public void Claude_Retry_WithSession_Resumes()
    {
        var args = ClaudeCodeAdapter.BuildArguments(new AgentExecutionRequest
        {
            Prompt = "Fix it.",
            AttemptNumber = 2,
            SessionId = "abc-123"
        });

        Assert.Equal("--resume", args[0]);
        Assert.Equal("abc-123", args[1]);
    }

    [Fact]
    public void Claude_Retry_WithoutSession_StartsFresh()
    {
        var args = ClaudeCodeAdapter.BuildArguments(new AgentExecutionRequest
        {
            Prompt = "Fix it.",
            AttemptNumber = 2,
            SessionId = null
        });

        Assert.DoesNotContain("--resume", args);
    }

    [Fact]
    public void Codex_FirstAttempt_DoesNotResume()
    {
        var request = new AgentExecutionRequest { Prompt = "Do the thing.", AttemptNumber = 1 };

        var args = CodexAdapter.BuildArguments(request);

        Assert.Equal(["exec", "Do the thing."], args);
        Assert.False(CodexAdapter.UsesLastSessionFallback(request));
    }

    [Fact]
    public void Codex_Retry_WithSession_ResumesExactSession()
    {
        var request = new AgentExecutionRequest { Prompt = "Fix it.", AttemptNumber = 2, SessionId = "conv-001" };

        var args = CodexAdapter.BuildArguments(request);

        Assert.Equal(["exec", "resume", "conv-001", "Fix it."], args);
        Assert.False(CodexAdapter.UsesLastSessionFallback(request));
    }

    [Fact]
    public void Codex_Retry_WithoutSession_FallsBackToLastAndIsFlagged()
    {
        var request = new AgentExecutionRequest { Prompt = "Fix it.", AttemptNumber = 2, SessionId = null };

        var args = CodexAdapter.BuildArguments(request);

        Assert.Equal(["exec", "resume", "--last", "Fix it."], args);
        Assert.True(CodexAdapter.UsesLastSessionFallback(request));
    }

    [Fact]
    public void Claude_AppliesConfiguredPermissionMode()
    {
        var args = ClaudeCodeAdapter.BuildArguments(new AgentExecutionRequest
        {
            Prompt = "Edit.",
            AttemptNumber = 1,
            Options = new AgentInvocationOptions { ClaudePermissionMode = "acceptEdits" }
        });

        Assert.Equal(["--permission-mode", "acceptEdits", "-p", "Edit.", "--output-format", "json"], args);
    }

    [Fact]
    public void Claude_SkipPermissions_OverridesPermissionMode()
    {
        var args = ClaudeCodeAdapter.BuildArguments(new AgentExecutionRequest
        {
            Prompt = "Edit.",
            AttemptNumber = 1,
            Options = new AgentInvocationOptions
            {
                ClaudePermissionMode = "acceptEdits",
                ClaudeDangerouslySkipPermissions = true
            }
        });

        Assert.Contains("--dangerously-skip-permissions", args);
        Assert.DoesNotContain("--permission-mode", args);
    }

    [Fact]
    public void Codex_InitialAttempt_AppliesSandbox()
    {
        var args = CodexAdapter.BuildArguments(new AgentExecutionRequest
        {
            Prompt = "Build.",
            AttemptNumber = 1,
            Options = new AgentInvocationOptions { CodexSandbox = "workspace-write" }
        });

        Assert.Equal(["exec", "--sandbox", "workspace-write", "Build."], args);
    }

    [Fact]
    public void Codex_FullAuto_OverridesSandbox()
    {
        var args = CodexAdapter.BuildArguments(new AgentExecutionRequest
        {
            Prompt = "Build.",
            AttemptNumber = 1,
            Options = new AgentInvocationOptions { CodexSandbox = "workspace-write", CodexFullAuto = true }
        });

        Assert.Contains("--full-auto", args);
        Assert.DoesNotContain("--sandbox", args);
    }

    [Fact]
    public void Codex_Retry_DoesNotReapplySandbox()
    {
        // The resumed session keeps its original sandbox, so retries must not pass --sandbox.
        var args = CodexAdapter.BuildArguments(new AgentExecutionRequest
        {
            Prompt = "Fix.",
            AttemptNumber = 2,
            SessionId = "conv-1",
            Options = new AgentInvocationOptions { CodexSandbox = "workspace-write" }
        });

        Assert.DoesNotContain("--sandbox", args);
        Assert.Equal(["exec", "resume", "conv-1", "Fix."], args);
    }
}
