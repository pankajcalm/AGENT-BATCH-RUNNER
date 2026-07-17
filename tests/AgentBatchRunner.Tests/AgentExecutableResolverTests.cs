using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class AgentExecutableResolverTests
{
    [Fact]
    public void Resolve_Windows_PrefersExplicitConfiguredPath()
    {
        using var temp = TestWorkspace.Create();
        var configuredPath = CreateExecutable(temp.Root, "Configured Codex", "codex.exe");
        var nativePath = CreateExecutable(temp.Root, "Programs/OpenAI/Codex/bin", "codex.exe");
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOCALAPPDATA"] = temp.Root,
            ["PATH"] = Path.GetDirectoryName(nativePath),
            ["PATHEXT"] = ".EXE"
        };
        var resolver = CreateResolver(environment);

        var result = resolver.Resolve("codex", new BatchConfig { CodexExecutablePath = configuredPath });

        Assert.True(result.IsSuccess);
        Assert.Equal(Path.GetFullPath(configuredPath), result.ExecutablePath);
        Assert.Equal("configuration", result.Source);
    }

    [Fact]
    public void Resolve_Windows_PrefersNativeOpenAiCodexOverStalePathLauncher()
    {
        using var temp = TestWorkspace.Create();
        var nativePath = CreateExecutable(temp.Root, "Programs/OpenAI/Codex/bin", "codex.exe");
        var stalePath = CreateExecutable(temp.Root, "stale path", "codex.exe");
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["LOCALAPPDATA"] = temp.Root,
            ["PATH"] = Path.GetDirectoryName(stalePath),
            ["PATHEXT"] = ".EXE"
        };
        var resolver = CreateResolver(environment);

        var result = resolver.Resolve("codex", new BatchConfig());

        Assert.True(result.IsSuccess);
        Assert.Equal(Path.GetFullPath(nativePath), result.ExecutablePath);
        Assert.Equal("native OpenAI Windows installation", result.Source);
    }

    [Fact]
    public void Resolve_EnvironmentOverride_PrecedesNativeInstallation()
    {
        using var temp = TestWorkspace.Create();
        var environmentPath = CreateExecutable(temp.Root, "environment override", "codex.exe");
        CreateExecutable(temp.Root, "Programs/OpenAI/Codex/bin", "codex.exe");
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [AgentExecutableResolver.CodexEnvironmentVariable] = environmentPath,
            ["LOCALAPPDATA"] = temp.Root
        };
        var resolver = CreateResolver(environment);

        var result = resolver.Resolve("codex", new BatchConfig());

        Assert.Equal(Path.GetFullPath(environmentPath), result.ExecutablePath);
        Assert.Equal(AgentExecutableResolver.CodexEnvironmentVariable, result.Source);
    }

    private static AgentExecutableResolver CreateResolver(IReadOnlyDictionary<string, string?> environment)
    {
        return new AgentExecutableResolver(
            isWindows: true,
            getEnvironmentVariable: name => environment.TryGetValue(name, out var value) ? value : null);
    }

    private static string CreateExecutable(string root, string relativeDirectory, string fileName)
    {
        var directory = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "fake executable");
        return path;
    }
}
