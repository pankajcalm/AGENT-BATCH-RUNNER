using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class AgentRateLimitTests
{
    [Fact]
    public void Detect_FindsUsageLimitText()
    {
        var detector = new AgentRateLimitDetector();

        var info = detector.Detect(
            "claude",
            "Usage limit reached. Please try again later.",
            string.Empty,
            DateTimeOffset.Parse("2026-06-26T12:00:00Z"));

        Assert.NotNull(info);
        Assert.True(info!.IsBlocked);
        Assert.Equal("claude", info.AgentName);
        Assert.Contains("usage limit reached", info.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Usage limit reached", info.RawMessage);
    }

    [Fact]
    public void Detect_ParsesIsoResetTimestamp()
    {
        var detector = new AgentRateLimitDetector();

        var info = detector.Detect(
            "codex",
            "Rate limit exceeded. Limit resets at 2026-06-26T20:30:00Z.",
            string.Empty,
            DateTimeOffset.Parse("2026-06-26T12:00:00Z"));

        Assert.NotNull(info);
        Assert.Equal(DateTimeOffset.Parse("2026-06-26T20:30:00Z"), info!.BlockedUntil);
    }

    [Fact]
    public void Detect_ParsesRetryAfterSeconds()
    {
        var detector = new AgentRateLimitDetector();
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");

        var info = detector.Detect(
            "codex",
            string.Empty,
            "Too many requests. Retry-After: 120",
            now);

        Assert.NotNull(info);
        Assert.Equal(now.AddSeconds(120), info!.BlockedUntil);
    }

    [Fact]
    public void Detect_FindsOutOfCodexMessages()
    {
        var detector = new AgentRateLimitDetector();

        var info = detector.Detect(
            "codex",
            "You're out of Codex messages.",
            string.Empty,
            DateTimeOffset.Parse("2026-06-26T08:00:00-04:00"));

        Assert.NotNull(info);
        Assert.True(info!.IsBlocked);
        Assert.Equal("codex", info.AgentName);
    }

    [Fact]
    public void Detect_ParsesRateLimitResetsOnClockTime()
    {
        var detector = new AgentRateLimitDetector();
        var now = DateTimeOffset.Parse("2026-06-26T08:00:00-04:00");

        var info = detector.Detect("codex", "Your rate limit resets on 1:27 PM", string.Empty, now);

        Assert.NotNull(info);
        Assert.True(info!.IsBlocked);
        Assert.True(info.BlockedUntil.HasValue);
        Assert.Equal(13, info.BlockedUntil!.Value.Hour);
        Assert.Equal(27, info.BlockedUntil.Value.Minute);
        Assert.True(info.BlockedUntil.Value > now);
    }

    [Fact]
    public void Detect_FindsSessionLimitWithClockTimeAndTimezone()
    {
        var detector = new AgentRateLimitDetector();
        var now = DateTimeOffset.Parse("2026-06-26T08:00:00-04:00");

        var info = detector.Detect(
            "claude",
            "You've hit your session limit · resets 1pm (America/New_York)",
            string.Empty,
            now);

        Assert.NotNull(info);
        Assert.True(info!.IsBlocked);
        Assert.True(info.BlockedUntil.HasValue);
        Assert.Equal(13, info.BlockedUntil!.Value.Hour);
        Assert.Equal(0, info.BlockedUntil.Value.Minute);
    }

    [Fact]
    public void Store_SaveAndLoad_RoundTripsBlockedState()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.Root, "agent-rate-limits.json");
        var blockedUntil = DateTimeOffset.Parse("2026-06-26T20:30:00Z");
        var store = new AgentRateLimitStateStore(path, () => DateTimeOffset.Parse("2026-06-26T12:00:00Z"));

        store.SetBlocked(new AgentRateLimitInfo
        {
            AgentName = "Claude",
            IsBlocked = true,
            BlockedUntil = blockedUntil,
            LastDetectedAt = DateTimeOffset.Parse("2026-06-26T12:00:00Z"),
            Reason = "Usage limit reached.",
            RawMessage = "Usage limit reached."
        });

        var loaded = new AgentRateLimitStateStore(path, () => DateTimeOffset.Parse("2026-06-26T12:01:00Z")).Get("claude");

        Assert.True(loaded.IsBlocked);
        Assert.Equal("claude", loaded.AgentName);
        Assert.Equal(blockedUntil, loaded.BlockedUntil);
        Assert.Equal("Usage limit reached.", loaded.Reason);
    }

    [Fact]
    public void Store_Get_AutoClearsExpiredBlock()
    {
        using var workspace = TestWorkspace.Create();
        var path = Path.Combine(workspace.Root, "agent-rate-limits.json");
        var store = new AgentRateLimitStateStore(path, () => DateTimeOffset.Parse("2026-06-26T12:00:00Z"));
        store.SetBlocked(new AgentRateLimitInfo
        {
            AgentName = "codex",
            IsBlocked = true,
            BlockedUntil = DateTimeOffset.Parse("2026-06-26T12:00:01Z"),
            LastDetectedAt = DateTimeOffset.Parse("2026-06-26T11:59:00Z"),
            Reason = "Too many requests.",
            RawMessage = "Too many requests."
        });

        var laterStore = new AgentRateLimitStateStore(path, () => DateTimeOffset.Parse("2026-06-26T12:05:00Z"));
        var info = laterStore.Get("codex");

        Assert.False(info.IsBlocked);
        Assert.Null(info.BlockedUntil);
        Assert.False(laterStore.TryGetBlocked("codex", out _));
    }
}
