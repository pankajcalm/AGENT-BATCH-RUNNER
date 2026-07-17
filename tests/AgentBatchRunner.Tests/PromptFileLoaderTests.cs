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

    [Fact]
    public async Task LoadAndValidate_ExistingYamlWithoutToolchainFields_RemainsValid()
    {
        using var temp = TestWorkspace.Create();
        var yamlPath = Path.Combine(temp.Root, "legacy-prompts.yaml");
        await File.WriteAllTextAsync(
            yamlPath,
            $$"""
            project: Legacy
            repoPath: {{temp.Root}}
            defaultAgent: dryrun
            prompts:
              - id: P001
                title: Legacy task
                prompt: Build it.
                verify: []
            """);
        var loader = new PromptFileLoader();

        var config = await loader.LoadAsync(yamlPath);
        var validation = loader.Validate(config);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal("0.144.5", config.MinimumCodexVersion);
        Assert.Null(config.CodexExecutablePath);
        Assert.Null(config.ClaudeExecutablePath);
    }

    [Fact]
    public void Validate_RejectsPromptWithNoAgentAndNoDefaultAgent()
    {
        var config = new BatchConfig
        {
            Project = "Demo",
            RepoPath = @"C:\repo",
            DefaultAgent = null,
            Prompts = [new PromptTask { Id = "P001", Title = "Task", Prompt = "Do it." }]
        };

        var validation = new PromptFileLoader().Validate(config);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("no agent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_PreservesUnicodeAndMultilinePrompt()
    {
        using var temp = TestWorkspace.Create();
        var yamlPath = Path.Combine(temp.Root, "unicode-prompts.yaml");
        var emDash = "\u2014";
        var enDash = "\u2013";
        var smartQuoteOpen = "\u201c";
        var smartQuoteClose = "\u201d";
        var yaml =
            $"project: Unicode{Environment.NewLine}" +
            $"repoPath: {temp.Root}{Environment.NewLine}" +
            $"defaultAgent: dryrun{Environment.NewLine}" +
            $"prompts:{Environment.NewLine}" +
            $"  - id: P001{Environment.NewLine}" +
            $"    title: Product charter {emDash} phase 0{Environment.NewLine}" +
            $"    prompt: |{Environment.NewLine}" +
            $"      Support 10{enDash}20 teams.{Environment.NewLine}" +
            $"      Preserve {smartQuoteOpen}quoted text{smartQuoteClose}.{Environment.NewLine}" +
            $"    verify: []{Environment.NewLine}";
        await AgentBatchRunner.Infrastructure.Utf8File.WriteAllTextAsync(yamlPath, yaml);

        var config = await new PromptFileLoader().LoadAsync(yamlPath);

        Assert.Contains(emDash, config.Prompts[0].Title);
        Assert.Contains($"10{enDash}20", config.Prompts[0].Prompt);
        Assert.Contains($"{smartQuoteOpen}quoted text{smartQuoteClose}", config.Prompts[0].Prompt);
        Assert.Contains('\n', config.Prompts[0].Prompt);
    }
}
