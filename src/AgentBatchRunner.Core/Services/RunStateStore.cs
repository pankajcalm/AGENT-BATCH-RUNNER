using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class RunStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public string CreateRunDirectory(string repoPath, string runId)
    {
        var runDirectory = Path.Combine(repoPath, ".agentbatchrunner", "runs", runId);
        Directory.CreateDirectory(runDirectory);
        return runDirectory;
    }

    public async Task SaveJsonAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(value, JsonOptions);
        await Utf8File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public async Task<BatchConfig> LoadConfigAsync(string runDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(runDirectory, "run-config.normalized.json");
        var json = await Utf8File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<BatchConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Could not load run config from {path}.");
    }

    public async Task<RunResult> LoadRunResultAsync(string runDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(runDirectory, "run-summary.json");
        var json = await Utf8File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<RunResult>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Could not load run summary from {path}.");
    }

    public string? FindLatestRunDirectory(string startDirectory)
    {
        var runsDirectory = FindRunsDirectory(startDirectory);
        if (runsDirectory is null)
        {
            return null;
        }

        return Directory.GetDirectories(runsDirectory)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public string? FindRunDirectory(string startDirectory, string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        var runsDirectory = FindRunsDirectory(startDirectory);
        if (runsDirectory is null)
        {
            return null;
        }

        var candidate = Path.Combine(runsDirectory, runId);
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static string? FindRunsDirectory(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            var runsDirectory = Path.Combine(current.FullName, ".agentbatchrunner", "runs");
            if (Directory.Exists(runsDirectory))
            {
                return runsDirectory;
            }

            current = current.Parent;
        }

        return null;
    }
}
