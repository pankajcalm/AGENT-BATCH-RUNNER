using System.IO;
using System.Text.Json;
using AgentBatchRunner.Agents;
using AgentBatchRunner.Gui.Models;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Services;

namespace AgentBatchRunner.Gui.Services;

public sealed class GuiSettingsStore
{
    public const int MaxRecentFiles = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public GuiSettingsStore(string? settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath) ? GetDefaultSettingsPath() : settingsPath;
    }

    public string SettingsPath { get; }

    public static string GetDefaultSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "AgentBatchRunner", "gui-settings.json");
    }

    public GuiSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new GuiSettings();
        }

        try
        {
            var json = Utf8File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<GuiSettings>(json, JsonOptions) ?? new GuiSettings();
            Normalize(settings, removeMissingRecentFiles: true);
            return settings;
        }
        catch
        {
            return new GuiSettings();
        }
    }

    public void Save(GuiSettings settings)
    {
        Normalize(settings, removeMissingRecentFiles: true);
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        Utf8File.WriteAllText(SettingsPath, json);
    }

    public void AddRecentFile(GuiSettings settings, string filePath)
    {
        if (!TryNormalizeExistingFile(filePath, out var normalizedPath))
        {
            return;
        }

        settings.LastPromptFilePath = normalizedPath;
        settings.RecentPromptFiles.RemoveAll(path => string.Equals(path, normalizedPath, StringComparison.OrdinalIgnoreCase));
        settings.RecentPromptFiles.Insert(0, normalizedPath);
        Normalize(settings, removeMissingRecentFiles: true);
    }

    public void ClearRecentFiles(GuiSettings settings)
    {
        settings.RecentPromptFiles.Clear();
    }

    private static void Normalize(GuiSettings settings, bool removeMissingRecentFiles)
    {
        var selectedOverride = AgentRoutingMode.ToOverride(settings.LastSelectedAgent);
        settings.LastSelectedAgent = selectedOverride is null ||
                                     AgentAdapterFactory.IsSupportedAgent(selectedOverride)
            ? AgentRoutingMode.FromOverride(selectedOverride)
            : AgentRoutingMode.FromYaml;

        if (!TryNormalizeExistingFile(settings.LastPromptFilePath, out var normalizedLastPrompt))
        {
            settings.LastPromptFilePath = null;
        }
        else
        {
            settings.LastPromptFilePath = normalizedLastPrompt;
        }

        var normalizedRecentFiles = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in settings.RecentPromptFiles)
        {
            if (!TryNormalizePath(path, out var normalizedPath))
            {
                continue;
            }

            if (removeMissingRecentFiles && !File.Exists(normalizedPath))
            {
                continue;
            }

            if (seen.Add(normalizedPath))
            {
                normalizedRecentFiles.Add(normalizedPath);
            }

            if (normalizedRecentFiles.Count == MaxRecentFiles)
            {
                break;
            }
        }

        settings.RecentPromptFiles = normalizedRecentFiles;
    }

    private static bool TryNormalizeExistingFile(string? filePath, out string normalizedPath)
    {
        if (!TryNormalizePath(filePath, out normalizedPath))
        {
            return false;
        }

        return File.Exists(normalizedPath);
    }

    private static bool TryNormalizePath(string? filePath, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(filePath);
            return true;
        }
        catch
        {
            normalizedPath = string.Empty;
            return false;
        }
    }
}
