using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class PipelineFolderDiscovery(PromptFileLoader promptFileLoader)
{
    public async Task<PipelineFolderDiscoveryResult> DiscoverAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Pipeline folder path is required.", nameof(folderPath));
        }

        var fullFolderPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullFolderPath))
        {
            throw new DirectoryNotFoundException($"Pipeline folder was not found: {fullFolderPath}");
        }

        var result = new PipelineFolderDiscoveryResult
        {
            FolderPath = fullFolderPath,
            DiscoveredAt = DateTimeOffset.Now
        };

        foreach (var path in Directory.EnumerateFiles(fullFolderPath, "*.*", SearchOption.AllDirectories)
                     .Where(IsYaml)
                     .Where(path => IsEligibleSourcePath(fullFolderPath, path))
                     .OrderBy(path => Path.GetRelativePath(fullFolderPath, path), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var config = await promptFileLoader.LoadAsync(path, cancellationToken);
                if (config.Pipeline is { Enabled: false })
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(fullFolderPath, path);
                result.Files.Add(new PipelineFile
                {
                    FilePath = Path.GetFullPath(path),
                    RelativePath = relativePath,
                    FileName = Path.GetFileName(path),
                    Config = config
                });

                var batchValidation = promptFileLoader.Validate(config);
                foreach (var error in batchValidation.Errors)
                {
                    result.Errors.Add($"{relativePath}: {error}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                result.Errors.Add($"{Path.GetRelativePath(fullFolderPath, path)}: {ex.Message}");
            }
        }

        if (result.Files.Count == 0 && result.Errors.Count == 0)
        {
            result.Warnings.Add("No eligible YAML pipeline files were found.");
        }

        return result;
    }

    public static bool IsEligibleSourcePath(string folderPath, string filePath)
    {
        var relativePath = Path.GetRelativePath(folderPath, filePath);
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => string.Equals(segment, ".agentbatchrunner", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        if (fileName.StartsWith('_'))
        {
            return false;
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return !withoutExtension.EndsWith(".review", StringComparison.OrdinalIgnoreCase) &&
               !System.Text.RegularExpressions.Regex.IsMatch(
                   withoutExtension,
                   @"\.review-R\d+$",
                   System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                   System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static bool IsYaml(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase);
    }
}
