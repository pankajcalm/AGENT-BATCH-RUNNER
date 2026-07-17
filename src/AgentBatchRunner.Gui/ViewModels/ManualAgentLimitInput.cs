namespace AgentBatchRunner.Gui.ViewModels;

/// <summary>
/// Result of the "Set Agent Limit Manually" dialog: which agent to block, until when, and why.
/// </summary>
public sealed record ManualAgentLimitInput(string Agent, DateTimeOffset BlockedUntil, string Reason);
