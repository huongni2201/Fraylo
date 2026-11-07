using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fraylo.Application.Jobs;
using Fraylo.Contracts.Jobs;

namespace Fraylo.Application.Facebook;

public static class FacebookContentRewriteResultParser
{
    public const string InvalidResult = "InvalidRewriteResult";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Input(int SchemaVersion, string BatchId, string Source, string[] Slots, int MinLength, int MaxLength);
    public sealed record Variant(string SlotId, string Text);
    public sealed record Output(int SchemaVersion, Variant[] Variants);

    public static int Length(string value) => value.EnumerateRunes().Count();

    public static string Normalize(string value) => Regex.Replace(Regex.Replace(value.Normalize(NormalizationForm.FormKC), @"\p{Cf}", ""), @"\s+", " ").Trim().ToUpperInvariant();

    // Keep exact numeric tokens and URLs; names and meaning still require prompt instructions and human review.
    private static string[] Literals(string value) => Regex.Matches(value,
        "https?://[^\\s<>\"']+|www\\.[^\\s<>\"']+|[\\p{L}\\p{N}]*[0-9][\\p{L}\\p{N}.,%/+()_-]*",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();

    public static JobExecutionResult Validate(JobExecutionContext context, JobExecutionResult result) =>
        !result.Success || context.TaskType != JobTaskType.FacebookContentRewrite || Parse(result.ResultText, context.ConfigSnapshotJson) is not null
            ? result : result with { Success = false, ReasonCode = InvalidResult };

    public static Output? Parse(string? response, string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(response) || response.Length > 120000 || string.IsNullOrWhiteSpace(inputJson)) return null;
        try
        {
            var input = JsonSerializer.Deserialize<Input>(inputJson, Json);
            if (input is not { SchemaVersion: 1, Slots.Length: >= 1 and <= 10 } || string.IsNullOrWhiteSpace(input.Source) ||
                input.Slots.Distinct(StringComparer.Ordinal).Count() != input.Slots.Length) return null;
            var raw = response.Trim();
            if (raw.StartsWith("```", StringComparison.Ordinal) && raw.EndsWith("```", StringComparison.Ordinal))
            {
                var newline = raw.IndexOf('\n');
                if (newline < 0 || newline >= raw.Length - 3) return null;
                raw = raw[(newline + 1)..^3].Trim();
            }
            var output = JsonSerializer.Deserialize<Output>(raw, Json);
            if (output is not { SchemaVersion: 1, Variants: not null } || output.Variants.Length != input.Slots.Length) return null;
            var slots = input.Slots.ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal) { Normalize(input.Source) };
            var literals = Literals(input.Source);
            foreach (var variant in output.Variants)
            {
                if (variant is null || !slots.Remove(variant.SlotId) || string.IsNullOrWhiteSpace(variant.Text) ||
                    variant.Text.Any(c => (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) || c != '\u200d' && char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) ||
                    Length(variant.Text) < input.MinLength || Length(variant.Text) > input.MaxLength || Length(variant.Text) > 10000 ||
                    !seen.Add(Normalize(variant.Text)) || !literals.SequenceEqual(Literals(variant.Text), StringComparer.Ordinal)) return null;
            }
            return slots.Count == 0 ? output : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or RegexMatchTimeoutException) { return null; }
    }
}
