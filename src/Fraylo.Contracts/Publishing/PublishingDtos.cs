namespace Fraylo.Contracts.Publishing;

public sealed record PublishVideoDto(
    string ProjectId, string ProjectName, string AssetId, string FileName, long ByteSize, string Caption, string CreatedAtUtc);

public sealed record PublishVideoRequest(
    string ClientRequestId, string ProjectId, string AssetId, string AccountId, string Caption, string? SoundName = null, string? FavoriteSoundKey = null);

public sealed record PublishAttemptDto(
    string AttemptId, string ProjectId, string AssetId, string AccountId, string AccountIdentity,
    string Caption, string State, string Message, string? ExternalPostUrl,
    string CreatedAtUtc, string UpdatedAtUtc);

public sealed record PublishingListResponse(
    IReadOnlyList<PublishVideoDto> Videos, IReadOnlyList<PublishAttemptDto> Attempts);

public sealed record FavoriteSoundsRequest(string ProjectId, string AssetId, string AccountId);
public sealed record FavoriteSoundDto(string Key, string Title, string Author, string Duration);
public sealed record FavoriteSoundsResponse(string AccountId, IReadOnlyList<FavoriteSoundDto> Sounds, bool IsComplete);