namespace Fraylo.Contracts.Facebook;

public sealed record FacebookAccountDto(string AccountId, string DisplayName, string? AccountIdentity, string SessionStatus, bool Enabled = true);
public sealed record FacebookGroupDto(string TargetId, string AccountId, string Name, string Url);
public sealed record FacebookActivityEntry(string AtUtc, string Code, string Message, string? ReasonCode = null);
public sealed record FacebookTaskDto(string TaskId, string BatchId, string AccountId, string TargetId,
    string GroupName, string GroupUrl, string Text, string State, string Message,
    string CreatedAtUtc, string UpdatedAtUtc, string? SubmittedAtUtc,
    string? ReasonCode = null, string? DueAtUtc = null, IReadOnlyList<string>? ImageAssetIds = null, IReadOnlyList<FacebookActivityEntry>? ActivityLog = null);
public sealed record FacebookDraftDto(string DraftId, string Name, string AccountId, string Text,
    IReadOnlyList<string> TargetIds, IReadOnlyList<string> ImageAssetIds, string UpdatedAtUtc, string? DueAtUtc = null, bool RewriteContent = true);
public sealed record FacebookListResponse(IReadOnlyList<FacebookAccountDto> Accounts,
    IReadOnlyList<FacebookGroupDto> Groups, IReadOnlyList<FacebookTaskDto> Tasks,
    IReadOnlyList<FacebookDraftDto>? Drafts = null, IReadOnlyList<FacebookPostDto>? Posts = null, string? RewriteProvider = null);
public sealed record FacebookSyncRequest(string AccountId);
public sealed record FacebookSyncResponse(IReadOnlyList<FacebookGroupDto> Groups, bool IsComplete, string Message);
public sealed record FacebookCreateBatchRequest(string BatchRequestId, string AccountId,
    IReadOnlyList<string> TargetIds, string Text, IReadOnlyList<string> ImageAssetIds, string? DueAtUtc = null, string? Name = null, string TargetKind = "Group", bool RewriteContent = false);
public sealed record FacebookTaskRequest(string TaskId);
public sealed record FacebookRescheduleRequest(string TaskId, string DueAtUtc);
public sealed record FacebookSaveDraftRequest(string? DraftId, string Name, string AccountId, string Text,
    IReadOnlyList<string> TargetIds, IReadOnlyList<string> ImageAssetIds, string? DueAtUtc = null, bool RewriteContent = true);
public sealed record FacebookDraftRequest(string DraftId);

public sealed record FacebookPostDto(string BatchId, string Name, string AccountId, bool Paused, long Revision,
    string CreatedAtUtc, IReadOnlyList<FacebookTaskDto> Tasks, string? OriginalText = null,
    IReadOnlyList<FacebookRewriteChunkDto>? RewriteChunks = null);
public sealed record FacebookRewriteChunkDto(string ChunkId, int Ordinal, int Count, string State, string? Reason,
    bool Applied, bool CanRetry, bool Uncertain, string? JobId = null);
public sealed record FacebookRewriteRequest(string BatchId, string ChunkId, long Revision);
public sealed record FacebookPostActionRequest(string BatchId, long Revision, string Action, string? DueAtUtc = null);
