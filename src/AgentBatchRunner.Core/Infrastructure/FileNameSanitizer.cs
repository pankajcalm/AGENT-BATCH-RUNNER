using System.Text;

namespace AgentBatchRunner.Infrastructure;

public static class FileNameSanitizer
{
    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }

        var invalid = Path.GetInvalidFileNameChars().Concat([' ', '\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '-' : character);
        }

        var sanitized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }
}
