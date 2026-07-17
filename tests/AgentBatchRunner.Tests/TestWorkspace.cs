using System.Diagnostics;

namespace AgentBatchRunner.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private TestWorkspace(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public static TestWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "AgentBatchRunner.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TestWorkspace(root);
    }

    public static TestWorkspace CreateGitRepository()
    {
        var workspace = Create();
        RunGit(workspace.Root, "init");
        RunGit(workspace.Root, "config user.email agentbatchrunner@example.local");
        RunGit(workspace.Root, "config user.name AgentBatchRunner Tests");
        File.WriteAllText(Path.Combine(workspace.Root, "README.md"), "# Test repository");
        RunGit(workspace.Root, "add README.md");
        RunGit(workspace.Root, "commit -m init");
        return workspace;
    }

    /// <summary>Runs git in this workspace and returns trimmed stdout.</summary>
    public string Git(string arguments)
    {
        return RunGit(Root, arguments);
    }

    public string CurrentBranch()
    {
        return Git("rev-parse --abbrev-ref HEAD");
    }

    public string HeadCommit()
    {
        return Git("rev-parse HEAD");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Test cleanup is best effort.
        }
    }

    private static string RunGit(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {arguments} failed: {stdout} {stderr}");
        }

        return stdout.Trim();
    }
}
