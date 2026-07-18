using AgentBatchRunner.Models;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Tests;

public sealed class AgentOutcomeParserTests
{
    [Fact]
    public void Parse_ReadsJsonOutcomeFromMixedAgentOutput()
    {
        const string output =
            "Work completed partially.\n" +
            "{\"agentOutcome\":\"Blocked\",\"blockerCode\":\"PHASE_PREREQUISITE_MISSING\",\"blocker\":\"Approval missing.\",\"recommendedNext\":\"repair.yaml\"}";

        var result = new AgentOutcomeParser().Parse(output);

        Assert.NotNull(result);
        Assert.Equal(AgentOutcome.Blocked, result.AgentOutcome);
        Assert.Equal("PHASE_PREREQUISITE_MISSING", result.BlockerCode);
        Assert.Equal("Approval missing.", result.Blocker);
        Assert.Equal("repair.yaml", result.RecommendedNext);
        Assert.True(result.StopsWithoutRetry);
    }

    [Fact]
    public void Parse_ReadsStructuredFooterCaseInsensitively()
    {
        const string output =
            "AGENT_OUTCOME: NeedsHumanDecision\n" +
            "BLOCKER_CODE: OWNER_DECISION\n" +
            "BLOCKER: Select publication policy.\n" +
            "RECOMMENDED_NEXT: 12_repair.yaml";

        var result = new AgentOutcomeParser().Parse(output);

        Assert.NotNull(result);
        Assert.Equal(AgentOutcome.NeedsHumanDecision, result.AgentOutcome);
        Assert.Equal("OWNER_DECISION", result.BlockerCode);
    }

    [Fact]
    public void Parse_IgnoresUnrelatedJson()
    {
        Assert.Null(new AgentOutcomeParser().Parse("{\"status\":\"Blocked\"}"));
    }
}
