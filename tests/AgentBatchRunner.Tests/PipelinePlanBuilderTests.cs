using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class PipelinePlanBuilderTests
{
    [Fact]
    public async Task Build_UsesFilenameOrderingForLegacyFiles()
    {
        using var workspace = TestWorkspace.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "20_second.yaml"),
            PipelineDiscoveryTests.ValidBatchYaml(workspace.Root));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "10_first.yaml"),
            PipelineDiscoveryTests.ValidBatchYaml(workspace.Root));

        var plan = await DiscoverAndPlanAsync(workspace.Root);

        Assert.True(plan.IsValid, string.Join(Environment.NewLine, plan.Errors));
        Assert.Equal(["10_first.yaml", "20_second.yaml"], plan.Files.Select(file => file.FileName));
    }

    [Fact]
    public async Task Build_UsesExplicitOrderAndHonorsDependencies()
    {
        using var workspace = TestWorkspace.Create();
        await PipelineDiscoveryTests.WritePipelineAsync(workspace.Root, "z_dependency.yaml", "DEPENDENCY", order: 20);
        await PipelineDiscoveryTests.WritePipelineAsync(
            workspace.Root,
            "a_dependent.yaml",
            "DEPENDENT",
            order: 10,
            dependsOn: ["DEPENDENCY"]);

        var plan = await DiscoverAndPlanAsync(workspace.Root);

        Assert.True(plan.IsValid, string.Join(Environment.NewLine, plan.Errors));
        Assert.Equal(["DEPENDENCY", "DEPENDENT"], plan.Files.Select(file => file.PipelineId));
    }

    [Fact]
    public async Task Build_RejectsDuplicatePipelineIds()
    {
        using var workspace = TestWorkspace.Create();
        await PipelineDiscoveryTests.WritePipelineAsync(workspace.Root, "10_one.yaml", "DUP", order: 10);
        await PipelineDiscoveryTests.WritePipelineAsync(workspace.Root, "20_two.yaml", "DUP", order: 20);

        var plan = await DiscoverAndPlanAsync(workspace.Root);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, error => error.Contains("Duplicate pipeline id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Build_RejectsDuplicateExplicitOrderValues()
    {
        using var workspace = TestWorkspace.Create();
        await PipelineDiscoveryTests.WritePipelineAsync(workspace.Root, "one.yaml", "ONE", order: 10);
        await PipelineDiscoveryTests.WritePipelineAsync(workspace.Root, "two.yaml", "TWO", order: 10);

        var plan = await DiscoverAndPlanAsync(workspace.Root);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, error => error.Contains("Duplicate pipeline order", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Build_RejectsCircularDependencies()
    {
        using var workspace = TestWorkspace.Create();
        await PipelineDiscoveryTests.WritePipelineAsync(
            workspace.Root,
            "one.yaml",
            "ONE",
            order: 10,
            dependsOn: ["TWO"]);
        await PipelineDiscoveryTests.WritePipelineAsync(
            workspace.Root,
            "two.yaml",
            "TWO",
            order: 20,
            dependsOn: ["ONE"]);

        var plan = await DiscoverAndPlanAsync(workspace.Root);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, error => error.Contains("Circular", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Build_RejectsMissingDependency()
    {
        using var workspace = TestWorkspace.Create();
        await PipelineDiscoveryTests.WritePipelineAsync(
            workspace.Root,
            "one.yaml",
            "ONE",
            order: 10,
            dependsOn: ["MISSING"]);

        var plan = await DiscoverAndPlanAsync(workspace.Root);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, error => error.Contains("dependency", StringComparison.OrdinalIgnoreCase) &&
                                              error.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Build_RejectsInvalidAndSelfNextReferences()
    {
        using var workspace = TestWorkspace.Create();
        await PipelineDiscoveryTests.WritePipelineAsync(
            workspace.Root,
            "one.yaml",
            "ONE",
            order: 10,
            nextApproved: "ONE");
        await PipelineDiscoveryTests.WritePipelineAsync(
            workspace.Root,
            "two.yaml",
            "TWO",
            order: 20,
            nextApproved: "MISSING");

        var plan = await DiscoverAndPlanAsync(workspace.Root);

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Errors, error => error.Contains("points to itself", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Errors, error => error.Contains("pipeline.next", StringComparison.OrdinalIgnoreCase) &&
                                              error.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<AgentBatchRunner.Models.PipelinePlan> DiscoverAndPlanAsync(string folder)
    {
        var discovery = await PipelineDiscoveryTests.CreateDiscovery().DiscoverAsync(folder);
        return new PipelinePlanBuilder().Build(discovery);
    }
}
