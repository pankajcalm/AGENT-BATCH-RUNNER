using System.Globalization;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class CommandLineApp(
    PromptFileLoader promptFileLoader,
    BatchRunner batchRunner,
    RunStateStore runStateStore,
    ReportGenerator reportGenerator,
    AgentRateLimitStateStore rateLimitStateStore,
    ConsoleLogger logger,
    Func<bool>? isElevated = null)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0].ToLowerInvariant();
        try
        {
            if ((isElevated ?? PrivilegeGuard.IsElevated)())
            {
                logger.Error("AgentBatchRunner refuses to run with administrator/root privileges.");
                return 1;
            }

            return command switch
            {
                "validate" => await ValidateAsync(args, cancellationToken),
                "run" => await RunBatchAsync(args, cancellationToken),
                "resume" => await ResumeAsync(args, cancellationToken),
                "report" => await ReportAsync(args, cancellationToken),
                "limits" => HandleLimits(args),
                _ => UnknownCommand(command)
            };
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Operation canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            logger.Error(ex.Message);
            return 1;
        }
    }

    private async Task<int> ValidateAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            logger.Error("Missing YAML path.");
            PrintUsage();
            return 1;
        }

        var config = await promptFileLoader.LoadAsync(args[1], cancellationToken);
        var validation = promptFileLoader.Validate(config);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                logger.Error(error);
            }

            return 1;
        }

        logger.Info("Prompt file is valid.");
        return 0;
    }

    private async Task<int> RunBatchAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            logger.Error("Missing YAML path.");
            PrintUsage();
            return 1;
        }

        var agentOverride = TryReadOption(args, "--agent");
        var config = await promptFileLoader.LoadAsync(args[1], cancellationToken);
        var validation = promptFileLoader.Validate(config);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                logger.Error(error);
            }

            return 1;
        }

        var blockedExitCode = CheckBlockedAgents(config, agentOverride);
        if (blockedExitCode != 0)
        {
            return blockedExitCode;
        }

        var result = await batchRunner.RunAsync(
            config,
            new RunOptions { AgentOverride = agentOverride },
            cancellationToken);

        return result.RateLimited > 0
            ? 3
            : result.NeedsHumanReview == 0 && result.Failed == 0 ? 0 : 2;
    }

    private async Task<int> ResumeAsync(string[] args, CancellationToken cancellationToken)
    {
        var agentOverride = TryReadOption(args, "--agent");
        var runDirectory = ResolveRunDirectory(args);
        if (runDirectory is null)
        {
            return 1;
        }

        var config = await runStateStore.LoadConfigAsync(runDirectory, cancellationToken);
        var previousResult = await runStateStore.LoadRunResultAsync(runDirectory, cancellationToken);
        var succeededPromptIds = previousResult.Tasks
            .Where(t => t.Status is RunStatus.Succeeded or RunStatus.UnverifiedSuccess)
            .Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var blockedExitCode = CheckBlockedAgents(config, agentOverride, succeededPromptIds);
        if (blockedExitCode != 0)
        {
            return blockedExitCode;
        }

        logger.Info($"Resuming run {previousResult.RunId}. Succeeded tasks will be skipped.");
        var result = await batchRunner.RunAsync(
            config,
            new RunOptions
            {
                RunId = previousResult.RunId,
                AgentOverride = agentOverride,
                ExistingResult = previousResult,
                SkipPromptIds = succeededPromptIds
            },
            cancellationToken);

        return result.RateLimited > 0
            ? 3
            : result.NeedsHumanReview == 0 && result.Failed == 0 ? 0 : 2;
    }

    private int HandleLimits(string[] args)
    {
        if (args.Length < 2)
        {
            return ShowLimits();
        }

        var subcommand = args[1].ToLowerInvariant();
        return subcommand switch
        {
            "set" => SetLimit(args),
            "clear" => ClearLimit(args),
            "show" or "list" => ShowLimits(),
            _ => UnknownLimitsSubcommand(subcommand)
        };
    }

    private int ShowLimits()
    {
        foreach (var agentName in new[] { "claude", "codex" })
        {
            var info = rateLimitStateStore.Get(agentName);
            Console.WriteLine($"{agentName}: {AgentRateLimitDisplay.Availability(agentName, info)}");
        }

        return 0;
    }

    private int SetLimit(string[] args)
    {
        if (!TryReadLimitAgent(args, "set", out var agentName))
        {
            return 1;
        }

        var untilText = TryReadOption(args, "--until");
        if (string.IsNullOrWhiteSpace(untilText) ||
            !DateTimeOffset.TryParse(
                untilText,
                CultureInfo.CurrentCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                out var until))
        {
            logger.Error("Missing or invalid --until value. Use an ISO-8601 timestamp, e.g. 2026-06-26T13:27:00-04:00.");
            return 1;
        }

        var reason = TryReadOption(args, "--reason");
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "Usage limit reached";
        }

        rateLimitStateStore.SetBlocked(new AgentRateLimitInfo
        {
            AgentName = agentName,
            IsBlocked = true,
            BlockedUntil = until,
            Reason = reason,
            RawMessage = "Manually set via CLI."
        });

        logger.Info($"Blocked {agentName} until {AgentRateLimitDisplay.FormatLocal(until)}. Reason: {reason}");
        return 0;
    }

    private int ClearLimit(string[] args)
    {
        if (!TryReadLimitAgent(args, "clear", out var agentName))
        {
            return 1;
        }

        rateLimitStateStore.Clear(agentName);
        logger.Info($"Cleared saved rate-limit state for {agentName}.");
        return 0;
    }

    private bool TryReadLimitAgent(string[] args, string subcommand, out string agentName)
    {
        agentName = string.Empty;
        if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
        {
            logger.Error($"Usage: limits {subcommand} <claude|codex>" +
                (subcommand == "set" ? " --until <ISO-8601> [--reason <text>]" : string.Empty));
            return false;
        }

        var candidate = args[2].Trim().ToLowerInvariant();
        if (candidate is not ("claude" or "codex"))
        {
            logger.Error($"Cannot {subcommand} a limit for agent '{args[2]}'. Only claude and codex are supported.");
            return false;
        }

        agentName = candidate;
        return true;
    }

    private int UnknownLimitsSubcommand(string subcommand)
    {
        logger.Error($"Unknown limits subcommand: {subcommand}");
        PrintUsage();
        return 1;
    }

    private int CheckBlockedAgents(
        BatchConfig config,
        string? agentOverride,
        IReadOnlySet<string>? skipPromptIds = null)
    {
        foreach (var agentName in ResolveEffectiveAgents(config, agentOverride, skipPromptIds))
        {
            if (string.Equals(agentName, "dryrun", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rateLimitStateStore.TryGetBlocked(agentName, out var info))
            {
                logger.Error(AgentRateLimitDisplay.BlockedMessage(agentName, info));
                return 3;
            }
        }

        return 0;
    }

    private static IEnumerable<string> ResolveEffectiveAgents(
        BatchConfig config,
        string? agentOverride,
        IReadOnlySet<string>? skipPromptIds)
    {
        return config.Prompts
            .Where(prompt => skipPromptIds?.Contains(prompt.Id) != true)
            .Select(prompt => (agentOverride ?? prompt.Agent ?? config.DefaultAgent).Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<int> ReportAsync(string[] args, CancellationToken cancellationToken)
    {
        var runDirectory = ResolveRunDirectory(args);
        if (runDirectory is null)
        {
            return 1;
        }

        var result = await runStateStore.LoadRunResultAsync(runDirectory, cancellationToken);
        await reportGenerator.GenerateAsync(runDirectory, result, cancellationToken);
        var reportPath = Path.Combine(runDirectory, "final-report.md");
        logger.Info($"Report generated: {reportPath}");
        Console.WriteLine(await File.ReadAllTextAsync(reportPath, cancellationToken));
        return 0;
    }

    /// <summary>
    /// Resolves which run directory <c>resume</c>/<c>report</c> should act on. With <c>--run-id</c>
    /// the exact run is selected; otherwise the most recent run is used. Discovery walks up from the
    /// current working directory, so these commands must be run from the repo root (or an ancestor).
    /// </summary>
    private string? ResolveRunDirectory(string[] args)
    {
        var startDirectory = Directory.GetCurrentDirectory();
        var runId = TryReadOption(args, "--run-id");
        if (!string.IsNullOrWhiteSpace(runId))
        {
            var runDirectory = runStateStore.FindRunDirectory(startDirectory, runId);
            if (runDirectory is null)
            {
                logger.Error($"No run with id '{runId}' found under .agentbatchrunner/runs.");
            }

            return runDirectory;
        }

        var latest = runStateStore.FindLatestRunDirectory(startDirectory);
        if (latest is null)
        {
            logger.Error("No previous run found under .agentbatchrunner/runs (run from the repo root).");
        }

        return latest;
    }

    private static string? TryReadOption(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            var prefix = optionName + "=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[prefix.Length..];
            }
        }

        return null;
    }

    private static bool IsHelp(string arg)
    {
        return arg is "-h" or "--help" or "help";
    }

    private int UnknownCommand(string command)
    {
        logger.Error($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            AgentBatchRunner

            Usage:
              AgentBatchRunner validate <prompts.yaml>
              AgentBatchRunner run <prompts.yaml> [--agent claude|codex|dryrun]
              AgentBatchRunner resume [--run-id <id>] [--agent claude|codex|dryrun]
              AgentBatchRunner report [--run-id <id>]
              AgentBatchRunner limits
              AgentBatchRunner limits set <claude|codex> --until <ISO-8601> [--reason <text>]
              AgentBatchRunner limits clear <claude|codex>

            resume/report act on the latest run by default; pass --run-id to target a
            specific run. Run them from the repo root so the run history can be found.

            limits set/clear manually block or unblock an agent (for example when Codex or
            Claude Desktop shows a usage-limit reset time that the CLI did not capture).
            """);
    }
}
