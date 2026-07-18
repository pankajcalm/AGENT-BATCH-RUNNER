using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public interface IRunAgentRoutingController
{
    AgentRoutingDecision Resolve(
        string promptId,
        string baseAgent,
        AgentRoutingReason baseRoutingReason);

    Task QueueSwitchAsync(AgentSwitchRequest request, CancellationToken cancellationToken);

    IReadOnlyList<AgentSwitchRequest> DequeueSwitches();

    AgentRoutingChange? ApplySwitch(
        AgentSwitchRequest request,
        IReadOnlyCollection<AgentRoutingCandidate> candidates);

    RunRoutingSnapshot CreateSnapshot(string runId);
}

public sealed class RunAgentRoutingController : IRunAgentRoutingController
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Agent, AgentRoutingReason Reason)> _routes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<AgentSwitchRequest> _queuedSwitches = new();
    private readonly List<AgentRoutingChange> _changes = [];

    public RunAgentRoutingController(RunRoutingSnapshot? snapshot = null)
    {
        if (snapshot is null)
        {
            return;
        }

        foreach (var change in snapshot.Changes)
        {
            var clone = Clone(change);
            _changes.Add(clone);
            foreach (var promptId in clone.AffectedPromptIds)
            {
                _routes[promptId] = (clone.ReplacementAgent, clone.Reason);
            }
        }
    }

    public AgentRoutingDecision Resolve(
        string promptId,
        string baseAgent,
        AgentRoutingReason baseRoutingReason)
    {
        lock (_gate)
        {
            return _routes.TryGetValue(promptId, out var route)
                ? new AgentRoutingDecision
                {
                    PromptId = promptId,
                    BaseAgent = Normalize(baseAgent),
                    EffectiveAgent = route.Agent,
                    RoutingReason = route.Reason
                }
                : new AgentRoutingDecision
                {
                    PromptId = promptId,
                    BaseAgent = Normalize(baseAgent),
                    EffectiveAgent = Normalize(baseAgent),
                    RoutingReason = baseRoutingReason
                };
        }
    }

    public Task QueueSwitchAsync(AgentSwitchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        lock (_gate)
        {
            _queuedSwitches.Enqueue(Clone(request));
        }

        return Task.CompletedTask;
    }

    public IReadOnlyList<AgentSwitchRequest> DequeueSwitches()
    {
        lock (_gate)
        {
            var requests = new List<AgentSwitchRequest>(_queuedSwitches.Count);
            while (_queuedSwitches.Count > 0)
            {
                requests.Add(_queuedSwitches.Dequeue());
            }

            return requests;
        }
    }

    public AgentRoutingChange? ApplySwitch(
        AgentSwitchRequest request,
        IReadOnlyCollection<AgentRoutingCandidate> candidates)
    {
        ValidateRequest(request);
        lock (_gate)
        {
            var source = Normalize(request.SourceAgent);
            var replacement = Normalize(request.ReplacementAgent);
            var requestedIds = request.AffectedPromptIds.Count == 0
                ? null
                : request.AffectedPromptIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var affected = candidates
                .Where(candidate => requestedIds is null || requestedIds.Contains(candidate.PromptId))
                .Where(candidate => string.Equals(
                    ResolveUnsafe(candidate.PromptId, candidate.BaseAgent).Agent,
                    source,
                    StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.PromptId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (affected.Count == 0)
            {
                return null;
            }

            var change = new AgentRoutingChange
            {
                Timestamp = DateTimeOffset.Now,
                SourceAgent = source,
                ReplacementAgent = replacement,
                Reason = request.Reason,
                IsAutomatic = request.IsAutomatic,
                StartingPromptId = request.StartingPromptId ?? affected[0],
                AffectedPromptIds = affected,
                RateLimitResetAt = request.RateLimitResetAt,
                UserConfirmed = request.UserConfirmed,
                RetryRateLimitedTask = request.RetryRateLimitedTask
            };

            foreach (var promptId in affected)
            {
                _routes[promptId] = (replacement, request.Reason);
            }

            _changes.Add(change);
            return Clone(change);
        }
    }

    public RunRoutingSnapshot CreateSnapshot(string runId)
    {
        lock (_gate)
        {
            return new RunRoutingSnapshot
            {
                RunId = runId,
                Changes = _changes.Select(Clone).ToList()
            };
        }
    }

    private (string Agent, AgentRoutingReason Reason) ResolveUnsafe(string promptId, string baseAgent)
    {
        return _routes.TryGetValue(promptId, out var route)
            ? route
            : (Normalize(baseAgent), AgentRoutingReason.Default);
    }

    private static void ValidateRequest(AgentSwitchRequest request)
    {
        var source = Normalize(request.SourceAgent);
        var replacement = Normalize(request.ReplacementAgent);
        if (!AgentBatchRunner.Agents.AgentAdapterFactory.IsSupportedAgent(source) ||
            !AgentBatchRunner.Agents.AgentAdapterFactory.IsSupportedAgent(replacement))
        {
            throw new ArgumentException("Agent switches require supported source and replacement agents.");
        }

        if (string.Equals(source, replacement, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and replacement agents must be different.");
        }
    }

    private static string Normalize(string agent)
    {
        return agent.Trim().ToLowerInvariant();
    }

    private static AgentRoutingChange Clone(AgentRoutingChange change)
    {
        return new AgentRoutingChange
        {
            Timestamp = change.Timestamp,
            SourceAgent = change.SourceAgent,
            ReplacementAgent = change.ReplacementAgent,
            Reason = change.Reason,
            IsAutomatic = change.IsAutomatic,
            StartingPromptId = change.StartingPromptId,
            AffectedPromptIds = [.. change.AffectedPromptIds],
            RateLimitResetAt = change.RateLimitResetAt,
            UserConfirmed = change.UserConfirmed,
            RetryRateLimitedTask = change.RetryRateLimitedTask
        };
    }

    private static AgentSwitchRequest Clone(AgentSwitchRequest request)
    {
        return new AgentSwitchRequest
        {
            SourceAgent = request.SourceAgent,
            ReplacementAgent = request.ReplacementAgent,
            Reason = request.Reason,
            IsAutomatic = request.IsAutomatic,
            StartingPromptId = request.StartingPromptId,
            AffectedPromptIds = [.. request.AffectedPromptIds],
            RateLimitResetAt = request.RateLimitResetAt,
            UserConfirmed = request.UserConfirmed,
            RetryRateLimitedTask = request.RetryRateLimitedTask
        };
    }
}
