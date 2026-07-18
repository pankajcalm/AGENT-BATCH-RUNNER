using System.Text.Json;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class AgentOutcomeParser
{
    public AgentOutcomeInfo? Parse(string output)
    {
        if (StructuredJsonExtractor.TryExtractObject(output, "agentOutcome", out var json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!TryParseOutcome(GetString(root, "agentOutcome"), out var outcome))
                {
                    return null;
                }

                return new AgentOutcomeInfo
                {
                    AgentOutcome = outcome,
                    BlockerCode = GetString(root, "blockerCode"),
                    Blocker = GetString(root, "blocker"),
                    RecommendedNext = GetString(root, "recommendedNext"),
                    RawMessage = json
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return ParseFooter(output);
    }

    private static AgentOutcomeInfo? ParseFooter(string output)
    {
        var values = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("AGENT_OUTCOME", out var outcomeText) ||
            !TryParseOutcome(outcomeText, out var outcome))
        {
            return null;
        }

        values.TryGetValue("BLOCKER_CODE", out var blockerCode);
        values.TryGetValue("BLOCKER", out var blocker);
        values.TryGetValue("RECOMMENDED_NEXT", out var recommendedNext);
        return new AgentOutcomeInfo
        {
            AgentOutcome = outcome,
            BlockerCode = blockerCode,
            Blocker = blocker,
            RecommendedNext = recommendedNext,
            RawMessage = string.Join(Environment.NewLine, values.Select(pair => $"{pair.Key}: {pair.Value}"))
        };
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }

        return null;
    }

    private static bool TryParseOutcome(string? value, out AgentOutcome outcome)
    {
        return Enum.TryParse(value?.Replace(" ", string.Empty, StringComparison.Ordinal), true, out outcome) &&
               outcome != AgentOutcome.Unspecified;
    }
}
