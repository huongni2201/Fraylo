using System.Text.Json;
using Fraylo.Contracts.Jobs;

namespace Fraylo.Application.Content.Workflows;

internal static class ReferenceMotionAnalysis
{
    internal static bool TryRead(string? result, JobMediaSnapshotDto media, out string prompt)
    {
        prompt = "";
        if (string.IsNullOrWhiteSpace(result) || result.Length > 16000 ||
            media.ReferenceVideoDurationSeconds is not double sourceDuration)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _) ||
                !root.TryGetProperty("beats", out var beats) || beats.ValueKind != JsonValueKind.Array ||
                beats.GetArrayLength() == 0 ||
                !root.TryGetProperty("renderPrompt", out var renderPrompt) ||
                renderPrompt.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(renderPrompt.GetString()))
                return false;

            double previousSourceEnd = 0, previousTargetEnd = 0;
            foreach (var beat in beats.EnumerateArray())
            {
                if (beat.ValueKind != JsonValueKind.Object ||
                    !beat.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(action.GetString()) ||
                    !ValidRange(beat, "sourceStart", "sourceEnd", sourceDuration + .25, ref previousSourceEnd) ||
                    !ValidRange(beat, "targetStart", "targetEnd", media.DurationSeconds, ref previousTargetEnd))
                    return false;
            }
            prompt = renderPrompt.GetString()!.Trim();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidRange(JsonElement beat, string startKey, string endKey, double limit, ref double previousEnd)
    {
        if (!beat.TryGetProperty(startKey, out var start) || start.ValueKind != JsonValueKind.Number ||
            !beat.TryGetProperty(endKey, out var end) || end.ValueKind != JsonValueKind.Number ||
            !start.TryGetDouble(out var first) || !end.TryGetDouble(out var last) ||
            first < previousEnd || last <= first || last > limit)
            return false;
        previousEnd = last;
        return true;
    }
}
