using System.Text.Json;

namespace AgentBatchRunner.Infrastructure;

public static class StructuredJsonExtractor
{
    public static bool TryExtractObject(
        string text,
        string requiredProperty,
        out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        for (var start = 0; start < text.Length; start++)
        {
            if (text[start] != '{')
            {
                continue;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < text.Length; index++)
            {
                var current = text[index];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                }
                else if (current == '{')
                {
                    depth++;
                }
                else if (current == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var candidate = text[start..(index + 1)];
                        if (HasProperty(candidate, requiredProperty))
                        {
                            json = candidate;
                            return true;
                        }

                        break;
                    }
                }
            }
        }

        return false;
    }

    private static bool HasProperty(string json, string requiredProperty)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.EnumerateObject().Any(property =>
                       string.Equals(property.Name, requiredProperty, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
