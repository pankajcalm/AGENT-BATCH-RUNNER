using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class PipelineDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_FindsYamlAndYmlFilesRecursively()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, "20_second.yml", "SECOND", order: 20);
        await WritePipelineAsync(Path.Combine(workspace.Root, "nested"), "10_first.yaml", "FIRST", order: 10);

        var result = await CreateDiscovery().DiscoverAsync(workspace.Root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(2, result.Files.Count);
        Assert.Contains(result.Files, file => file.FileName == "10_first.yaml");
        Assert.Contains(result.Files, file => file.FileName == "20_second.yml");
    }

    [Fact]
    public async Task DiscoverAsync_ExcludesGeneratedReviewInternalAndUnderscoreFiles()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, "10_work.yaml", "WORK", order: 10);
        await WritePipelineAsync(workspace.Root, "10_work.review.yaml", "REVIEW", order: 11);
        await WritePipelineAsync(workspace.Root, "10_work.review-R1.yml", "REREVIEW", order: 12);
        await WritePipelineAsync(workspace.Root, "_template.yaml", "TEMPLATE", order: 13);
        await WritePipelineAsync(
            Path.Combine(workspace.Root, ".agentbatchrunner", "pipelines", "run"),
            "internal.yaml",
            "INTERNAL",
            order: 14);

        var result = await CreateDiscovery().DiscoverAsync(workspace.Root);

        var file = Assert.Single(result.Files);
        Assert.Equal("10_work.yaml", file.FileName);
    }

    [Fact]
    public async Task DiscoverAsync_ExcludesExplicitlyDisabledFile()
    {
        using var workspace = TestWorkspace.Create();
        await WritePipelineAsync(workspace.Root, "10_enabled.yaml", "ENABLED", order: 10);
        await WritePipelineAsync(workspace.Root, "20_disabled.yaml", "DISABLED", order: 20, enabled: false);

        var result = await CreateDiscovery().DiscoverAsync(workspace.Root);

        var file = Assert.Single(result.Files);
        Assert.Equal("ENABLED", file.PipelineId);
    }

    [Fact]
    public async Task DiscoverAsync_LeavesLegacyYamlValid()
    {
        using var workspace = TestWorkspace.Create();
        await Utf8File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "legacy.yaml"),
            ValidBatchYaml(workspace.Root));

        var result = await CreateDiscovery().DiscoverAsync(workspace.Root);

        var file = Assert.Single(result.Files);
        Assert.True(file.IsLegacy);
        Assert.Equal("legacy.yaml", file.PipelineId);
    }

    internal static async Task WritePipelineAsync(
        string folder,
        string fileName,
        string pipelineId,
        int? order = null,
        bool enabled = true,
        IReadOnlyList<string>? dependsOn = null,
        string? nextApproved = null)
    {
        Directory.CreateDirectory(folder);
        var dependencyYaml = dependsOn is { Count: > 0 }
            ? Environment.NewLine + string.Join(Environment.NewLine, dependsOn.Select(item => $"    - {item}"))
            : " []";
        var nextYaml = string.IsNullOrWhiteSpace(nextApproved)
            ? string.Empty
            : $"{Environment.NewLine}  next:{Environment.NewLine}    onApproved: {nextApproved}";
        var yaml =
            $"pipeline:{Environment.NewLine}" +
            $"  id: {pipelineId}{Environment.NewLine}" +
            $"  title: {pipelineId} title{Environment.NewLine}" +
            $"  phase: TEST{Environment.NewLine}" +
            (order.HasValue ? $"  order: {order.Value}{Environment.NewLine}" : string.Empty) +
            $"  enabled: {enabled.ToString().ToLowerInvariant()}{Environment.NewLine}" +
            $"  dependsOn:{dependencyYaml}{nextYaml}{Environment.NewLine}" +
            ValidBatchYaml(folder);
        await Utf8File.WriteAllTextAsync(Path.Combine(folder, fileName), yaml);
    }

    internal static string ValidBatchYaml(string repoPath)
    {
        return
            $"project: Pipeline test{Environment.NewLine}" +
            $"repoPath: '{repoPath}'{Environment.NewLine}" +
            $"defaultAgent: dryrun{Environment.NewLine}" +
            $"prompts:{Environment.NewLine}" +
            $"  - id: P001{Environment.NewLine}" +
            $"    title: Test task{Environment.NewLine}" +
            $"    prompt: Do the safe test task.{Environment.NewLine}" +
            $"    verify: []{Environment.NewLine}";
    }

    internal static PipelineFolderDiscovery CreateDiscovery()
    {
        return new PipelineFolderDiscovery(new PromptFileLoader());
    }
}
