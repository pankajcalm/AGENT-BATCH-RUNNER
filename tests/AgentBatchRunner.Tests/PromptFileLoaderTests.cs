using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class PromptFileLoaderTests
{
    [Fact]
    public async Task LoadAsync_ParsesYamlPrompts()
    {
        using var temp = TestWorkspace.Create();
        var yamlPath = Path.Combine(temp.Root, "prompts.yaml");
        await File.WriteAllTextAsync(
            yamlPath,
            $$"""
            project: Demo
            repoPath: {{temp.Root}}
            defaultAgent: dryrun
            defaultMaxRetries: 2
            defaultAgentTimeoutSeconds: 120
            defaultVerifyTimeoutSeconds: 60
            prompts:
              - id: P001
                title: Build project
                prompt: >
                  Build the project.
                verify:
                  - dotnet --version
                agentTimeoutSeconds: 30
                verifyTimeoutSeconds: 15
            """);

        var loader = new PromptFileLoader();
        var config = await loader.LoadAsync(yamlPath);

        Assert.Equal("Demo", config.Project);
        Assert.Equal("dryrun", config.DefaultAgent);
        Assert.Equal(120, config.DefaultAgentTimeoutSeconds);
        Assert.Equal(60, config.DefaultVerifyTimeoutSeconds);
        Assert.Single(config.Prompts);
        Assert.Equal("P001", config.Prompts[0].Id);
        Assert.Equal(30, config.Prompts[0].AgentTimeoutSeconds);
        Assert.Equal(15, config.Prompts[0].VerifyTimeoutSeconds);
        Assert.Equal("dotnet --version", config.Prompts[0].Verify[0]);
    }

    [Fact]
    public void Validate_ReturnsErrorsForMissingFields()
    {
        var loader = new PromptFileLoader();
        var validation = loader.Validate(new BatchConfig());

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("project", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Errors, e => e.Contains("repoPath", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Errors, e => e.Contains("prompts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AllowsEmptyVerifyList()
    {
        var loader = new PromptFileLoader();
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = @"C:\repo",
            DefaultAgent = "dryrun",
            Prompts = [new PromptTask { Id = "P001", Title = "t", Prompt = "x", Verify = [] }]
        };

        var validation = loader.Validate(config);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public void Validate_RejectsBlankVerifyEntries()
    {
        var loader = new PromptFileLoader();
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = @"C:\repo",
            DefaultAgent = "dryrun",
            Prompts = [new PromptTask { Id = "P001", Title = "t", Prompt = "x", Verify = ["  "] }]
        };

        var validation = loader.Validate(config);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("verify", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ReturnsErrorsForInvalidTimeouts()
    {
        var loader = new PromptFileLoader();
        var validation = loader.Validate(new BatchConfig
        {
            Project = "Demo",
            RepoPath = @"C:\repo",
            DefaultAgent = "dryrun",
            DefaultMaxRetries = 1,
            DefaultAgentTimeoutSeconds = 0,
            DefaultVerifyTimeoutSeconds = -1,
            Prompts =
            [
                new PromptTask
                {
                    Id = "P001",
                    Title = "t",
                    Prompt = "x",
                    Verify = ["exit 0"],
                    AgentTimeoutSeconds = 0,
                    VerifyTimeoutSeconds = -1
                }
            ]
        });

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("defaultAgentTimeoutSeconds", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Errors, e => e.Contains("defaultVerifyTimeoutSeconds", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Errors, e => e.Contains("agentTimeoutSeconds", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation.Errors, e => e.Contains("verifyTimeoutSeconds", StringComparison.OrdinalIgnoreCase));
    }
}
