using AgentBatchRunner.Agents;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class RateLimitFallbackPolicy
{
    public IReadOnlyList<string> GetConfiguredFallbackAgents(BatchConfig config)
    {
        return (config.RateLimitFallbacks ?? new Dictionary<string, List<string>>())
            .SelectMany(pair => pair.Value ?? [])
            .Select(EffectiveAgentPolicy.NormalizeOptional)
            .Where(agent => agent is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetFallbacks(BatchConfig config, string sourceAgent)
    {
        var pair = (config.RateLimitFallbacks ?? new Dictionary<string, List<string>>())
            .FirstOrDefault(item => string.Equals(item.Key, sourceAgent, StringComparison.OrdinalIgnoreCase));
        return (pair.Value ?? [])
            .Select(EffectiveAgentPolicy.NormalizeOptional)
            .Where(agent => agent is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> Validate(BatchConfig config)
    {
        var errors = new List<string>();
        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.RateLimitFallbacks ?? new Dictionary<string, List<string>>())
        {
            var source = EffectiveAgentPolicy.NormalizeOptional(pair.Key);
            if (source is null || !AgentAdapterFactory.IsSupportedAgent(source))
            {
                errors.Add($"rateLimitFallbacks source '{pair.Key}' is not supported. Use claude, codex, or dryrun.");
                continue;
            }

            var fallbacks = new List<string>();
            foreach (var rawFallback in pair.Value ?? [])
            {
                var fallback = EffectiveAgentPolicy.NormalizeOptional(rawFallback);
                if (fallback is null || !AgentAdapterFactory.IsSupportedAgent(fallback))
                {
                    errors.Add($"rateLimitFallbacks.{source} contains unsupported agent '{rawFallback}'. Use claude, codex, or dryrun.");
                    continue;
                }

                if (string.Equals(source, fallback, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"rateLimitFallbacks.{source} cannot route back to {source}.");
                    continue;
                }

                if (!fallbacks.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                {
                    fallbacks.Add(fallback);
                }
            }

            graph[source] = fallbacks;
        }

        var cycle = FindCycle(graph);
        if (cycle.Count > 0)
        {
            errors.Add($"rateLimitFallbacks contains a routing cycle: {string.Join(" -> ", cycle)}.");
        }

        return errors;
    }

    private static IReadOnlyList<string> FindCycle(Dictionary<string, List<string>> graph)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        foreach (var node in graph.Keys)
        {
            if (Visit(node, graph, visiting, visited, path, out var cycle))
            {
                return cycle;
            }
        }

        return [];
    }

    private static bool Visit(
        string node,
        Dictionary<string, List<string>> graph,
        HashSet<string> visiting,
        HashSet<string> visited,
        List<string> path,
        out IReadOnlyList<string> cycle)
    {
        if (visiting.Contains(node))
        {
            var start = path.FindIndex(item => string.Equals(item, node, StringComparison.OrdinalIgnoreCase));
            cycle = [.. path.Skip(Math.Max(0, start)), node];
            return true;
        }

        if (visited.Contains(node))
        {
            cycle = [];
            return false;
        }

        visiting.Add(node);
        path.Add(node);
        if (graph.TryGetValue(node, out var nextAgents))
        {
            foreach (var next in nextAgents)
            {
                if (Visit(next, graph, visiting, visited, path, out cycle))
                {
                    return true;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        visiting.Remove(node);
        visited.Add(node);
        cycle = [];
        return false;
    }
}
