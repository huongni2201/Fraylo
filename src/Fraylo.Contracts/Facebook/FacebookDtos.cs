namespace Fraylo.Contracts.Facebook;

public sealed record FacebookAccountDto(string AccountId, string DisplayName, string? AccountIdentity, string SessionStatus, bool Enabled = true);
public sealed record FacebookGroupDto(string TargetId, string AccountId, string Name, string Url);
public sealed record FacebookTaskDto(string TaskId, string BatchId, string AccountId, string TargetId,
    string GroupName, string GroupUrl, string Text, string State, string Message,
    string CreatedAtUtc, string UpdatedAtUtc, string? SubmittedAtUtc);
public sealed record FacebookListResponse(IReadOnlyList<FacebookAccountDto> Accounts,
    IReadOnlyList<FacebookGroupDto> Groups, IReadOnlyList<FacebookTaskDto> Tasks);
public sealed record FacebookSyncRequest(string AccountId);
public sealed record FacebookSyncResponse(IReadOnlyList<FacebookGroupDto> Groups, bool IsComplete, string Message);
public sealed record FacebookCreateBatchRequest(string BatchRequestId, string AccountId,
    IReadOnlyList<string> TargetIds, string Text, IReadOnlyList<string> ImageAssetIds);
public sealed record FacebookTaskRequest(string TaskId);
