namespace AgentBatchRunner.Gui.Models;

public sealed class GuiSettings
{
    public string? LastPromptFilePath { get; set; }

    public string LastSelectedAgent { get; set; } = AgentBatchRunner.Services.AgentRoutingMode.FromYaml;

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public List<string> RecentPromptFiles { get; set; } = [];
}
