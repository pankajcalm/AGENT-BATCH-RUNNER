using AgentBatchRunner.Agents;

namespace AgentBatchRunner.Tests;

public sealed class SessionParserTests
{
    [Fact]
    public void Claude_ExtractsSessionIdFromJsonObject()
    {
        var output = """{ "type": "result", "session_id": "abc-123", "result": "done" }""";

        Assert.Equal("abc-123", ClaudeSessionParser.TryExtractSessionId(output));
    }

    [Fact]
    public void Claude_ExtractsSessionIdFromStreamingJsonLines()
    {
        var output = string.Join(
            Environment.NewLine,
            """{ "type": "system" }""",
            "not json at all",
            """{ "type": "result", "sessionId": "line-456" }""");

        Assert.Equal("line-456", ClaudeSessionParser.TryExtractSessionId(output));
    }

    [Fact]
    public void Claude_ExtractsSessionIdFromNestedObject()
    {
        var output = """{ "meta": { "session": { "session_id": "nested-789" } } }""";

        Assert.Equal("nested-789", ClaudeSessionParser.TryExtractSessionId(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plain text, no json")]
    [InlineData("""{ "result": "no session here" }""")]
    public void Claude_ReturnsNullWhenNoSessionId(string output)
    {
        Assert.Null(ClaudeSessionParser.TryExtractSessionId(output));
    }

    [Fact]
    public void Codex_ExtractsConversationIdAlias()
    {
        var output = """{ "conversation_id": "conv-001" }""";

        Assert.Equal("conv-001", CodexSessionParser.TryExtractSessionId(output));
    }
}
