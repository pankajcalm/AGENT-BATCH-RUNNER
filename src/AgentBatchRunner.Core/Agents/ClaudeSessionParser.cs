namespace AgentBatchRunner.Agents;

public static class ClaudeSessionParser
{
    public static string? TryExtractSessionId(string output)
    {
        return JsonSessionParser.TryExtract(output, "session_id", "sessionId");
    }
}
