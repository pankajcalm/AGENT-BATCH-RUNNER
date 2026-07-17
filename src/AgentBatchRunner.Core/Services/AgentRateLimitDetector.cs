using System.Globalization;
using System.Text.RegularExpressions;
using AgentBatchRunner.Infrastructure;

namespace AgentBatchRunner.Services;

public sealed class AgentRateLimitDetector
{
    private static readonly string[] TriggerPatterns =
    [
        "usage limit reached",
        "rate limit reached",
        "rate limit exceeded",
        "rate limit resets",
        "too many requests",
        "quota exceeded",
        "limit resets",
        "reset at",
        "try again at",
        "retry after",
        "available again",
        "out of codex messages",
        "out of claude messages",
        "out of messages",
        "session limit",
        "hit your session limit"
    ];

    private static readonly Regex RelativeDurationRegex = new(
        @"\b(?:retry\s+after|try\s+again\s+in|available\s+again\s+in|resets?\s+in|limit\s+resets\s+in)\s+(?<amount>\d+)\s*(?<unit>seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RetryAfterSecondsRegex = new(
        @"\b(?:retry-after|retry\s+after)\s*:?\s*(?<seconds>\d{1,8})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnixEpochSecondsRegex = new(
        @"\b(?<epoch>[1-4]\d{9})\b",
        RegexOptions.Compiled);

    private static readonly Regex IsoTimestampRegex = new(
        @"\b(?<iso>\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:\s?(?:Z|[+-]\d{2}:?\d{2}))?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NaturalResetTimeRegex = new(
        @"\b(?:limits?\s+resets?|resets?|reset|try\s+again|available\s+again)\s+(?:at\s+|on\s+)?(?<time>\d{1,2}(?::\d{2})?\s*(?:am|pm))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AgentRateLimitInfo? Detect(
        string agentName,
        string? standardOutput,
        string? standardError,
        DateTimeOffset? detectedAt = null)
    {
        var rawMessage = JoinOutput(standardOutput, standardError);
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return null;
        }

        var matchedPattern = TriggerPatterns.FirstOrDefault(
            pattern => rawMessage.Contains(pattern, StringComparison.OrdinalIgnoreCase));

        // Fall back to a clock-time reset phrase ("resets 1pm", "resets on 1:27 PM") so messages
        // that only carry the reset time are still recognized as a limit.
        if (matchedPattern is null && NaturalResetTimeRegex.IsMatch(rawMessage))
        {
            matchedPattern = "reset time";
        }

        if (matchedPattern is null)
        {
            return null;
        }

        var now = detectedAt ?? DateTimeOffset.Now;
        return new AgentRateLimitInfo
        {
            AgentName = NormalizeAgentName(agentName),
            IsBlocked = true,
            BlockedUntil = TryParseResetTime(rawMessage, now),
            LastDetectedAt = now,
            Reason = $"Detected agent usage/rate limit message: {matchedPattern}.",
            RawMessage = SensitiveDataRedactor.Redact(Truncate(rawMessage))
        };
    }

    public DateTimeOffset? TryParseResetTime(string message, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var relative = TryParseRelativeDuration(message, now);
        if (relative.HasValue)
        {
            return relative;
        }

        var retryAfterSeconds = TryParseRetryAfterSeconds(message, now);
        if (retryAfterSeconds.HasValue)
        {
            return retryAfterSeconds;
        }

        var isoTimestamp = TryParseIsoTimestamp(message);
        if (isoTimestamp.HasValue)
        {
            return isoTimestamp;
        }

        var unixEpoch = TryParseUnixEpochSeconds(message, now);
        if (unixEpoch.HasValue)
        {
            return unixEpoch;
        }

        return TryParseNaturalResetTime(message, now);
    }

    private static DateTimeOffset? TryParseRelativeDuration(string message, DateTimeOffset now)
    {
        var match = RelativeDurationRegex.Match(message);
        if (!match.Success || !int.TryParse(match.Groups["amount"].Value, out var amount) || amount < 0)
        {
            return null;
        }

        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        return unit switch
        {
            "s" or "sec" or "secs" or "second" or "seconds" => now.AddSeconds(amount),
            "m" or "min" or "mins" or "minute" or "minutes" => now.AddMinutes(amount),
            "h" or "hr" or "hrs" or "hour" or "hours" => now.AddHours(amount),
            "d" or "day" or "days" => now.AddDays(amount),
            _ => null
        };
    }

    private static DateTimeOffset? TryParseRetryAfterSeconds(string message, DateTimeOffset now)
    {
        var match = RetryAfterSecondsRegex.Match(message);
        if (!match.Success || !int.TryParse(match.Groups["seconds"].Value, out var seconds) || seconds < 0)
        {
            return null;
        }

        return now.AddSeconds(seconds);
    }

    private static DateTimeOffset? TryParseIsoTimestamp(string message)
    {
        foreach (Match match in IsoTimestampRegex.Matches(message))
        {
            var value = match.Groups["iso"].Value;
            if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                    out var timestamp))
            {
                return timestamp;
            }
        }

        return null;
    }

    private static DateTimeOffset? TryParseUnixEpochSeconds(string message, DateTimeOffset now)
    {
        foreach (Match match in UnixEpochSecondsRegex.Matches(message))
        {
            if (!long.TryParse(match.Groups["epoch"].Value, out var seconds))
            {
                continue;
            }

            try
            {
                var timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (timestamp >= now.AddMinutes(-5) && timestamp <= now.AddYears(5))
                {
                    return timestamp;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // Ignore numbers that are not plausible Unix timestamps.
            }
        }

        return null;
    }

    private static DateTimeOffset? TryParseNaturalResetTime(string message, DateTimeOffset now)
    {
        var match = NaturalResetTimeRegex.Match(message);
        if (!match.Success)
        {
            return null;
        }

        var dateAndTime = $"{now:yyyy-MM-dd} {match.Groups["time"].Value}";
        if (!DateTime.TryParse(dateAndTime, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return null;
        }

        var resetAt = new DateTimeOffset(parsed, now.Offset);
        return resetAt <= now ? resetAt.AddDays(1) : resetAt;
    }

    private static string JoinOutput(string? standardOutput, string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return standardError ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(standardError))
        {
            return standardOutput;
        }

        return standardOutput + Environment.NewLine + standardError;
    }

    private static string NormalizeAgentName(string agentName)
    {
        return agentName.Trim().ToLowerInvariant();
    }

    private static string Truncate(string value)
    {
        const int maxLength = 8000;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
