using AgentBatchRunner.Agents;
using AgentBatchRunner.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentBatchRunner.Services;

public sealed class PromptFileLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<BatchConfig> LoadAsync(string yamlPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(yamlPath))
        {
            throw new ArgumentException("YAML path is required.", nameof(yamlPath));
        }

        if (!File.Exists(yamlPath))
        {
            throw new FileNotFoundException("Prompt YAML file was not found.", yamlPath);
        }

        var yaml = await File.ReadAllTextAsync(yamlPath, cancellationToken);
        try
        {
            return _deserializer.Deserialize<BatchConfig>(yaml) ?? new BatchConfig();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not parse YAML file '{yamlPath}': {ex.Message}", ex);
        }
    }

    public BatchValidationResult Validate(BatchConfig config)
    {
        var result = new BatchValidationResult();

        if (string.IsNullOrWhiteSpace(config.Project))
        {
            result.Errors.Add("project is required.");
        }

        if (string.IsNullOrWhiteSpace(config.RepoPath))
        {
            result.Errors.Add("repoPath is required.");
        }

        if (string.IsNullOrWhiteSpace(config.DefaultAgent))
        {
            result.Errors.Add("defaultAgent is required.");
        }
        else if (!AgentAdapterFactory.IsSupportedAgent(config.DefaultAgent))
        {
            result.Errors.Add($"defaultAgent '{config.DefaultAgent}' is not supported. Use claude, codex, or dryrun.");
        }

        if (config.DefaultMaxRetries < 1)
        {
            result.Errors.Add("defaultMaxRetries must be 1 or greater.");
        }

        if (config.DefaultAgentTimeoutSeconds < 1)
        {
            result.Errors.Add("defaultAgentTimeoutSeconds must be 1 or greater.");
        }

        if (config.DefaultVerifyTimeoutSeconds < 1)
        {
            result.Errors.Add("defaultVerifyTimeoutSeconds must be 1 or greater.");
        }

        if (config.DefaultCommandTimeoutSeconds < 0)
        {
            result.Errors.Add("defaultCommandTimeoutSeconds must be 0 or greater.");
        }

        if (config.Prompts.Count == 0)
        {
            result.Errors.Add("prompts must contain at least one prompt.");
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < config.Prompts.Count; i++)
        {
            var prompt = config.Prompts[i];
            var prefix = $"prompts[{i}]";

            if (string.IsNullOrWhiteSpace(prompt.Id))
            {
                result.Errors.Add($"{prefix}.id is required.");
            }
            else if (!seenIds.Add(prompt.Id))
            {
                result.Errors.Add($"{prefix}.id '{prompt.Id}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(prompt.Title))
            {
                result.Errors.Add($"{prefix}.title is required.");
            }

            if (!string.IsNullOrWhiteSpace(prompt.Agent) && !AgentAdapterFactory.IsSupportedAgent(prompt.Agent))
            {
                result.Errors.Add($"{prefix}.agent '{prompt.Agent}' is not supported. Use claude, codex, or dryrun.");
            }

            if (string.IsNullOrWhiteSpace(prompt.Prompt))
            {
                result.Errors.Add($"{prefix}.prompt is required.");
            }

            // verify is optional: an empty list yields an UnverifiedSuccess result. If commands
            // are listed, none may be blank.
            if (prompt.Verify.Any(string.IsNullOrWhiteSpace))
            {
                result.Errors.Add($"{prefix}.verify entries must not be blank.");
            }

            if (prompt.MaxRetries is < 1)
            {
                result.Errors.Add($"{prefix}.maxRetries must be 1 or greater.");
            }

            if (prompt.AgentTimeoutSeconds is < 1)
            {
                result.Errors.Add($"{prefix}.agentTimeoutSeconds must be 1 or greater.");
            }

            if (prompt.VerifyTimeoutSeconds is < 1)
            {
                result.Errors.Add($"{prefix}.verifyTimeoutSeconds must be 1 or greater.");
            }
        }

        return result;
    }
}
