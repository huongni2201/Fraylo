using System.Text.Json;
using Fraylo.Contracts.Analytics;
using Fraylo.Contracts.Jobs;
using Fraylo.Application.Jobs;

namespace Fraylo.Application.Analytics;

internal static class AnalyticsAnalysisResultParser
{
    internal const string InvalidResult = "InvalidAnalysisResult";

    internal static JobExecutionResult Validate(JobExecutionContext context, JobExecutionResult result)
    {
        if (!result.Success || context.TaskType != JobTaskType.AnalyticsAnalysis) return result;
        return Parse(result.ResultText, context.ConfigSnapshotJson) is not null
            ? result
            : result with { Success = false, ReasonCode = InvalidResult };
    }

    internal static AnalyticsAnalysisResult? Parse(string? response, string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(response) || response.Length > 20000 || string.IsNullOrEmpty(inputJson)) return null;
        var raw = response.Trim();
        if (raw.StartsWith("```", StringComparison.Ordinal) && raw.EndsWith("```", StringComparison.Ordinal))
        {
            var newline = raw.IndexOf('\n');
            if (newline < 0 || newline >= raw.Length - 3) return null;
            raw = raw[(newline + 1)..^3].Trim();
        }
        try
        {
            var input = JsonSerializer.Deserialize<AnalyticsAnalysisInputBuilder.Input>(inputJson, AnalyticsAnalysisInputBuilder.Json);
            var result = JsonSerializer.Deserialize<AnalyticsAnalysisResult>(raw, AnalyticsAnalysisInputBuilder.Json);
            if (input is null || result is null || result.SchemaVersion != 1 || !ValidObservation(result.Summary) ||
                result.Limitations is not { Length: >= 1 and <= 16 } || result.Limitations.Any(s => !ValidText(s))) return null;
            var evidence = input.Totals.Select(t => t.Id).Concat(input.Sample.Where(r => r.Metrics.Values.Any(m => m.Value is not null)).Select(r => r.Id)).ToHashSet();
            return ValidItems(result.Highlights, 0, 6, evidence, ValidObservation) &&
                ValidItems(result.Improvements, 0, 6, evidence, ValidObservation) &&
                ValidItems(result.Recommendations, 3, 5, evidence, ValidText) ? result : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool ValidText(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 1000 &&
        !text.Contains("://", StringComparison.Ordinal) && !text.Contains("www.", StringComparison.OrdinalIgnoreCase);

    private static bool ValidObservation(string? text) => ValidText(text) && !text!.Any(char.IsDigit);

    private static bool ValidItems(AnalyticsAnalysisFinding[]? items, int min, int max, HashSet<string> evidence, Func<string?, bool> validText) =>
        items is not null && items.Length >= min && items.Length <= max && items.All(item =>
            item is not null && validText(item.Text) && item.Evidence is { Length: >= 1 and <= 4 } &&
            item.Evidence.All(evidence.Contains));
}
