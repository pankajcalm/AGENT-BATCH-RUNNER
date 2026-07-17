using System.Text.RegularExpressions;

namespace AgentBatchRunner.Infrastructure;

public static partial class SensitiveDataRedactor
{
    private static readonly Regex[] Patterns =
    [
        KeyValueSecretRegex(),
        ConnectionStringPasswordRegex()
    ];

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = value;
        foreach (var pattern in Patterns)
        {
            redacted = pattern.Replace(redacted, match => match.Groups[1].Value + "[REDACTED]" + match.Groups[3].Value);
        }

        return redacted;
    }

    [GeneratedRegex(@"(?i)\b((?:password|pwd|api_key|token|secret)\s*=\s*)([^;\s&""']+)([;\s&""']?)")]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"(?i)\b(Password\s*=\s*)([^;]+)(;?)")]
    private static partial Regex ConnectionStringPasswordRegex();
}
