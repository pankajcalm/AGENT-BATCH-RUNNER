using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class PipelinePlanBuilder
{
    private static readonly HashSet<string> TerminalNextRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "manual",
        "stop"
    };

    public PipelinePlan Build(PipelineFolderDiscoveryResult discovery)
    {
        var plan = new PipelinePlan
        {
            FolderPath = discovery.FolderPath,
            Errors = [.. discovery.Errors],
            Warnings = [.. discovery.Warnings]
        };

        ValidateMetadata(discovery.Files, plan.Errors);
        var resolver = new PipelineReferenceResolver(discovery.Files, plan.Errors);
        var dependencies = BuildDependencies(discovery.Files, resolver, plan.Errors);
        ValidateNextReferences(discovery.Files, resolver, plan.Errors);

        var ordered = TopologicalSort(discovery.Files, dependencies, plan.Errors);
        plan.Files.AddRange(ordered);

        foreach (var file in discovery.Files.Where(file => file.IsLegacy))
        {
            plan.Warnings.Add(
                $"{file.RelativePath}: no pipeline metadata; filename ordering and manual next-file selection apply.");
        }

        return plan;
    }

    private static void ValidateMetadata(IReadOnlyCollection<PipelineFile> files, List<string> errors)
    {
        foreach (var file in files.Where(file => file.Metadata is not null))
        {
            if (string.IsNullOrWhiteSpace(file.Metadata!.Id))
            {
                errors.Add($"{file.RelativePath}: pipeline.id is required when pipeline metadata is present.");
            }

            if (file.Metadata.Order is < 0)
            {
                errors.Add($"{file.RelativePath}: pipeline.order must be zero or greater.");
            }

            if (file.Metadata.DependsOn.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"{file.RelativePath}: pipeline.dependsOn entries must not be blank.");
            }

            var reviewAgent = file.Metadata.Review.Agent;
            if (!string.IsNullOrWhiteSpace(reviewAgent) && !Agents.AgentAdapterFactory.IsSupportedAgent(reviewAgent))
            {
                errors.Add($"{file.RelativePath}: pipeline.review.agent '{reviewAgent}' is not supported.");
            }
        }

        foreach (var duplicate in files
                     .Where(file => !string.IsNullOrWhiteSpace(file.Metadata?.Id))
                     .GroupBy(file => file.Metadata!.Id, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            errors.Add(
                $"Duplicate pipeline id '{duplicate.Key}' is used by: " +
                string.Join(", ", duplicate.Select(file => file.RelativePath)) + ".");
        }

        foreach (var duplicate in files
                     .Where(file => file.Order.HasValue)
                     .GroupBy(file => file.Order!.Value)
                     .Where(group => group.Count() > 1))
        {
            errors.Add(
                $"Duplicate pipeline order '{duplicate.Key}' is used by: " +
                string.Join(", ", duplicate.Select(file => file.RelativePath)) + ".");
        }
    }

    private static Dictionary<PipelineFile, List<PipelineFile>> BuildDependencies(
        IReadOnlyCollection<PipelineFile> files,
        PipelineReferenceResolver resolver,
        List<string> errors)
    {
        var dependencies = files.ToDictionary(file => file, _ => new List<PipelineFile>());
        foreach (var file in files)
        {
            foreach (var reference in file.Dependencies)
            {
                var dependency = resolver.Resolve(reference, $"{file.RelativePath}: dependency", errors);
                if (dependency is null)
                {
                    continue;
                }

                if (ReferenceEquals(file, dependency))
                {
                    errors.Add($"{file.RelativePath}: pipeline dependency points to itself.");
                    continue;
                }

                dependencies[file].Add(dependency);
            }
        }

        return dependencies;
    }

    private static void ValidateNextReferences(
        IEnumerable<PipelineFile> files,
        PipelineReferenceResolver resolver,
        List<string> errors)
    {
        foreach (var file in files.Where(file => file.Metadata is not null))
        {
            foreach (var rule in EnumerateNextRules(file.Metadata!.Next))
            {
                if (string.IsNullOrWhiteSpace(rule.Value) || TerminalNextRules.Contains(rule.Value))
                {
                    continue;
                }

                var target = resolver.Resolve(rule.Value, $"{file.RelativePath}: pipeline.next.{rule.Name}", errors);
                if (ReferenceEquals(file, target))
                {
                    errors.Add($"{file.RelativePath}: pipeline.next.{rule.Name} points to itself.");
                }
            }
        }
    }

    private static IReadOnlyList<PipelineFile> TopologicalSort(
        IReadOnlyCollection<PipelineFile> files,
        IReadOnlyDictionary<PipelineFile, List<PipelineFile>> dependencies,
        List<string> errors)
    {
        var inDegree = files.ToDictionary(file => file, file => dependencies[file].Distinct().Count());
        var dependents = files.ToDictionary(file => file, _ => new List<PipelineFile>());
        foreach (var (file, requiredFiles) in dependencies)
        {
            foreach (var required in requiredFiles.Distinct())
            {
                dependents[required].Add(file);
            }
        }

        var comparer = Comparer<PipelineFile>.Create((left, right) =>
        {
            var orderComparison = (left.Order ?? int.MaxValue).CompareTo(right.Order ?? int.MaxValue);
            if (orderComparison != 0)
            {
                return orderComparison;
            }

            var pathComparison = StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath);
            return pathComparison != 0
                ? pathComparison
                : StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
        });
        var ready = new SortedSet<PipelineFile>(files.Where(file => inDegree[file] == 0), comparer);
        var ordered = new List<PipelineFile>(files.Count);

        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            ordered.Add(next);

            foreach (var dependent in dependents[next])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (ordered.Count != files.Count)
        {
            var cyclicFiles = files.Except(ordered)
                .Select(file => file.RelativePath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            errors.Add("Circular pipeline dependencies detected among: " + string.Join(", ", cyclicFiles) + ".");
            ordered.AddRange(files.Except(ordered).OrderBy(file => file, comparer));
        }

        return ordered;
    }

    private static IEnumerable<(string Name, string? Value)> EnumerateNextRules(PipelineNextMetadata next)
    {
        yield return ("onApproved", next.OnApproved);
        yield return ("onApprovedWithWarnings", next.OnApprovedWithWarnings);
        yield return ("onBlocked", next.OnBlocked);
        yield return ("onNeedsHumanDecision", next.OnNeedsHumanDecision);
        yield return ("onPrerequisiteMissing", next.OnPrerequisiteMissing);
        yield return ("onReviewFailed", next.OnReviewFailed);
        yield return ("onCanceled", next.OnCanceled);
        yield return ("onRateLimited", next.OnRateLimited);
    }

    private sealed class PipelineReferenceResolver
    {
        private readonly IReadOnlyCollection<PipelineFile> _files;

        public PipelineReferenceResolver(IReadOnlyCollection<PipelineFile> files, List<string> errors)
        {
            _files = files;
            foreach (var duplicate in files
                         .GroupBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                errors.Add(
                    $"Duplicate pipeline filename '{duplicate.Key}' requires relative-path references: " +
                    string.Join(", ", duplicate.Select(file => file.RelativePath)) + ".");
            }
        }

        public PipelineFile? Resolve(string reference, string context, List<string> errors)
        {
            var normalized = reference.Trim().Replace('/', Path.DirectorySeparatorChar);
            var matches = _files.Where(file =>
                    string.Equals(file.PipelineId, reference.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(file.FileName, reference.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(file.RelativePath, normalized, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            errors.Add(matches.Count == 0
                ? $"{context} '{reference}' was not found."
                : $"{context} '{reference}' is ambiguous.");
            return null;
        }
    }
}
