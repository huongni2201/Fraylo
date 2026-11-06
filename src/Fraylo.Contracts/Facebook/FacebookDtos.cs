namespace Fraylo.Contracts.Facebook;

public sealed record FacebookAccountDto(string AccountId, string DisplayName, string? AccountIdentity, string SessionStatus, bool Enabled = true);
public sealed record FacebookGroupDto(string TargetId, string AccountId, string Name, string Url);
public sealed record FacebookTaskDto(string TaskId, string BatchId, string AccountId, string TargetId,
    string GroupName, string GroupUrl, string Text, string State, string Message,
    string CreatedAtUtc, string UpdatedAtUtc, string? SubmittedAtUtc,
    string? ReasonCode = null, string? DueAtUtc = null, IReadOnlyList<string>? ImageAssetIds = null);
public sealed record FacebookDraftDto(string DraftId, string Name, string AccountId, string Text,
    IReadOnlyList<string> TargetIds, IReadOnlyList<string> ImageAssetIds, string UpdatedAtUtc);
public sealed record FacebookListResponse(IReadOnlyList<FacebookAccountDto> Accounts,
    IReadOnlyList<FacebookGroupDto> Groups, IReadOnlyList<FacebookTaskDto> Tasks,
    IReadOnlyList<FacebookDraftDto>? Drafts = null);
public sealed record FacebookSyncRequest(string AccountId);
public sealed record FacebookSyncResponse(IReadOnlyList<FacebookGroupDto> Groups, bool IsComplete, string Message);
public sealed record FacebookCreateBatchRequest(string BatchRequestId, string AccountId,
    IReadOnlyList<string> TargetIds, string Text, IReadOnlyList<string> ImageAssetIds, string? DueAtUtc = null);
public sealed record FacebookTaskRequest(string TaskId);
public sealed record FacebookRescheduleRequest(string TaskId, string DueAtUtc);
public sealed record FacebookSaveDraftRequest(string? DraftId, string Name, string AccountId, string Text,
    IReadOnlyList<string> TargetIds, IReadOnlyList<string> ImageAssetIds);
public sealed record FacebookDraftRequest(string DraftId);
