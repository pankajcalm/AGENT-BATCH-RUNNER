namespace AgentBatchRunner.Agents;

public static class CodexSessionParser
{
    public static string? TryExtractSessionId(string output)
    {
        return JsonSessionParser.TryExtract(output, "session_id", "sessionId", "conversation_id", "conversationId");
    }
}
