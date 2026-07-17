using System.Text.Json;

namespace AgentBatchRunner.Agents;

internal static class JsonSessionParser
{
    public static string? TryExtract(string output, params string[] propertyNames)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var wholeDocumentResult = TryExtractFromJson(output, propertyNames);
        if (wholeDocumentResult is not null)
        {
            return wholeDocumentResult;
        }

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var lineResult = TryExtractFromJson(line, propertyNames);
            if (lineResult is not null)
            {
                return lineResult;
            }
        }

        return null;
    }

    private static string? TryExtractFromJson(string json, params string[] propertyNames)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return FindProperty(document.RootElement, propertyNames);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindProperty(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString();
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = FindProperty(property.Value, propertyNames);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindProperty(item, propertyNames);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }
}
