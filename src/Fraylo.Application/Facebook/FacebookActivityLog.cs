using System.Text.Json;
using Fraylo.Contracts.Facebook;

namespace Fraylo.Application.Facebook;

internal static class FacebookActivityLog
{
    public static FacebookActivityEntry[] Read(string json) =>
        JsonSerializer.Deserialize<FacebookActivityEntry[]>(json) ?? [];

    public static string Append(string json, string atUtc, string code, string message, string? reasonCode = null) =>
        JsonSerializer.Serialize(Read(json).TakeLast(49).Append(new(atUtc, code, message, reasonCode)).ToArray());
}
