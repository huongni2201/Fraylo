namespace Fraylo.Contracts.Publishing;

public sealed record PublishVideoDto(
    string ProjectId, string ProjectName, string AssetId, string FileName, long ByteSize, string Caption, string CreatedAtUtc);

public sealed record PublishVideoRequest(
    string ClientRequestId, string ProjectId, string AssetId, string AccountId, string Caption, string? SoundName = null);

public sealed record PublishAttemptDto(
    string AttemptId, string ProjectId, string AssetId, string AccountId, string AccountIdentity,
    string Caption, string State, string Message, string? ExternalPostUrl,
    string CreatedAtUtc, string UpdatedAtUtc);

public sealed record PublishingListResponse(
    IReadOnlyList<PublishVideoDto> Videos, IReadOnlyList<PublishAttemptDto> Attempts);
