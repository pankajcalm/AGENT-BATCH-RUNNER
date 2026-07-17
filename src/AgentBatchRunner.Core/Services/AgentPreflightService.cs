using System.Text.RegularExpressions;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public interface IAgentPreflightService
{
    Task<AgentPreflightResult> RunAsync(
        BatchConfig config,
        IReadOnlyCollection<string> effectiveAgents,
        string workingDirectory,
        CancellationToken cancellationToken);
}

public sealed class AgentPreflightService(
    ProcessRunner processRunner,
    IAgentExecutableResolver executableResolver) : IAgentPreflightService
{
    private static readonly Regex CodexVersionPattern = new(
        @"\bcodex-cli\s+(?<version>\d+(?:\.\d+){1,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClaudeVersionPattern = new(
        @"\b(?<version>\d+(?:\.\d+){1,3})\b",
        RegexOptions.Compiled);

    public async Task<AgentPreflightResult> RunAsync(
        BatchConfig config,
        IReadOnlyCollection<string> effectiveAgents,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = new AgentPreflightResult();
        foreach (var agentName in effectiveAgents.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(agentName, "dryrun", StringComparison.OrdinalIgnoreCase))
            {
                result.Toolchains.Add(new AgentToolchainInfo
                {
                    AgentName = "dryrun",
                    Status = AgentPreflightStatus.NotRequired,
                    Version = "built-in"
                });
                continue;
            }

            var resolution = executableResolver.Resolve(agentName, config);
            if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.ExecutablePath))
            {
                return Fail(result, agentName, null, resolution.ErrorMessage ?? $"Could not resolve {agentName}.");
            }

            var processResult = await processRunner.RunExecutableAsync(
                resolution.ExecutablePath,
                ["--version"],
                workingDirectory,
                cancellationToken,
                timeout: TimeSpan.FromSeconds(30));
            var output = processResult.CombinedOutput.Trim();
            if (!processResult.Succeeded)
            {
                var reason = $"{agentName} version preflight failed for '{resolution.ExecutablePath}' " +
                             $"with exit code {processResult.ExitCode}: {Summarize(output)}";
                return Fail(result, agentName, resolution.ExecutablePath, reason);
            }

            if (LooksLikeWslGuidance(output))
            {
                return Fail(
                    result,
                    agentName,
                    resolution.ExecutablePath,
                    $"{agentName} launcher '{resolution.ExecutablePath}' returned WSL installation guidance instead of a native version. " +
                    "Configure the native executable path and restart AgentBatchRunner.");
            }

            if (!TryParseVersion(agentName, output, out var version))
            {
                return Fail(
                    result,
                    agentName,
                    resolution.ExecutablePath,
                    $"Could not parse the {agentName} version from '{resolution.ExecutablePath}'. Output: {Summarize(output)}");
            }

            if (string.Equals(agentName, "codex", StringComparison.OrdinalIgnoreCase) &&
                (!Version.TryParse(config.MinimumCodexVersion, out var minimumVersion) || version < minimumVersion))
            {
                return Fail(
                    result,
                    agentName,
                    resolution.ExecutablePath,
                    $"Codex '{resolution.ExecutablePath}' is version {version}; minimum required is {config.MinimumCodexVersion}. " +
                    "Configure the native current Codex executable and restart AgentBatchRunner.",
                    version.ToString());
            }

            result.Toolchains.Add(new AgentToolchainInfo
            {
                AgentName = agentName.ToLowerInvariant(),
                ExecutablePath = resolution.ExecutablePath,
                Version = version.ToString(),
                Status = AgentPreflightStatus.Succeeded
            });
        }

        result.Succeeded = true;
        return result;
    }

    private static AgentPreflightResult Fail(
        AgentPreflightResult result,
        string agentName,
        string? executablePath,
        string reason,
        string? version = null)
    {
        result.Succeeded = false;
        result.FailureReason = reason;
        result.Toolchains.Add(new AgentToolchainInfo
        {
            AgentName = agentName.ToLowerInvariant(),
            ExecutablePath = executablePath,
            Version = version,
            Status = AgentPreflightStatus.Failed,
            FailureReason = reason
        });
        return result;
    }

    private static bool TryParseVersion(string agentName, string output, out Version version)
    {
        version = new Version(0, 0);
        var pattern = string.Equals(agentName, "codex", StringComparison.OrdinalIgnoreCase)
            ? CodexVersionPattern
            : ClaudeVersionPattern;
        var match = pattern.Match(output);
        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var parsedVersion))
        {
            return false;
        }

        version = parsedVersion;
        return true;
    }

    private static bool LooksLikeWslGuidance(string output)
    {
        return output.Contains("Windows Subsystem for Linux", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("install WSL", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("run Codex in WSL", StringComparison.OrdinalIgnoreCase);
    }

    private static string Summarize(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "(no output)";
        }

        var normalized = SensitiveDataRedactor.Redact(output).ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "...";
    }
}

public sealed class AgentPreflightResult
{
    public bool Succeeded { get; set; }

    public string? FailureReason { get; set; }

    public List<AgentToolchainInfo> Toolchains { get; set; } = [];

    public AgentToolchainInfo? Find(string agentName)
    {
        return Toolchains.FirstOrDefault(toolchain =>
            string.Equals(toolchain.AgentName, agentName, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class NoOpAgentPreflightService : IAgentPreflightService
{
    public static NoOpAgentPreflightService Instance { get; } = new();

    public Task<AgentPreflightResult> RunAsync(
        BatchConfig config,
        IReadOnlyCollection<string> effectiveAgents,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new AgentPreflightResult { Succeeded = true });
    }
}
