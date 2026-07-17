using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public interface IAgentExecutableResolver
{
    AgentExecutableResolution Resolve(string agentName, BatchConfig config);
}

public sealed class AgentExecutableResolver : IAgentExecutableResolver
{
    public const string CodexEnvironmentVariable = "AGENTBATCHRUNNER_CODEX_PATH";
    public const string ClaudeEnvironmentVariable = "AGENTBATCHRUNNER_CLAUDE_PATH";

    private readonly bool _isWindows;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string> _getFullPath;

    public AgentExecutableResolver(
        bool? isWindows = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Func<string, bool>? fileExists = null,
        Func<string, string>? getFullPath = null)
    {
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _fileExists = fileExists ?? File.Exists;
        _getFullPath = getFullPath ?? Path.GetFullPath;
    }

    public AgentExecutableResolution Resolve(string agentName, BatchConfig config)
    {
        var normalizedAgent = EffectiveAgentPolicy.NormalizeOptional(agentName);
        if (normalizedAgent is not ("claude" or "codex"))
        {
            return AgentExecutableResolution.Failed(agentName, $"Agent '{agentName}' does not require an external executable.");
        }

        var configuredPath = normalizedAgent == "codex"
            ? config.CodexExecutablePath
            : config.ClaudeExecutablePath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return ResolveAbsoluteCandidate(normalizedAgent, configuredPath, "configuration");
        }

        var environmentName = normalizedAgent == "codex"
            ? CodexEnvironmentVariable
            : ClaudeEnvironmentVariable;
        var environmentPath = _getEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return ResolveAbsoluteCandidate(normalizedAgent, environmentPath, environmentName);
        }

        if (_isWindows && normalizedAgent == "codex")
        {
            var localAppData = _getEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                var nativePath = Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
                if (_fileExists(nativePath))
                {
                    return ResolveAbsoluteCandidate(
                        normalizedAgent,
                        nativePath,
                        "native OpenAI Windows installation");
                }
            }
        }

        var pathResolution = ResolveFromPath(normalizedAgent);
        return pathResolution ?? AgentExecutableResolution.Failed(
            normalizedAgent,
            $"Could not resolve {normalizedAgent} to an executable. Configure an absolute path or set {environmentName}.");
    }

    private AgentExecutableResolution ResolveAbsoluteCandidate(string agentName, string candidate, string source)
    {
        try
        {
            var trimmed = candidate.Trim().Trim('"');
            if (!Path.IsPathRooted(trimmed))
            {
                return AgentExecutableResolution.Failed(
                    agentName,
                    $"The {source} executable path must be absolute: {trimmed}");
            }

            var fullPath = _getFullPath(trimmed);
            return _fileExists(fullPath)
                ? AgentExecutableResolution.Succeeded(agentName, fullPath, source)
                : AgentExecutableResolution.Failed(agentName, $"The {source} executable does not exist: {fullPath}");
        }
        catch (Exception ex)
        {
            return AgentExecutableResolution.Failed(
                agentName,
                $"The {source} executable path is invalid: {ex.Message}");
        }
    }

    private AgentExecutableResolution? ResolveFromPath(string agentName)
    {
        var pathValue = _getEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var executableNames = GetExecutableNames(agentName);
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            foreach (var executableName in executableNames)
            {
                try
                {
                    var candidate = Path.Combine(cleanDirectory, executableName);
                    if (_fileExists(candidate))
                    {
                        return AgentExecutableResolution.Succeeded(agentName, _getFullPath(candidate), "PATH");
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries and continue looking for a usable executable.
                }
            }
        }

        return null;
    }

    private IReadOnlyList<string> GetExecutableNames(string agentName)
    {
        if (!_isWindows)
        {
            return [agentName];
        }

        var pathExtensions = _getEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathExtensions)
            ? new[] { ".EXE", ".CMD", ".BAT" }
            : pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return extensions
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .Select(extension => agentName + extension.ToLowerInvariant())
            .ToList();
    }
}

public sealed class AgentExecutableResolution
{
    public string AgentName { get; init; } = string.Empty;

    public bool IsSuccess { get; init; }

    public string? ExecutablePath { get; init; }

    public string? Source { get; init; }

    public string? ErrorMessage { get; init; }

    public static AgentExecutableResolution Succeeded(string agentName, string executablePath, string source)
    {
        return new AgentExecutableResolution
        {
            AgentName = agentName,
            IsSuccess = true,
            ExecutablePath = executablePath,
            Source = source
        };
    }

    public static AgentExecutableResolution Failed(string agentName, string errorMessage)
    {
        return new AgentExecutableResolution
        {
            AgentName = agentName,
            ErrorMessage = errorMessage
        };
    }
}
