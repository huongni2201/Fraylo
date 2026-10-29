using System;
using System.Collections.Generic;
using System.Linq;

namespace TiToAI.Contracts.Jobs;

public static class JobTaskType
{
    public const string Script = "Script";
    public const string Image = "Image";
    public const string VideoPrompt = "VideoPrompt";
    public const string VideoRender = "VideoRender";

    public static readonly string[] All = [Script, Image, VideoPrompt, VideoRender];
    public static bool IsValid(string type) => All.Contains(type, StringComparer.OrdinalIgnoreCase);
}

public static class JobState
{
    public const string Queued = "Queued";
    public const string Reserved = "Reserved";
    public const string Running = "Running";
    public const string AwaitingUser = "AwaitingUser";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";

    public static readonly string[] ActiveStates = [Queued, Reserved, Running, AwaitingUser];
    public static readonly string[] TerminalStates = [Succeeded, Failed, Canceled];
    public static bool IsTerminal(string state) => TerminalStates.Contains(state, StringComparer.OrdinalIgnoreCase);
}

public static class JobReasonCode
{
    public const string LoginRequired = "login_required";
    public const string SessionExpired = "session_expired";
    public const string WrongAccount = "wrong_account";
    public const string AutomationBlocked = "automation_blocked";
    public const string NetworkError = "network_error";
    public const string AuthClosed = "auth_closed";
    public const string ReconcileRequired = "reconcile_required";
    public const string MaxAttemptsExceeded = "max_attempts_exceeded";
    public const string LeaseExpiredMaxAttempts = "lease_expired_max_attempts";
    public const string StorageRootUnavailable = "storage_root_unavailable";
    public const string ProviderRejected = "provider_rejected";
    public const string UserCanceled = "user_canceled";
    public const string CanceledLateResult = "canceled_late_result";
    public const string TabCapacityUnavailable = "tab_capacity_unavailable";
    public const string WaitingForCapacity = "waiting_for_capacity";
    public const string WaitingForReconcile = "waiting_for_reconcile";
    public const string GlobalJobBudgetReached = "global_job_budget_reached";
    public const string LeaseExpiredRetry = "lease_expired_retry";
    public const string ReconcileRejected = "reconcile_rejected";
    public const string RenderTimeout = "render_timeout";
}

public sealed record JobSummaryDto(
    string Id,
    string ProjectId,
    string? SceneId,
    string TaskType,
    string State,
    string? ReasonCode,
    int Attempts,
    int? ProgressPercent,
    string? CurrentStep,
    string CreatedAtUtc,
    int? MaxAttempts = null,
    int? SideEffectSubmitted = null,
    bool? CancelRequested = null,
    bool? HasUnresolvedSideEffect = null,
    string? ProfileId = null,
    string? ResultReference = null,
    long Revision = 1,
    string? ResultText = null
);

public sealed record JobDetailDto(
    string Id,
    string ProjectId,
    string? SceneId,
    string TaskType,
    string State,
    string? ReasonCode,
    string ProviderKey,
    string StorageRootId,
    int Attempts,
    int MaxAttempts,
    int SideEffectSubmitted,
    string? ResultReference,
    string? ResultText,
    bool CancelRequested,
    string CreatedAtUtc,
    string UpdatedAtUtc,
    string? ProfileId = null,
    bool? HasUnresolvedSideEffect = null,
    long Revision = 1,
    IReadOnlyList<JobActivityEntryDto>? Activity = null
);

public sealed record JobActivityEntryDto(string AtUtc, string Step, string Message);

/// <summary>
/// Bản chụp bất biến của các tệp media và cấu hình tùy chọn được đóng băng tại thời điểm Enqueue theo PV-R6.
/// </summary>
public sealed record JobMediaSnapshotDto(
    string? ModelAssetId = null,
    string? ModelAssetHash = null,
    string? ProductAssetId = null,
    string? ProductAssetHash = null,
    string? ReadyImageAssetId = null,
    string? ReadyImageAssetHash = null,
    string? SourceImageAssetId = null,
    string? SourceImageAssetHash = null,
    string? AudioAssetId = null,
    string? AudioAssetHash = null,
    string AudioOption = "None",
    int DurationSeconds = 15,
    string? InputRevision = null,
    IReadOnlyDictionary<string, string>? MediaRoles = null,
    IReadOnlyList<string>? ImageAssetIds = null
);

public sealed record JobEnqueueRequest(
    string ProjectId,
    string? SceneId,
    string TaskType,
    string? PromptTemplateId = null,
    IReadOnlyDictionary<string, string>? PromptVariables = null,
    string? CustomPromptText = null,
    JobMediaSnapshotDto? MediaSnapshot = null,
    string? WorkflowRunId = null,
    string? WorkflowStepKey = null
);

public sealed record JobEnqueueResponseData(
    string JobId,
    string State,
    string TaskType,
    string ProviderKey,
    string StorageRootId,
    string CreatedAtUtc,
    long Revision = 1
);

public sealed record JobGetStatusRequest(string JobId);
public sealed record JobGetStatusResponseData(JobDetailDto Job);

public sealed record JobListActiveRequest;
public sealed record JobListActiveResponseData(IReadOnlyList<JobSummaryDto> Jobs);

public sealed record JobListHistoryRequest(
    string? ProjectId = null,
    string? Cursor = null,
    int? PageSize = null
);
public sealed record JobListHistoryResponseData(
    IReadOnlyList<JobSummaryDto> Items,
    string? NextCursor
);

public sealed record JobCancelRequest(string JobId);
public sealed record JobCancelResponseData(
    string JobId,
    string State,
    bool CancelRequested,
    string Message,
    string? PreviousState = null,
    long Revision = 0
);

public sealed record JobResumeRequest(string JobId);
public sealed record JobResumeResponseData(
    string JobId,
    string State,
    string Message,
    long Revision = 0
);

public sealed record JobRetryRequest(
    string JobId,
    long ExpectedRevision
);
public sealed record JobRetryResponseData(
    string JobId,
    string State,
    long Revision,
    string Message
);

public sealed record JobReconcileRequest(
    string JobId,
    string Action,
    bool? ConfirmedNotReceived = null,
    string? ResultText = null,
    string? ResultReference = null
);
public sealed record JobReconcileResponseData(
    string JobId,
    string PreviousState,
    string CurrentState,
    string ReconcileResult,
    string? ResultReference,
    long Revision = 0
);

public sealed record JobRecoverOutputRequest(string JobId);

public sealed record JobProgressUpdatedPayload(
    string JobId,
    string ProjectId,
    string? SceneId,
    string State,
    int ProgressPercent,
    string CurrentStep,
    long JobRevision = 0
);

public sealed record JobStateChangedPayload(
    string JobId,
    string? ProjectId,
    string PreviousState,
    string NewState,
    string? ReasonCode,
    string? ResultReference,
    string TimestampUtc,
    long JobRevision = 0
);
