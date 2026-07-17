using System.Text.Json;

namespace AgentBatchRunner.Services;

public sealed class AgentRateLimitStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Func<DateTimeOffset> _nowProvider;

    public AgentRateLimitStateStore(string? statePath = null, Func<DateTimeOffset>? nowProvider = null)
    {
        StatePath = string.IsNullOrWhiteSpace(statePath) ? GetDefaultStatePath() : statePath;
        _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
    }

    public string StatePath { get; }

    public DateTimeOffset Now => _nowProvider();

    public static string GetDefaultStatePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "AgentBatchRunner", "agent-rate-limits.json");
    }

    public AgentRateLimitInfo Get(string agentName)
    {
        var state = LoadState();
        var changed = ClearExpiredBlocks(state);
        var normalizedAgentName = NormalizeAgentName(agentName);
        var info = state.Agents.FirstOrDefault(
            agent => string.Equals(agent.AgentName, normalizedAgentName, StringComparison.OrdinalIgnoreCase));

        if (changed)
        {
            SaveState(state);
        }

        return Clone(info ?? Available(normalizedAgentName));
    }

    public IReadOnlyList<AgentRateLimitInfo> GetAll()
    {
        var state = LoadState();
        if (ClearExpiredBlocks(state))
        {
            SaveState(state);
        }

        return state.Agents.Select(Clone).ToList();
    }

    public bool TryGetBlocked(string agentName, out AgentRateLimitInfo info)
    {
        info = Get(agentName);
        return info.IsBlocked && (!info.BlockedUntil.HasValue || info.BlockedUntil.Value > Now);
    }

    public void SetBlocked(AgentRateLimitInfo info)
    {
        var state = LoadState();
        var normalizedAgentName = NormalizeAgentName(info.AgentName);
        var existing = state.Agents.FirstOrDefault(
            agent => string.Equals(agent.AgentName, normalizedAgentName, StringComparison.OrdinalIgnoreCase));
        var stored = Clone(info);
        stored.AgentName = normalizedAgentName;
        stored.IsBlocked = true;
        if (stored.LastDetectedAt == default)
        {
            stored.LastDetectedAt = Now;
        }

        if (existing is null)
        {
            state.Agents.Add(stored);
        }
        else
        {
            existing.IsBlocked = stored.IsBlocked;
            existing.BlockedUntil = stored.BlockedUntil;
            existing.LastDetectedAt = stored.LastDetectedAt;
            existing.Reason = stored.Reason;
            existing.RawMessage = stored.RawMessage;
        }

        SaveState(state);
    }

    public void Clear(string agentName)
    {
        var state = LoadState();
        var normalizedAgentName = NormalizeAgentName(agentName);
        var existing = state.Agents.FirstOrDefault(
            agent => string.Equals(agent.AgentName, normalizedAgentName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        existing.IsBlocked = false;
        existing.BlockedUntil = null;
        existing.Reason = string.Empty;
        existing.RawMessage = string.Empty;
        SaveState(state);
    }

    private bool ClearExpiredBlocks(AgentRateLimitStateFile state)
    {
        var now = Now;
        var changed = false;
        foreach (var agent in state.Agents)
        {
            if (agent.IsBlocked && agent.BlockedUntil.HasValue && agent.BlockedUntil.Value <= now)
            {
                agent.IsBlocked = false;
                agent.BlockedUntil = null;
                agent.Reason = string.Empty;
                agent.RawMessage = string.Empty;
                changed = true;
            }
        }

        return changed;
    }

    private AgentRateLimitStateFile LoadState()
    {
        if (!File.Exists(StatePath))
        {
            return new AgentRateLimitStateFile();
        }

        try
        {
            var json = File.ReadAllText(StatePath);
            var state = JsonSerializer.Deserialize<AgentRateLimitStateFile>(json, JsonOptions)
                ?? new AgentRateLimitStateFile();
            Normalize(state);
            return state;
        }
        catch
        {
            return new AgentRateLimitStateFile();
        }
    }

    private void SaveState(AgentRateLimitStateFile state)
    {
        Normalize(state);
        var directory = Path.GetDirectoryName(StatePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static void Normalize(AgentRateLimitStateFile state)
    {
        var normalizedAgents = new List<AgentRateLimitInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in state.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.AgentName))
            {
                continue;
            }

            agent.AgentName = NormalizeAgentName(agent.AgentName);
            if (seen.Add(agent.AgentName))
            {
                normalizedAgents.Add(agent);
            }
        }

        state.Agents = normalizedAgents;
    }

    private static AgentRateLimitInfo Available(string agentName)
    {
        return new AgentRateLimitInfo { AgentName = agentName, IsBlocked = false };
    }

    private static AgentRateLimitInfo Clone(AgentRateLimitInfo info)
    {
        return new AgentRateLimitInfo
        {
            AgentName = info.AgentName,
            IsBlocked = info.IsBlocked,
            BlockedUntil = info.BlockedUntil,
            LastDetectedAt = info.LastDetectedAt,
            Reason = info.Reason,
            RawMessage = info.RawMessage
        };
    }

    private static string NormalizeAgentName(string agentName)
    {
        return agentName.Trim().ToLowerInvariant();
    }

    private sealed class AgentRateLimitStateFile
    {
        public List<AgentRateLimitInfo> Agents { get; set; } = [];
    }
}
