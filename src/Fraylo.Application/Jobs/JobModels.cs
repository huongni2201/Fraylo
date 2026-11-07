using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Fraylo.Application.Jobs;

/// <summary>
/// Tín hiệu đánh thức gửi tới JobScheduler thông qua In-memory Channel
/// khi có sự kiện enqueue mới, hoàn tất đăng nhập browser hoặc giải phóng account slot.
/// </summary>
public sealed record JobWakeupSignal(
    string Source,
    string? JobId = null,
    string? ProfileId = null
);

/// <summary>
/// Giao diện phát tín hiệu đánh thức tới Scheduler.
/// </summary>
public interface IJobWakeupNotifier
{
    void NotifyWakeup(JobWakeupSignal signal);
}

/// <summary>
/// Kết quả chụp bản sao bất biến của các tham số đầu vào khi Enqueue.
/// </summary>
public sealed record JobSnapshotResult(
    string ProviderKey,
    string StorageRootId,
    string? PromptSnapshotText,
    string? ConfigSnapshotJson
);

/// <summary>
/// Ngữ cảnh thực thi cung cấp cho Worker khi tiến hành xử lý một Job.
/// </summary>
public sealed record JobExecutionContext(
    string JobId,
    string? ProjectId,
    string? SceneId,
    string TaskType,
    string ProviderKey,
    string StorageRootId,
    string? PromptSnapshotText,
    string? ConfigSnapshotJson,
    long FencingToken,
    int Attempts,
    string? ProfileId = null,
    string? ProviderAccountId = null,
    string? ExistingResultText = null,
    string? ExistingResultReference = null
);

/// <summary>
/// Kết quả trả về sau khi Worker hoàn tất thực thi tác vụ.
/// </summary>
public sealed record JobExecutionResult(
    bool Success,
    string? ResultReference = null,
    string? ResultText = null,
    string? ReasonCode = null,
    string? ErrorMessage = null,
    bool SideEffectSubmitted = false
);

/// <summary>
/// Abstraction điều phối thực thi tác vụ AI.
/// Được thiết kế độc lập với Playwright để hỗ trợ kiểm thử định lượng và cách ly module.
/// </summary>
public interface IJobExecutor
{
    Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        Func<Task> onPreSideEffectCommitAsync,
        Func<int, string, Task>? onProgressAsync,
        CancellationToken cancellationToken);
}

/// <summary>
/// Cổng kiểm soát (Gatekeeper) trạng thái trình duyệt và hạn ngạch tab.
/// Scheduler sử dụng giao diện này để tự động bỏ qua profile đang tạm dừng,
/// Profile đang chờ đăng nhập hoặc Role Tab đang bận.
/// </summary>
public interface IBrowserGatekeeper
{
    bool IsDispatchPaused(string profileId);
    bool IsProfileBusyWithLogin(string profileId);
    bool IsRoleActive(string profileId, string role);
    Task<IDisposable?> TryAcquireTabLeaseAsync(string profileId, string role, string jobId, CancellationToken cancellationToken = default);
    void ReleaseTabLease(string profileId, string role, string jobId);
    bool IsProfileIdle(string profileId);
    void CloseProfileIfIdle(string profileId);
    IReadOnlySet<string> GetActiveOrBusyProfileIds();

    void TrackJob(string profileId, string jobId, bool sideEffectSubmitted = false, string state = "Running") { }
    void SetJobSideEffectSubmitted(string profileId, string jobId, bool submitted = true) { }
    void UntrackJob(string profileId, string jobId) { }
    bool CanResumeJob(string profileId, string jobId, bool sideEffectSubmitted) => true;
}

/// <summary>
/// Triển khai mặc định của IBrowserGatekeeper dựa trên bộ nhớ trong.
/// Phục vụ điều phối cơ bản và cho phép test can thiệp trạng thái nhanh chóng.
/// </summary>
public class DefaultBrowserGatekeeper : IBrowserGatekeeper
{
    private readonly ConcurrentDictionary<string, bool> _pausedProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _loginBusyProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeTabRoles = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? OnProfileClosed;

    public void SetDispatchPaused(string profileId, bool paused) => _pausedProfiles[profileId] = paused;
    public void SetProfileBusyWithLogin(string profileId, bool busy) => _loginBusyProfiles[profileId] = busy;

    public bool IsDispatchPaused(string profileId) =>
        _pausedProfiles.TryGetValue(profileId, out var paused) && paused;

    public bool IsProfileBusyWithLogin(string profileId) =>
        _loginBusyProfiles.TryGetValue(profileId, out var busy) && busy;

    public bool IsRoleActive(string profileId, string role)
    {
        if (string.Equals(profileId, "shared_ai", StringComparison.OrdinalIgnoreCase))
        {
            return _activeTabRoles.Values.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    public bool IsProfileIdle(string profileId) =>
        !_activeTabRoles.Keys.Any(k => k.StartsWith($"{profileId}:", StringComparison.OrdinalIgnoreCase));

    public void CloseProfileIfIdle(string profileId)
    {
        if (IsProfileIdle(profileId))
        {
            OnProfileClosed?.Invoke(profileId);
        }
    }

    public IReadOnlySet<string> GetActiveOrBusyProfileIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _loginBusyProfiles.Where(kv => kv.Value).Select(kv => kv.Key))
        {
            set.Add(p);
        }
        foreach (var p in _pausedProfiles.Where(kv => kv.Value).Select(kv => kv.Key))
        {
            set.Add(p);
        }
        foreach (var key in _activeTabRoles.Keys)
        {
            int colonIndex = key.IndexOf(':');
            if (colonIndex > 0)
            {
                set.Add(key[..colonIndex]);
            }
        }
        return set;
    }

    public Task<IDisposable?> TryAcquireTabLeaseAsync(string profileId, string role, string jobId, CancellationToken cancellationToken = default)
    {
        if (string.Equals(profileId, "shared_ai", StringComparison.OrdinalIgnoreCase))
        {
            if (_activeTabRoles.Values.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult<IDisposable?>(null);
            }
        }

        var key = MakeKey(profileId, jobId);
        if (_activeTabRoles.TryAdd(key, role))
        {
            var lease = new TabLeaseDisposable(() => ReleaseTabLease(profileId, role, jobId));
            return Task.FromResult<IDisposable?>(lease);
        }

        return Task.FromResult<IDisposable?>(null);
    }

    public void ReleaseTabLease(string profileId, string role, string jobId)
    {
        var key = MakeKey(profileId, jobId);
        _activeTabRoles.TryRemove(key, out _);
    }

    private static string MakeKey(string profileId, string jobId) => $"{profileId}:{jobId}";

    private sealed class TabLeaseDisposable : IDisposable
    {
        private Action? _releaseAction;
        public TabLeaseDisposable(Action releaseAction) => _releaseAction = releaseAction;

        public void Dispose()
        {
            Interlocked.Exchange(ref _releaseAction, null)?.Invoke();
        }
    }
}

/// <summary>
/// Executor giả lập có tính chất tất định (Deterministic Fake Job Executor) dành riêng cho test suite.
/// Cho phép lập trình viên mô phỏng chính xác các kịch bản crash, network failure, trễ tiến trình và zombie worker.
/// </summary>
public sealed class DeterministicFakeJobExecutor : IJobExecutor
{
    public TimeSpan ExecutionDelay { get; set; } = TimeSpan.Zero;
    public bool SimulateNetworkErrorBeforeSideEffect { get; set; }
    public bool SimulateCrashBeforeSideEffectCommit { get; set; }
    public bool SimulateCrashAfterSideEffectCommit { get; set; }
    public bool SimulateHangWithoutHeartbeat { get; set; }
    public bool SimulateStorageErrorAfterAiGeneration { get; set; }
    public int TotalAiSubmits { get; set; } = 0;
    public string? ResultReferenceToReturn { get; set; } = "asset-fake-ref-001";
    public string? ResultTextToReturn { get; set; } = "Script generated successfully by deterministic fake executor.";

    public Fraylo.Infrastructure.Storage.AssetFileStore? FileStore { get; set; }
    public Fraylo.Infrastructure.Storage.StorageRootRegistry? RootRegistry { get; set; }
    public string? FinalizeRelativePath { get; set; }

    public List<JobExecutionContext> RecordedExecutions { get; } = new();

    public async Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        Func<Task> onPreSideEffectCommitAsync,
        Func<int, string, Task>? onProgressAsync,
        CancellationToken cancellationToken)
    {
        lock (RecordedExecutions)
        {
            RecordedExecutions.Add(context);
        }

        // T16 CHECKPOINT / FINALIZE:
        // Nếu context đã mang sẵn intermediate result (checkpoint từ lần trước),
        // KHÔNG gửi lại AI (không gọi onPreSideEffectCommitAsync và không tăng TotalAiSubmits)
        bool hasExistingCheckpoint = !string.IsNullOrWhiteSpace(context.ExistingResultText) ||
                                     !string.IsNullOrWhiteSpace(context.ExistingResultReference);

        if (!hasExistingCheckpoint)
        {
            if (onProgressAsync is not null)
            {
                await onProgressAsync(10, "Chuẩn bị tham số prompt").ConfigureAwait(false);
            }

            if (SimulateNetworkErrorBeforeSideEffect)
            {
                throw new InvalidOperationException("Mạng bị ngắt kết nối trước khi gửi yêu cầu.");
            }

            if (SimulateCrashBeforeSideEffectCommit)
            {
                throw new ApplicationException("Giả lập sự cố crash ứng dụng trước khi commit cờ side effect.");
            }

            if (ExecutionDelay > TimeSpan.Zero)
            {
                await Task.Delay(ExecutionDelay, cancellationToken).ConfigureAwait(false);
            }

            // CRITICAL BOUNDARY: Commit cờ side_effect_submitted = 1 vào SQLite TRƯỚC KHI thao tác ngoài
            await onPreSideEffectCommitAsync().ConfigureAwait(false);
            TotalAiSubmits++;

            if (onProgressAsync is not null)
            {
                await onProgressAsync(50, "Đã gửi yêu cầu AI, đang chờ phản hồi").ConfigureAwait(false);
            }

            if (SimulateCrashAfterSideEffectCommit)
            {
                throw new ApplicationException("Giả lập sự cố crash ứng dụng ngay sau khi commit cờ side effect.");
            }

            if (SimulateHangWithoutHeartbeat)
            {
                // Treo tiến trình và không gửi heartbeat để lease quá hạn
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            }

            // Giả lập lỗi storage sau khi sinh AI
            if (SimulateStorageErrorAfterAiGeneration)
            {
                return new JobExecutionResult(
                    Success: false,
                    ResultReference: null,
                    ResultText: ResultTextToReturn,
                    ReasonCode: "storage_root_unavailable",
                    ErrorMessage: "Thư mục lưu trữ đích không khả dụng hoặc bị ngắt kết nối.",
                    SideEffectSubmitted: true
                );
            }
        }
        else
        {
            // Đã có checkpoint: retry chỉ tiến hành finalize lưu output vào storage mà không gọi lại AI
            if (SimulateStorageErrorAfterAiGeneration)
            {
                return new JobExecutionResult(
                    Success: false,
                    ResultReference: context.ExistingResultReference,
                    ResultText: context.ExistingResultText,
                    ReasonCode: "storage_root_unavailable",
                    ErrorMessage: "Thư mục lưu trữ đích vẫn chưa khả dụng khi finalize từ checkpoint.",
                    SideEffectSubmitted: true
                );
            }
        }

        if (FileStore != null && RootRegistry != null)
        {
            var textToSave = context.ExistingResultText ?? ResultTextToReturn;
            if (!string.IsNullOrWhiteSpace(textToSave))
            {
                var rootPath = await RootRegistry.ResolveRootPathAsync(context.StorageRootId, cancellationToken).ConfigureAwait(false);
                var relPath = FinalizeRelativePath ?? $"{context.ProjectId}/output/{context.JobId}_output.txt";
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(textToSave));
                await FileStore.SaveFileAsync(rootPath, relPath, ms, cancellationToken).ConfigureAwait(false);

                if (onProgressAsync is not null)
                {
                    await onProgressAsync(100, "Hoàn tất xử lý tác vụ").ConfigureAwait(false);
                }

                return new JobExecutionResult(
                    Success: true,
                    ResultReference: ResultReferenceToReturn ?? relPath,
                    ResultText: textToSave,
                    SideEffectSubmitted: true
                );
            }
        }

        if (onProgressAsync is not null)
        {
            await onProgressAsync(100, "Hoàn tất xử lý tác vụ").ConfigureAwait(false);
        }

        return new JobExecutionResult(
            Success: true,
            ResultReference: context.ExistingResultReference ?? ResultReferenceToReturn,
            ResultText: context.ExistingResultText ?? ResultTextToReturn,
            SideEffectSubmitted: true
        );
    }
}

/// <summary>
/// Bộ thực thi tác vụ cho môi trường production khi adapter của nhà cung cấp AI chưa sẵn sàng.
/// Tuyệt đối không mô phỏng thành công giả mạo lên cơ sở dữ liệu ứng dụng.
/// </summary>
public sealed class UnavailableAdapterJobExecutor : IJobExecutor
{
    public Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        Func<Task> onPreSideEffectCommitAsync,
        Func<int, string, Task>? onProgressAsync,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new JobExecutionResult(
            Success: false,
            ResultReference: null,
            ResultText: null,
            ReasonCode: "adapter_not_available",
            ErrorMessage: $"Bộ điều hợp cho nhà cung cấp '{context.ProviderKey}' chưa được cấu hình hoặc chưa sẵn sàng trong phiên bản này.",
            SideEffectSubmitted: false
        ));
    }
}
