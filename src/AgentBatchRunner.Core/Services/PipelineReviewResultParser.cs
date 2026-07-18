using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBatchRunner.Infrastructure;
using AgentBatchRunner.Models;

namespace AgentBatchRunner.Services;

public sealed class PipelineReviewResultParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public bool TryParse(string output, out PipelineReviewResult result, out string error)
    {
        result = new PipelineReviewResult { ReviewVerdict = PipelineReviewVerdict.ReviewFailed };
        if (!StructuredJsonExtractor.TryExtractObject(output, "reviewVerdict", out var json))
        {
            error = "Review output did not contain a JSON object with reviewVerdict.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var schemaVersion = GetString(root, "schemaVersion");
            var verdictText = GetString(root, "reviewVerdict");
            if (!Enum.TryParse<PipelineReviewVerdict>(
                    verdictText?.Replace(" ", string.Empty, StringComparison.Ordinal),
                    true,
                    out var verdict))
            {
                error = $"Review output has unsupported reviewVerdict '{verdictText}'.";
                return false;
            }

            result = JsonSerializer.Deserialize<PipelineReviewResult>(json, JsonOptions)
                     ?? new PipelineReviewResult();
            result.ReviewVerdict = verdict;
            result.Findings ??= [];
            result.RequiredDecisions ??= [];
            if (!string.Equals(schemaVersion, "1.0", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(result.SourcePipelineFile) ||
                string.IsNullOrWhiteSpace(result.ExecutionRunId) ||
                string.IsNullOrWhiteSpace(result.ExecutionStatus) ||
                string.IsNullOrWhiteSpace(result.Summary))
            {
                error = "Review JSON is missing schemaVersion, sourcePipelineFile, executionRunId, executionStatus, or summary.";
                return false;
            }

            if (result.ReviewVerdict == PipelineReviewVerdict.Approved && !result.GateApproved)
            {
                error = "Approved review JSON must set gateApproved to true.";
                return false;
            }

            if (result.ReviewVerdict is (PipelineReviewVerdict.Approved or PipelineReviewVerdict.ApprovedWithWarnings) &&
                result.Findings.Any(finding => finding.Severity is PipelineFindingSeverity.High or PipelineFindingSeverity.Critical))
            {
                error = $"{result.ReviewVerdict} review JSON cannot contain unresolved high or critical findings.";
                return false;
            }

            if (result.ReviewVerdict is (PipelineReviewVerdict.Blocked or PipelineReviewVerdict.NeedsHumanDecision) &&
                result.CanAutoAdvance)
            {
                error = $"{result.ReviewVerdict} review JSON cannot set canAutoAdvance to true.";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Review JSON is invalid: {ex.Message}";
            return false;
        }
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }

        return null;
    }
}
