using System.Text;
using AgentBatchRunner.Agents;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public interface IPipelineReviewRunner
{
    Task<PipelineReviewExecutionResult> ExecuteAsync(
        GeneratedPipelineReview generatedReview,
        PipelineFile sourceFile,
        RunResult executionResult,
        string pipelineRunDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class PipelineReviewRunner(
    PromptFileLoader promptFileLoader,
    IAgentAdapterProvider agentAdapterProvider,
    IAgentPreflightService preflightService,
    VerificationRunner verificationRunner,
    ProcessRunner processRunner,
    RunStateStore runStateStore,
    PipelineReviewResultParser resultParser,
    PipelineReviewReportGenerator reportGenerator,
    AgentRateLimitDetector rateLimitDetector,
    AgentRateLimitStateStore rateLimitStateStore,
    AgentToolchainFailureDetector? toolchainFailureDetector = null) : IPipelineReviewRunner
{
    private readonly AgentToolchainFailureDetector _toolchainFailureDetector =
        toolchainFailureDetector ?? new AgentToolchainFailureDetector();

    public async Task<PipelineReviewExecutionResult> ExecuteAsync(
        GeneratedPipelineReview generatedReview,
        PipelineFile sourceFile,
        RunResult executionResult,
        string pipelineRunDirectory,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ExecuteCoreAsync(
                generatedReview,
                sourceFile,
                executionResult,
                pipelineRunDirectory,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await FailAsync(
                generatedReview,
                sourceFile,
                executionResult,
                pipelineRunDirectory,
                $"Review infrastructure failed: {ex.Message}",
                CancellationToken.None);
        }
    }

    private async Task<PipelineReviewExecutionResult> ExecuteCoreAsync(
        GeneratedPipelineReview generatedReview,
        PipelineFile sourceFile,
        RunResult executionResult,
        string pipelineRunDirectory,
        CancellationToken cancellationToken = default)
    {
        var config = await promptFileLoader.LoadAsync(generatedReview.ReviewYamlPath, cancellationToken);
        var validation = promptFileLoader.Validate(config);
        if (!validation.IsValid || config.Prompts.Count != 1)
        {
            return await FailAsync(
                generatedReview,
                sourceFile,
                executionResult,
                pipelineRunDirectory,
                "Generated review YAML is invalid: " + string.Join(" ", validation.Errors),
                cancellationToken);
        }

        var prompt = config.Prompts[0];
        var agent = generatedReview.ReviewAgent;
        var reviewRunId = CreateReviewRunId(pipelineRunDirectory);
        var reviewRunDirectory = Path.Combine(pipelineRunDirectory, "review-runs", reviewRunId);
        Directory.CreateDirectory(reviewRunDirectory);

        if (rateLimitStateStore.TryGetBlocked(agent, out var blockedInfo))
        {
            return await FailAsync(
                generatedReview,
                sourceFile,
                executionResult,
                pipelineRunDirectory,
                AgentRateLimitDisplay.BlockedMessage(agent, blockedInfo),
                cancellationToken,
                PipelineReviewVerdict.RateLimited,
                reviewRunId,
                reviewRunDirectory);
        }

        var preflight = await preflightService.RunAsync(
            config,
            [agent],
            config.RepoPath,
            cancellationToken);
        if (!preflight.Succeeded)
        {
            return await FailAsync(
                generatedReview,
                sourceFile,
                executionResult,
                pipelineRunDirectory,
                preflight.FailureReason ?? $"Review-agent preflight failed for {agent}.",
                cancellationToken,
                reviewRunId: reviewRunId,
                reviewRunDirectory: reviewRunDirectory);
        }

        var beforeFingerprint = await CaptureProductWorktreeAsync(config.RepoPath, cancellationToken);
        var adapter = agentAdapterProvider.Create(agent);
        var agentResult = await adapter.ExecuteAsync(
            new AgentExecutionRequest
            {
                RepoPath = config.RepoPath,
                PromptId = prompt.Id,
                Prompt = prompt.Prompt,
                AttemptNumber = 1,
                ResumeSession = false,
                AttemptDirectory = reviewRunDirectory,
                ExecutablePath = preflight.Find(agent)?.ExecutablePath,
                Options = new AgentInvocationOptions
                {
                    TimeoutSeconds = prompt.AgentTimeoutSeconds ?? config.DefaultAgentTimeoutSeconds,
                    ClaudePermissionMode = "plan",
                    ClaudeDangerouslySkipPermissions = false,
                    CodexSandbox = "read-only",
                    CodexFullAuto = false
                }
            },
            cancellationToken);
        ApplyRateLimit(agent, agentResult);
        var toolchainFailure = _toolchainFailureDetector.Detect(agent, agentResult);
        if (!string.IsNullOrWhiteSpace(toolchainFailure))
        {
            agentResult.IsToolchainFailure = true;
            agentResult.ToolchainFailureReason = toolchainFailure;
        }

        await SaveAgentOutputAsync(reviewRunDirectory, config.RepoPath, agentResult, cancellationToken);
        var afterFingerprint = await CaptureProductWorktreeAsync(config.RepoPath, cancellationToken);
        var productFilesChanged = !string.Equals(
            beforeFingerprint,
            afterFingerprint,
            StringComparison.Ordinal);

        if (productFilesChanged)
        {
            return await FailAsync(
                generatedReview,
                sourceFile,
                executionResult,
                pipelineRunDirectory,
                "The read-only review changed product worktree state. Changes were preserved for inspection; the pipeline stopped without reset.",
                cancellationToken,
                reviewRunId: reviewRunId,
                reviewRunDirectory: reviewRunDirectory,
                agentResult: agentResult,
                productFilesChanged: true);
        }

        if (agentResult.IsRateLimited)
        {
            return await FailAsync(
                generatedReview,
                sourceFile,
                executionResult,
                pipelineRunDirectory,
                agentResult.RateLimitReason ?? $"{agent} is rate-limited.",
                cancellationToken,
                PipelineReviewVerdict.RateLimited,
                reviewRunId,
                reviewRunDirectory,
                agentResult);
        }

        if (!agentResult.Succeeded)
        {
            var reason = agentResult.IsToolchainFailure
                ? agentResult.ToolchainFailureReason
                : agentResult.TimedOut
                    ? $"Review agent timed out after {agentResult.Timeout?.TotalSeconds:0}s."
                    : $"Review agent failed with exit code {agentResult.ExitCode}.";
            return await FailAsync(
                generatedReview,
                sourceFile,
                executionResult,
                pipelineRunDirectory,
                reason ?? "Review agent failed.",
                cancellationToken,
                reviewRunId: reviewRunId,
                reviewRunDirectory: reviewRunDirectory,
                agentResult: agentResult);
        }

        if (!resultParser.TryParse(agentResult.CombinedOutput, out var reviewResult, out var parseError))
        {
            return await FailAsync(
                generatedReview,
                sourceFile,
                executionResult,
                pipelineRunDirectory,
                parseError,
                cancellationToken,
                reviewRunId: reviewRunId,
                reviewRunDirectory: reviewRunDirectory,
                agentResult: agentResult);
        }

        if (!string.Equals(reviewResult.SourcePipelineFile, sourceFile.FileName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reviewResult.ExecutionRunId, executionResult.RunId, StringComparison.OrdinalIgnoreCase))
        {
            return await FailAsync(
                generatedReview,
                sourceFile,
                executionResult,
                pipelineRunDirectory,
                "Review JSON does not match the source pipeline file and execution run ID.",
                cancellationToken,
                reviewRunId: reviewRunId,
                reviewRunDirectory: reviewRunDirectory,
                agentResult: agentResult);
        }

        reviewResult.ReviewRunId = reviewRunId;
        reviewResult.ReviewYamlPath = generatedReview.ReviewYamlPath;
        await reportGenerator.SaveAsync(
            reviewResult,
            generatedReview.ReviewResultPath,
            generatedReview.ReviewReportPath,
            cancellationToken);

        var verification = await verificationRunner.RunAsync(
            prompt.Verify,
            config.RepoPath,
            reviewRunDirectory,
            cancellationToken,
            TimeSpan.FromSeconds(prompt.VerifyTimeoutSeconds ?? config.DefaultVerifyTimeoutSeconds),
            reviewRunId,
            prompt.Id,
            prompt.Title,
            agent,
            1,
            1);
        if (!verification.Succeeded)
        {
            reviewResult.ReviewVerdict = PipelineReviewVerdict.ReviewFailed;
            reviewResult.CanAutoAdvance = false;
            reviewResult.FailureReason = verification.TimedOut
                ? $"Review artifact verification timed out after {verification.Timeout?.TotalSeconds:0}s."
                : $"Review artifact verification failed: {verification.FailedCommand}";
            await reportGenerator.SaveAsync(
                reviewResult,
                generatedReview.ReviewResultPath,
                generatedReview.ReviewReportPath,
                cancellationToken);
        }

        var execution = new PipelineReviewExecutionResult
        {
            ReviewRunDirectory = reviewRunDirectory,
            GeneratedReview = generatedReview,
            ReviewResult = reviewResult,
            AgentResult = agentResult,
            VerificationResult = verification,
            ProductFilesChanged = false
        };
        await runStateStore.SaveJsonAsync(
            Path.Combine(reviewRunDirectory, "review-execution-status.json"),
            execution,
            cancellationToken);
        return execution;
    }

    private async Task<PipelineReviewExecutionResult> FailAsync(
        GeneratedPipelineReview generatedReview,
        PipelineFile sourceFile,
        RunResult executionResult,
        string pipelineRunDirectory,
        string reason,
        CancellationToken cancellationToken,
        PipelineReviewVerdict verdict = PipelineReviewVerdict.ReviewFailed,
        string? reviewRunId = null,
        string? reviewRunDirectory = null,
        AgentExecutionResult? agentResult = null,
        bool productFilesChanged = false)
    {
        reviewRunId ??= CreateReviewRunId(pipelineRunDirectory);
        reviewRunDirectory ??= Path.Combine(pipelineRunDirectory, "review-runs", reviewRunId);
        Directory.CreateDirectory(reviewRunDirectory);
        var result = new PipelineReviewResult
        {
            SourcePipelineFile = sourceFile.FileName,
            ExecutionRunId = executionResult.RunId,
            ReviewRunId = reviewRunId,
            ExecutionStatus = DetermineExecutionStatus(executionResult),
            ReviewVerdict = verdict,
            GateId = sourceFile.Metadata?.Gate?.Id,
            GateApproved = false,
            Summary = reason,
            CanAutoAdvance = false,
            FailureReason = reason,
            ReviewYamlPath = generatedReview.ReviewYamlPath
        };
        await reportGenerator.SaveAsync(
            result,
            generatedReview.ReviewResultPath,
            generatedReview.ReviewReportPath,
            cancellationToken);
        var execution = new PipelineReviewExecutionResult
        {
            ReviewRunDirectory = reviewRunDirectory,
            GeneratedReview = generatedReview,
            ReviewResult = result,
            AgentResult = agentResult,
            ProductFilesChanged = productFilesChanged
        };
        await runStateStore.SaveJsonAsync(
            Path.Combine(reviewRunDirectory, "review-execution-status.json"),
            execution,
            cancellationToken);
        return execution;
    }

    private async Task<string> CaptureProductWorktreeAsync(
        string repoPath,
        CancellationToken cancellationToken)
    {
        var status = await processRunner.RunExecutableAsync(
            "git",
            ["status", "--porcelain=v1", "--untracked-files=all"],
            repoPath,
            cancellationToken);
        var diff = await processRunner.RunExecutableAsync(
            "git",
            ["diff", "--binary", "HEAD", "--", ".", ":(exclude).agentbatchrunner/**"],
            repoPath,
            cancellationToken);
        if (!status.Succeeded || !diff.Succeeded)
        {
            throw new InvalidOperationException("Could not capture product worktree state before read-only review.");
        }

        var filteredStatus = string.Join(
            '\n',
            status.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !IsInternalAgentBatchRunnerPath(line))
                .OrderBy(line => line, StringComparer.Ordinal));
        return filteredStatus + "\n---DIFF---\n" + diff.StandardOutput;
    }

    private static bool IsInternalAgentBatchRunnerPath(string statusLine)
    {
        var path = statusLine.Length > 3 ? statusLine[3..].Trim('"') : statusLine;
        return path.StartsWith(".agentbatchrunner/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(".agentbatchrunner\\", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyRateLimit(string agent, AgentExecutionResult result)
    {
        var info = rateLimitDetector.Detect(
            agent,
            result.StandardOutput,
            result.StandardError,
            rateLimitStateStore.Now);
        if (info is null)
        {
            return;
        }

        rateLimitStateStore.SetBlocked(info);
        result.IsRateLimited = true;
        result.RateLimitResetAt = info.BlockedUntil;
        result.RateLimitReason = info.Reason;
    }

    private static async Task SaveAgentOutputAsync(
        string reviewRunDirectory,
        string workingDirectory,
        AgentExecutionResult result,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder()
            .AppendLine($"Command: {result.Command}")
            .AppendLine($"Working directory: {workingDirectory}")
            .AppendLine($"Exit code: {result.ExitCode}")
            .AppendLine($"Timed out: {result.TimedOut}")
            .AppendLine($"Rate limited: {result.IsRateLimited}")
            .AppendLine()
            .AppendLine("STDOUT")
            .AppendLine(result.StandardOutput)
            .AppendLine("STDERR")
            .AppendLine(result.StandardError)
            .ToString();
        await Utf8File.WriteAllTextAsync(
            Path.Combine(reviewRunDirectory, "agent-output.txt"),
            SensitiveDataRedactor.Redact(content),
            cancellationToken);
    }

    private static string DetermineExecutionStatus(RunResult result)
    {
        return result.Tasks.LastOrDefault(task => task.Status is not (RunStatus.Succeeded or RunStatus.UnverifiedSuccess))
                   ?.Status.ToString() ?? "Succeeded";
    }

    private static string CreateReviewRunId(string pipelineRunDirectory)
    {
        var baseId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmssfff");
        var candidate = baseId;
        for (var suffix = 1;
             Directory.Exists(Path.Combine(pipelineRunDirectory, "review-runs", candidate));
             suffix++)
        {
            candidate = $"{baseId}-{suffix:D2}";
        }

        return candidate;
    }
}
