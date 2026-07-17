using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class RunStateStoreTests
{
    [Fact]
    public void FindLatestRunDirectory_ReturnsMostRecentByTimestampName()
    {
        using var temp = TestWorkspace.Create();
        CreateRun(temp.Root, "20260625-100000");
        CreateRun(temp.Root, "20260626-090000");
        CreateRun(temp.Root, "20260626-083000");
        var store = new RunStateStore();

        var latest = store.FindLatestRunDirectory(temp.Root);

        Assert.NotNull(latest);
        Assert.Equal("20260626-090000", Path.GetFileName(latest));
    }

    [Fact]
    public void FindLatestRunDirectory_WalksUpFromSubdirectory()
    {
        using var temp = TestWorkspace.Create();
        CreateRun(temp.Root, "20260626-090000");
        var nested = Path.Combine(temp.Root, "src", "deep");
        Directory.CreateDirectory(nested);
        var store = new RunStateStore();

        var latest = store.FindLatestRunDirectory(nested);

        Assert.NotNull(latest);
        Assert.Equal("20260626-090000", Path.GetFileName(latest));
    }

    [Fact]
    public void FindRunDirectory_ReturnsExactRun()
    {
        using var temp = TestWorkspace.Create();
        CreateRun(temp.Root, "20260625-100000");
        CreateRun(temp.Root, "20260626-090000");
        var store = new RunStateStore();

        var match = store.FindRunDirectory(temp.Root, "20260625-100000");

        Assert.NotNull(match);
        Assert.Equal("20260625-100000", Path.GetFileName(match));
    }

    [Fact]
    public void FindRunDirectory_ReturnsNullForUnknownId()
    {
        using var temp = TestWorkspace.Create();
        CreateRun(temp.Root, "20260626-090000");
        var store = new RunStateStore();

        Assert.Null(store.FindRunDirectory(temp.Root, "does-not-exist"));
    }

    [Fact]
    public void FindLatestRunDirectory_ReturnsNullWhenNoRuns()
    {
        using var temp = TestWorkspace.Create();
        var store = new RunStateStore();

        Assert.Null(store.FindLatestRunDirectory(temp.Root));
    }

    private static void CreateRun(string root, string runId)
    {
        Directory.CreateDirectory(Path.Combine(root, ".agentbatchrunner", "runs", runId));
    }
}
