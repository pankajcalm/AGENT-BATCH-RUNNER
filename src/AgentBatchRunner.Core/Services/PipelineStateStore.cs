using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class PipelineStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string CreatePipelineRunDirectory(string repoPath, string pipelineRunId)
    {
        var path = Path.Combine(repoPath, ".agentbatchrunner", "pipelines", pipelineRunId);
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task SaveStateAsync(
        PipelineRunState state,
        CancellationToken cancellationToken = default)
    {
        await SaveJsonAtomicAsync(
            Path.Combine(state.PipelineRunDirectory, "pipeline-state.json"),
            state,
            cancellationToken);
        await SaveJsonAtomicAsync(
            Path.Combine(state.PipelineRunDirectory, "pipeline-summary.json"),
            state,
            cancellationToken);
        await SaveJsonAtomicAsync(
            Path.Combine(state.PipelineRunDirectory, "queue.json"),
            state.Files,
            cancellationToken);
    }

    public Task SaveDiscoveredFilesAsync(
        string pipelineRunDirectory,
        PipelinePlan plan,
        CancellationToken cancellationToken = default)
    {
        var snapshot = plan.Files.Select(file => new
        {
            file.PipelineId,
            file.FilePath,
            file.RelativePath,
            file.FileName,
            file.Title,
            file.Phase,
            file.Order,
            file.Dependencies,
            file.IsLegacy
        });
        return SaveJsonAtomicAsync(
            Path.Combine(pipelineRunDirectory, "discovered-files.json"),
            snapshot,
            cancellationToken);
    }

    public async Task<PipelineRunState> LoadStateAsync(
        string pipelineRunDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(pipelineRunDirectory, "pipeline-state.json");
        var json = await Utf8File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<PipelineRunState>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Could not load pipeline state from {path}.");
    }

    public string? FindPipelineDirectory(string startDirectory, string pipelineRunId)
    {
        var pipelinesDirectory = FindPipelinesDirectory(startDirectory);
        if (pipelinesDirectory is null)
        {
            return null;
        }

        var candidate = Path.Combine(pipelinesDirectory, pipelineRunId);
        return Directory.Exists(candidate) ? candidate : null;
    }

    public string? FindLatestPipelineDirectory(string startDirectory)
    {
        var pipelinesDirectory = FindPipelinesDirectory(startDirectory);
        return pipelinesDirectory is null
            ? null
            : Directory.GetDirectories(pipelinesDirectory)
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault();
    }

    private static async Task SaveJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await Utf8File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(value, JsonOptions),
            cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string? FindPipelinesDirectory(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".agentbatchrunner", "pipelines");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}
