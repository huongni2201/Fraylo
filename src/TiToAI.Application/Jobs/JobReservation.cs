using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TiToAI.Contracts.Jobs;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Application.Jobs;

/// <summary>
/// Kết quả phản hồi của một nỗ lực cấp quyền thuê độc quyền (Reservation)
/// kèm theo ExecutionBinding bất biến theo execution-contract-v1.1 §3.1.
/// </summary>
public sealed record JobReservationResult(
    bool Acquired,
    Job? Job,
    ExecutionBinding? Binding = null,
    string? Reason = null
)
{
    public static JobReservationResult Success(Job job, ExecutionBinding? binding = null) => new(true, job, binding);
    public static JobReservationResult Failure(string reason) => new(false, null, null, reason);
}

public sealed record JobCommitSuccessResult(
    string State,
    string? ReasonCode,
    bool WasCancelRequested,
    long Revision = 1
);

/// <summary>
/// Quản lý việc cấp phát độc quyền (Atomic Reservation), thời hạn thuê (Lease),
/// nhịp tim định kỳ (Heartbeat), lựa chọn ProviderAccount từ Pool toàn ứng dụng,
/// quản lý slot DB qua ActiveSlotOccupancy và bảo vệ chống Zombie Worker bằng Fencing Token đơn điệu
/// theo H1–H2 và plan 1.2 §2.2.
/// </summary>
public sealed class JobReservation
{
    private readonly DbContextOptions<TiToAIDbContext> _dbOptions;
    private readonly Func<DateTimeOffset>? _clock;

    public JobReservation(
        DbContextOptions<TiToAIDbContext> dbOptions,
        Func<DateTimeOffset>? clock = null)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _clock = clock;
    }

    private DateTimeOffset UtcNow => _clock?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>
    /// Job giữ slot vì cần đối soát: đang AwaitingUser, hoặc đã hủy nhưng side effect đã gửi và chưa có kết quả.
    /// </summary>
    private static bool HoldsSlotForReconcile(Job? job) =>
        job?.State == JobState.AwaitingUser
        || (job?.State == JobState.Canceled && job.SideEffectSubmitted == 1
            && string.IsNullOrWhiteSpace(job.ResultReference) && string.IsNullOrWhiteSpace(job.ResultText));

    /// <summary>
    /// Thực hiện cấp quyền thuê độc quyền (Atomic Compare-and-Set) thông qua SQLite BEGIN IMMEDIATE.
    /// Lựa chọn ProviderAccount eligible từ pool toàn ứng dụng, tạo ExecutionBinding bất biến,
    /// chiếm slot rảnh cụ thể trong ActiveSlotOccupancy và chuyển trạng thái từ 'Queued' sang 'Reserved'.
    /// Nếu pool đầy hoặc thiếu budget, giữ nguyên 'Queued', không tăng attempts và trả Acquired = false.
    /// </summary>
    public async Task<JobReservationResult> TryReserveJobAsync(
        string jobId,
        string workerId,
        TimeSpan leaseDuration,
        Func<string, bool>? isProfileAvailable = null,
        Func<IReadOnlySet<string>>? getOpenProfileIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var dbConn = dbContext.Database.GetDbConnection();

        if (dbConn.State != ConnectionState.Open)
        {
            await dbConn.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        IDbContextTransaction transaction;
        if (dbConn is SqliteConnection sqliteConn)
        {
            // BEGIN IMMEDIATE: Chiếm Exclusive Write Lock trên SQLite ngay lập tức
            var sqliteTx = sqliteConn.BeginTransaction(deferred: false);
            transaction = await dbContext.Database.UseTransactionAsync(sqliteTx, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Không thể liên kết DbContextTransaction từ SqliteTransaction.");
        }
        else
        {
            transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        }

        await using (transaction)
        {
            try
            {
                var nowIso = UtcNow.ToString("o");
                var leaseExpiresIso = UtcNow.Add(leaseDuration).ToString("o");

                // 1. Đọc bản ghi Job cần reserve
                var job = await dbContext.Jobs
                    .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);

                if (job == null || job.State != JobState.Queued)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return JobReservationResult.Failure("Job đã được chiếm bởi tiến trình khác hoặc không còn ở trạng thái Queued.");
                }

                // PV-R4: Kiểm tra trạng thái Workflow run của dự án: Chặn đứng reservation mới nếu quy trình đang Paused hoặc Idle
                var wfConfigKey = $"project.workflow.config.{job.ProjectId}";
                var wfSetting = await dbContext.AppSettings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Key == wfConfigKey, cancellationToken).ConfigureAwait(false);

                var workflowRunId = GetWorkflowRunId(job.ConfigSnapshotJson);
                if (workflowRunId != null &&
                    (wfSetting == null || !IsWorkflowRunDispatchable(wfSetting.Value, workflowRunId)))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return JobReservationResult.Failure("Workflow run đã bị dừng hoặc thay thế; job cũ không được dispatch.");
                }
                if (workflowRunId == null && wfSetting != null &&
                    wfSetting.Value.Contains("\"runState\":\"Paused\"", StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return JobReservationResult.Failure("Workflow run của dự án đang ở trạng thái Paused.");
                }

                // BẢO VỆ CHỐNG RESEND MÙ: Tuyệt đối không reserve lại job đã submit side effect mà chưa có kết quả/checkpoint đối soát.
                if (job.SideEffectSubmitted == 1 && string.IsNullOrWhiteSpace(job.ResultReference) && string.IsNullOrWhiteSpace(job.ResultText)
                    && !await TiToAI.Application.Providers.MuseAiPlaywrightAutomation.CanResumeRenderAsync(dbContext, job, cancellationToken).ConfigureAwait(false))
                {
                    job.State = JobState.AwaitingUser;
                    job.ReasonCode = JobReasonCode.ReconcileRequired;
                    job.LeaseExpiresAtUtc = null;
                    job.LeaseOwnerId = null;
                    job.UpdatedAtUtc = nowIso;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return JobReservationResult.Failure("reconcile_required");
                }

                // H1: Đọc và kiểm tra Global App Budget (toàn ứng dụng)
                int maxConcurrentJobsGlobal = 5;
                int maxOpenBrowsersGlobal = 3;

                var globalSettings = await dbContext.AppSettings
                    .AsNoTracking()
                    .Where(s => s.Key == "scheduler.max_concurrent_jobs" || s.Key == "scheduler.max_open_browsers")
                    .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken).ConfigureAwait(false);

                if (globalSettings.TryGetValue("scheduler.max_concurrent_jobs", out var mcjStr) && int.TryParse(mcjStr, out var mcjVal) && mcjVal > 0)
                {
                    maxConcurrentJobsGlobal = mcjVal;
                }
                if (globalSettings.TryGetValue("scheduler.max_open_browsers", out var mobStr) && int.TryParse(mobStr, out var mobVal) && mobVal > 0)
                {
                    maxOpenBrowsersGlobal = mobVal;
                }

                // Đọc toàn bộ active slot occupancies hiện hành
                var currentOccupancies = await dbContext.ActiveSlotOccupancies
                    .Include(o => o.Job)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                // Loại bỏ bất kỳ occupancy mồ côi mà job đã ở trạng thái kết thúc (Succeeded/Failed/Canceled)
                // LƯU Ý BẢO VỆ T16: Không bao giờ xóa occupancy của job có side effect unresolved (SideEffectSubmitted == 1 và chưa có kết quả)
                var orphanOccupancies = currentOccupancies
                    .Where(o => o.Job != null &&
                        (o.Job.State == JobState.Succeeded ||
                         ((o.Job.State == JobState.Failed || o.Job.State == JobState.Canceled) &&
                          !(o.Job.SideEffectSubmitted == 1 && string.IsNullOrWhiteSpace(o.Job.ResultReference) && string.IsNullOrWhiteSpace(o.Job.ResultText)))))
                    .ToList();
                if (orphanOccupancies.Count > 0)
                {
                    dbContext.ActiveSlotOccupancies.RemoveRange(orphanOccupancies);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    currentOccupancies.RemoveAll(o => orphanOccupancies.Contains(o));
                }

                // Lọc active occupancies (Reserved, Running, AwaitingUser)
                int currentGlobalActiveCount = currentOccupancies.Count(o => o.JobId != jobId);
                if (currentGlobalActiveCount >= maxConcurrentJobsGlobal)
                {
                    // Toàn app hết budget job: Giữ Queued, KHÔNG tăng attempts
                    job.ReasonCode = "waiting_for_global_capacity";
                    job.UpdatedAtUtc = nowIso;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return JobReservationResult.Failure("global_job_budget_reached");
                }

                // H1: Đếm profile đang mở từ dữ liệu được truy vấn rõ ràng, không dựa vào navigation chưa nạp
                var activeOccupancyProfileIds = await dbContext.ActiveSlotOccupancies
                    .AsNoTracking()
                    .Where(o => o.JobId != jobId)
                    .Join(dbContext.ProviderAccounts.AsNoTracking(),
                        o => o.ProviderAccountId,
                        a => a.Id,
                        (o, a) => a.ProfileId)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct()
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                // Thêm các profile của các job ở trạng thái AwaitingUser (uncertain / checkpoint chưa giải phóng tài nguyên)
                var uncertainProfileIds = await dbContext.Jobs
                    .AsNoTracking()
                    .Where(j => j.State == JobState.AwaitingUser && j.Id != jobId)
                    .Join(dbContext.ExecutionBindings.AsNoTracking(),
                        j => j.Id,
                        b => b.JobId,
                        (j, b) => b.ProfileId)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct()
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                var currentlyOpenProfileIds = new HashSet<string>(activeOccupancyProfileIds, StringComparer.OrdinalIgnoreCase);
                foreach (var p in uncertainProfileIds)
                {
                    currentlyOpenProfileIds.Add(p);
                }

                // Bổ sung các profile đang active ở runtime (đang VisibleActive/VisibleWaiting login, Quiescing, Closing, running process)
                var runtimeActiveProfiles = getOpenProfileIds?.Invoke();
                if (runtimeActiveProfiles != null)
                {
                    foreach (var p in runtimeActiveProfiles)
                    {
                        if (!string.IsNullOrWhiteSpace(p))
                        {
                            currentlyOpenProfileIds.Add(p);
                        }
                    }
                }

                // 2. Kiểm tra xem Job đã có ExecutionBinding chưa (trường hợp retry / reconcile)
                var existingBinding = await dbContext.ExecutionBindings
                    .Include(b => b.ProviderAccount)
                    .Include(b => b.Profile)
                    .FirstOrDefaultAsync(b => b.JobId == jobId, cancellationToken).ConfigureAwait(false);

                if (existingBinding != null)
                {
                    var boundAccount = await dbContext.ProviderAccounts
                        .FirstOrDefaultAsync(a => a.Id == existingBinding.ProviderAccountId, cancellationToken).ConfigureAwait(false);

                    if (boundAccount == null || !boundAccount.Enabled || boundAccount.Lifecycle != "Active")
                    {
                        job.ReasonCode = "account_disabled";
                        job.UpdatedAtUtc = nowIso;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return JobReservationResult.Failure("account_disabled");
                    }

                    if (isProfileAvailable != null && !isProfileAvailable(existingBinding.ProfileId))
                    {
                        job.ReasonCode = "profile_unavailable";
                        job.UpdatedAtUtc = nowIso;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return JobReservationResult.Failure("profile_unavailable");
                    }

                    // H1 Check Global Browser Budget trên nhánh retry
                    bool boundProfileIsOpen = currentlyOpenProfileIds.Contains(boundAccount.ProfileId);
                    if (!boundProfileIsOpen && currentlyOpenProfileIds.Count >= maxOpenBrowsersGlobal)
                    {
                        // Profile của job retry chưa mở và đã chạm ngưỡng max_open_browsers toàn app -> Giữ Queued, không tăng attempts
                        job.ReasonCode = "waiting_for_global_browser_budget";
                        job.UpdatedAtUtc = nowIso;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return JobReservationResult.Failure("global_browser_budget_reached");
                    }

                    // H1 Check Account Capacity & limits trên đường retry
                    var accountOtherOccupancies = currentOccupancies
                        .Where(o => o.ProviderAccountId == boundAccount.Id && o.JobId != jobId)
                        .ToList();

                    // Sử dụng MaxTabs làm nguồn cấu hình N duy nhất cho tài khoản, không bị Max Jobs cũ chặn ngầm
                    int boundCapacity = boundAccount.MaxTabs > 0 ? boundAccount.MaxTabs : 1;
                    if (accountOtherOccupancies.Count >= boundCapacity)
                    {
                        // Slot đang bị job cần đối soát giữ: báo rõ để người dùng biết cần xử lý job đó trước
                        job.ReasonCode = accountOtherOccupancies.Any(o => HoldsSlotForReconcile(o.Job))
                            ? "waiting_for_reconcile"
                            : "waiting_for_capacity";
                        job.UpdatedAtUtc = nowIso;
                        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return JobReservationResult.Failure("account_capacity_reached");
                    }

                    if (!string.IsNullOrWhiteSpace(boundAccount.PerTaskLimitsJson))
                    {
                        try
                        {
                            var limits = JsonSerializer.Deserialize<Dictionary<string, int>>(boundAccount.PerTaskLimitsJson);
                            if (limits != null && limits.TryGetValue(job.TaskType, out int taskLimit))
                            {
                                int currentTaskCount = accountOtherOccupancies.Count(o => o.Job != null && o.Job.TaskType == job.TaskType);
                                if (currentTaskCount >= taskLimit)
                                {
                                    job.ReasonCode = "waiting_for_capacity";
                                    job.UpdatedAtUtc = nowIso;
                                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                                    return JobReservationResult.Failure("account_task_limit_reached");
                                }
                            }
                        }
                        catch
                        {
                            // Bỏ qua lỗi parse
                        }
                    }

                    // Đảm bảo slot của existingBinding được ghi nhận trong ActiveSlotOccupancy
                    var existingOccupancy = currentOccupancies.FirstOrDefault(o => o.JobId == jobId);
                    if (existingOccupancy == null)
                    {
                        // Kiểm tra xem slot có bị job khác chiếm không
                        bool slotTakenByOther = accountOtherOccupancies.Any(o => o.TabSlotId == existingBinding.TabSlotId);
                        string targetSlot = slotTakenByOther
                            ? FindSmallestAvailableSlot(boundAccount, accountOtherOccupancies.Select(o => o.TabSlotId).ToHashSet())
                            : existingBinding.TabSlotId;

                        existingBinding.TabSlotId = targetSlot;

                        var newOcc = new ActiveSlotOccupancy
                        {
                            ProviderAccountId = boundAccount.Id,
                            TabSlotId = targetSlot,
                            JobId = job.Id,
                            LeaseOwnerId = workerId,
                            LeaseToken = job.FencingToken + 1,
                            AcquiredAtUtc = nowIso
                        };
                        dbContext.ActiveSlotOccupancies.Add(newOcc);
                    }
                    else
                    {
                        existingOccupancy.LeaseOwnerId = workerId;
                        existingOccupancy.LeaseToken = job.FencingToken + 1;
                        existingOccupancy.AcquiredAtUtc = nowIso;
                    }

                    // v1.1 §3.1: Giữ nguyên binding đã cấp cho các lần retry/reconcile, không đổi account ngầm
                    job.State = JobState.Reserved;
                    job.Revision += 1;
                    job.Attempts += 1;
                    job.LeaseOwnerId = workerId;
                    job.FencingToken += 1;
                    job.LeaseExpiresAtUtc = leaseExpiresIso;
                    job.LastHeartbeatAtUtc = nowIso;
                    job.UpdatedAtUtc = nowIso;
                    job.ReasonCode = null;

                    existingBinding.LeaseOwnerId = workerId;
                    existingBinding.LeaseToken = job.FencingToken;

                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return JobReservationResult.Success(job, existingBinding);
                }

                // 3. Lần reserve đầu tiên: Tìm ProviderAccount eligible từ pool toàn ứng dụng
                var eligibleAccounts = await dbContext.ProviderAccounts
                    .Where(a => a.ProviderKey == job.ProviderKey &&
                                a.Enabled &&
                                a.Lifecycle == "Active")
                    .ToListAsync(cancellationToken).ConfigureAwait(false);

                if (eligibleAccounts.Count == 0)
                {
                    // Chưa có tài khoản nào được cấu hình cho provider này trong pool
                    job.ReasonCode = "provider_account_required";
                    job.UpdatedAtUtc = nowIso;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return JobReservationResult.Failure("provider_account_required");
                }

                // Lọc các account còn slot trống và đáp ứng perTaskLimits + global browser limits
                var candidatePool = new List<(ProviderAccount Account, int ActiveCount, string AvailableSlotId)>();

                foreach (var account in eligibleAccounts)
                {
                    if (isProfileAvailable != null && !isProfileAvailable(account.ProfileId))
                    {
                        continue;
                    }

                    var accountOccupancies = currentOccupancies.Where(o => o.ProviderAccountId == account.Id).ToList();
                    int activeCount = accountOccupancies.Count;

                    // Sử dụng MaxTabs làm nguồn cấu hình N duy nhất (Số tác vụ chạy đồng thời), không lấy min(MaxJobs, MaxTabs)
                    int effectiveCap = account.MaxTabs > 0 ? account.MaxTabs : 1;
                    if (activeCount >= effectiveCap)
                    {
                        continue; // Hết capacity
                    }

                    // Nếu profile của account chưa mở, kiểm tra xem có vượt globalMaxOpenBrowsers không
                    if (!currentlyOpenProfileIds.Contains(account.ProfileId))
                    {
                        if (currentlyOpenProfileIds.Count >= maxOpenBrowsersGlobal)
                        {
                            continue; // Vượt ngân sách mở trình duyệt toàn app
                        }
                    }

                    // Kiểm tra perTaskLimitsJson nếu có
                    if (!string.IsNullOrWhiteSpace(account.PerTaskLimitsJson))
                    {
                        try
                        {
                            var limits = JsonSerializer.Deserialize<Dictionary<string, int>>(account.PerTaskLimitsJson);
                            if (limits != null && limits.TryGetValue(job.TaskType, out int taskLimit))
                            {
                                int currentRoleCount = accountOccupancies
                                    .Count(o => o.Job != null && o.Job.TaskType == job.TaskType);

                                if (currentRoleCount >= taskLimit)
                                {
                                    continue; // Vượt giới hạn task type này
                                }
                            }
                        }
                        catch
                        {
                            // Bỏ qua lỗi parse giới hạn, dùng MaxConcurrentJobs
                        }
                    }

                    // H2: Tìm slot rảnh nhỏ nhất, không bao giờ dùng ActiveCount + 1 gây trùng slot!
                    var occupiedSlotIds = accountOccupancies.Select(o => o.TabSlotId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    string availableSlot = FindSmallestAvailableSlot(account, occupiedSlotIds);

                    candidatePool.Add((account, activeCount, availableSlot));
                }

                if (candidatePool.Count == 0)
                {
                    bool blockedByBrowserBudget = eligibleAccounts.Any(a =>
                    {
                        int cap = a.MaxTabs > 0 ? a.MaxTabs : 1;
                        int cur = currentOccupancies.Count(o => o.ProviderAccountId == a.Id);
                        return cur < cap && !currentlyOpenProfileIds.Contains(a.ProfileId) && currentlyOpenProfileIds.Count >= maxOpenBrowsersGlobal;
                    });

                    // Toàn bộ pool đã đầy hoặc chạm budget browser: Giữ nguyên trạng thái Queued, KHÔNG tăng attempts
                    // Slot đang bị job cần đối soát giữ: báo rõ để người dùng biết cần xử lý job đó trước
                    bool blockedByReconcile = eligibleAccounts.Any(a =>
                        currentOccupancies.Any(o => o.ProviderAccountId == a.Id && HoldsSlotForReconcile(o.Job)));
                    job.ReasonCode = blockedByBrowserBudget ? "waiting_for_global_browser_budget"
                        : blockedByReconcile ? "waiting_for_reconcile"
                        : "waiting_for_capacity";
                    job.UpdatedAtUtc = nowIso;
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return JobReservationResult.Failure(blockedByBrowserBudget ? "global_browser_budget_reached" : "pool_capacity_reached");
                }

                // Phân phối công bằng: Chọn account có ít active job nhất, rồi theo UpdatedAt, rồi Id
                var selectedCandidate = candidatePool
                    .OrderBy(c => c.ActiveCount)
                    .ThenBy(c => c.Account.UpdatedAtUtc)
                    .ThenBy(c => c.Account.Id)
                    .First();

                var chosenAccount = selectedCandidate.Account;
                string chosenSlotId = selectedCandidate.AvailableSlotId;

                // 4. Tạo ExecutionBinding bất biến
                var newBinding = new ExecutionBinding
                {
                    JobId = job.Id,
                    ProviderAccountId = chosenAccount.Id,
                    ProfileId = chosenAccount.ProfileId,
                    Identity = chosenAccount.VerifiedIdentity,
                    CapacityConfigRevision = chosenAccount.Revision,
                    TabSlotId = chosenSlotId,
                    AssignedAtUtc = nowIso,
                    LeaseOwnerId = workerId,
                    LeaseToken = job.FencingToken + 1
                };

                dbContext.ExecutionBindings.Add(newBinding);

                // H2: Chiếm slot trong ActiveSlotOccupancy (PK ngăn chặn trùng tuyệt đối cấp SQLite)
                var newOccupancy = new ActiveSlotOccupancy
                {
                    ProviderAccountId = chosenAccount.Id,
                    TabSlotId = chosenSlotId,
                    JobId = job.Id,
                    LeaseOwnerId = workerId,
                    LeaseToken = job.FencingToken + 1,
                    AcquiredAtUtc = nowIso
                };
                dbContext.ActiveSlotOccupancies.Add(newOccupancy);

                // 5. Cập nhật Job sang trạng thái Reserved
                job.State = JobState.Reserved;
                job.Revision += 1;
                job.Attempts += 1;
                job.LeaseOwnerId = workerId;
                job.FencingToken += 1;
                job.LeaseExpiresAtUtc = leaseExpiresIso;
                job.LastHeartbeatAtUtc = nowIso;
                job.UpdatedAtUtc = nowIso;
                job.ReasonCode = null;

                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return JobReservationResult.Success(job, newBinding);
            }
            catch (Exception ex)
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Nuốt lỗi rollback nếu kết nối đã đóng
                }

                return JobReservationResult.Failure($"Lỗi giao dịch reservation: {ex.Message}");
            }
        }
    }

    private static string? GetWorkflowRunId(string? configSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(configSnapshotJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(configSnapshotJson);
            if (!document.RootElement.TryGetProperty("WorkflowRunId", out var runId) &&
                !document.RootElement.TryGetProperty("workflowRunId", out runId))
            {
                return null;
            }
            return runId.ValueKind == JsonValueKind.String ? runId.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsWorkflowRunDispatchable(string configJson, string jobRunId)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            var root = document.RootElement;
            var runId = root.TryGetProperty("CurrentRunId", out var currentRunId) || root.TryGetProperty("currentRunId", out currentRunId)
                ? currentRunId.GetString()
                : null;
            var runState = root.TryGetProperty("RunState", out var state) || root.TryGetProperty("runState", out state)
                ? state.GetString()
                : null;
            return string.Equals(runId, jobRunId, StringComparison.Ordinal) &&
                   string.Equals(runState, "Running", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tìm slot rảnh có số thứ tự nhỏ nhất từ slot-1, slot-2, ...
    /// </summary>
    private static string FindSmallestAvailableSlot(ProviderAccount account, HashSet<string> occupiedSlotIds)
    {
        int maxSlots = account.MaxTabs > 0 ? account.MaxTabs : 3;

        for (int k = 1; k <= maxSlots + 5; k++)
        {
            string candidateSlot = $"slot-{k}";
            if (!occupiedSlotIds.Contains(candidateSlot))
            {
                return candidateSlot;
            }
        }

        return $"slot-{occupiedSlotIds.Count + 1}";
    }

    /// <summary>
    /// Giải phóng vị trí slot khỏi ActiveSlotOccupancy trong DB khi job hoàn tất hoặc bị hủy.
    /// </summary>
    public async Task ReleaseOccupancyAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return;

        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var occupancy = await dbContext.ActiveSlotOccupancies
            .FirstOrDefaultAsync(o => o.JobId == jobId, cancellationToken).ConfigureAwait(false);

        if (occupancy != null)
        {
            dbContext.ActiveSlotOccupancies.Remove(occupancy);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Chuyển trạng thái từ Reserved sang Running trước khi tiến hành chuẩn bị dữ liệu và mở tab.
    /// Bắt buộc fencing_token phải khớp chính xác.
    /// </summary>
    public async Task TransitionToRunningAsync(
        string jobId,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var nowIso = UtcNow.ToString("o");

        const string updateSql = @"
            UPDATE Job
            SET revision = revision + 1,
                state = 'Running',
                updated_at_utc = @now
            WHERE id = @jobId
              AND fencing_token = @token
              AND state = 'Reserved';";

        var rows = await dbContext.Database.ExecuteSqlRawAsync(
            updateSql,
            new object[]
            {
                new SqliteParameter("@now", nowIso),
                new SqliteParameter("@jobId", jobId),
                new SqliteParameter("@token", fencingToken)
            },
            cancellationToken).ConfigureAwait(false);

        if (rows == 0)
        {
            throw new FencingTokenStaleException(jobId, fencingToken);
        }
    }

    /// <summary>
    /// Gửi nhịp tim định kỳ (Heartbeat) để gia hạn lease_expires_at_utc.
    /// Kiểm tra nghiêm ngặt lease_owner_id và fencing_token.
    /// Nếu rowsAffected == 0, worker đã bị loại bỏ (fenced out), ném ngay FencingTokenStaleException.
    /// </summary>
    public async Task SendHeartbeatAsync(
        string jobId,
        string workerId,
        long fencingToken,
        TimeSpan extension,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var nowIso = UtcNow.ToString("o");
        var newLeaseExpiresIso = UtcNow.Add(extension).ToString("o");

        const string updateSql = @"
            UPDATE Job
            SET revision = revision + 1,
                lease_expires_at_utc = @newLeaseExpires,
                last_heartbeat_at_utc = @now,
                updated_at_utc = @now
            WHERE id = @jobId
              AND lease_owner_id = @workerId
              AND fencing_token = @token
              AND state IN ('Reserved', 'Running')
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc >= @now);";

        var rows = await dbContext.Database.ExecuteSqlRawAsync(
            updateSql,
            new object[]
            {
                new SqliteParameter("@newLeaseExpires", newLeaseExpiresIso),
                new SqliteParameter("@now", nowIso),
                new SqliteParameter("@jobId", jobId),
                new SqliteParameter("@workerId", workerId),
                new SqliteParameter("@token", fencingToken)
            },
            cancellationToken).ConfigureAwait(false);

        if (rows == 0)
        {
            throw new FencingTokenStaleException(jobId, fencingToken);
        }
    }

    /// <summary>
    /// CRITICAL BOUNDARY: Cam kết cờ side_effect_submitted = 1 vào SQLite TRƯỚC KHI thực thi thao tác ngoài.
    /// Nếu rowsAffected == 0, ném ngay FencingTokenStaleException để ngừng ngay lập tức trước khi gọi browser.
    /// </summary>
    public async Task CommitSideEffectAsync(
        string jobId,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var nowIso = UtcNow.ToString("o");

        const string updateSql = @"
            UPDATE Job
            SET revision = revision + 1,
                side_effect_submitted = 1,
                side_effect_at_utc = @now,
                updated_at_utc = @now
            WHERE id = @jobId
              AND fencing_token = @token
              AND state IN ('Reserved', 'Running')
              AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc >= @now);";

        var rows = await dbContext.Database.ExecuteSqlRawAsync(
            updateSql,
            new object[]
            {
                new SqliteParameter("@now", nowIso),
                new SqliteParameter("@jobId", jobId),
                new SqliteParameter("@token", fencingToken)
            },
            cancellationToken).ConfigureAwait(false);

        if (rows == 0)
        {
            throw new FencingTokenStaleException(jobId, fencingToken);
        }
    }

    /// <summary>
    /// Cam kết kết quả hoàn thành thành công (Succeeded), xóa lease và giải phóng slot occupancy.
    /// Nếu CancelRequested = 1 (kết quả muộn sau khi người dùng hủy): chuyển Canceled, giữ kết quả,
    /// đánh dấu Asset liên quan dạng Unlinked để không làm mất tài nguyên của người dùng theo T16.
    /// Bắt buộc phải có ít nhất ResultReference hoặc ResultText hợp lệ theo v1.1.
    /// </summary>
    public async Task<JobCommitSuccessResult> CommitSuccessAsync(
        string jobId,
        long fencingToken,
        string? resultReference,
        string? resultText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resultReference) && string.IsNullOrWhiteSpace(resultText))
        {
            throw new InvalidOperationException("Không thể đánh dấu thành công (Succeeded) khi không có ResultReference hoặc ResultText hợp lệ.");
        }

        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var nowIso = UtcNow.ToString("o");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        const string updateSql = @"
            UPDATE Job
            SET revision = revision + 1,
                state = CASE WHEN cancel_requested = 1 THEN 'Canceled' ELSE 'Succeeded' END,
                reason_code = CASE WHEN cancel_requested = 1 THEN 'user_canceled' ELSE NULL END,
                result_reference = @resultRef,
                result_text = @resultText,
                lease_owner_id = NULL,
                lease_expires_at_utc = NULL,
                updated_at_utc = @now
            WHERE id = @jobId
              AND fencing_token = @token
              AND state IN ('Reserved', 'Running');";

        var rows = await dbContext.Database.ExecuteSqlRawAsync(
            updateSql,
            new object[]
            {
                new SqliteParameter("@resultRef", (object?)resultReference ?? DBNull.Value),
                new SqliteParameter("@resultText", (object?)resultText ?? DBNull.Value),
                new SqliteParameter("@now", nowIso),
                new SqliteParameter("@jobId", jobId),
                new SqliteParameter("@token", fencingToken)
            },
            cancellationToken).ConfigureAwait(false);

        if (rows == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new FencingTokenStaleException(jobId, fencingToken);
        }

        var committedJob = await dbContext.Jobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);

        bool wasCancelRequested = committedJob?.State == JobState.Canceled;
        string targetState = wasCancelRequested ? JobState.Canceled : JobState.Succeeded;
        string? reasonCode = wasCancelRequested ? JobReasonCode.UserCanceled : null;

        // Nếu bị hủy mà có Asset sinh ra, đánh dấu Unlinked để không làm mất file vật lý
        if (wasCancelRequested && !string.IsNullOrWhiteSpace(resultReference))
        {
            var asset = await dbContext.Assets
                .FirstOrDefaultAsync(a => a.Id == resultReference || a.RelativePath == resultReference, cancellationToken)
                .ConfigureAwait(false);
            if (asset != null)
            {
                asset.Status = "Unlinked";
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Giải phóng slot occupancy vì kết quả đã được ghi nhận an toàn
        var occupancy = await dbContext.ActiveSlotOccupancies
            .FirstOrDefaultAsync(o => o.JobId == jobId, cancellationToken).ConfigureAwait(false);
        if (occupancy != null)
        {
            dbContext.ActiveSlotOccupancies.Remove(occupancy);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new JobCommitSuccessResult(targetState, reasonCode, wasCancelRequested, committedJob?.Revision ?? 1);
    }

    /// <summary>
    /// Xử lý thất bại khi thực thi tác vụ:
    /// Nếu canRetry == true: trả về Queued kèm retry_after_utc.
    /// Ngược lại: đánh dấu Failed kèm reason_code.
    /// Cả 2 trường hợp này giải phóng slot occupancy.
    /// </summary>
    public async Task CommitFailureAsync(
        string jobId,
        long fencingToken,
        string reasonCode,
        bool canRetry,
        TimeSpan? retryDelay,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var nowIso = UtcNow.ToString("o");

        if (canRetry)
        {
            var retryAfterIso = UtcNow.Add(retryDelay ?? TimeSpan.FromSeconds(5)).ToString("o");

            const string retrySql = @"
                UPDATE Job
                SET revision = revision + 1,
                    state = 'Queued',
                    reason_code = @reason,
                    lease_expires_at_utc = NULL,
                    retry_after_utc = @retryAfter,
                    updated_at_utc = @now
                WHERE id = @jobId
                  AND fencing_token = @token
                  AND state IN ('Reserved', 'Running');";

            var rows = await dbContext.Database.ExecuteSqlRawAsync(
                retrySql,
                new object[]
                {
                    new SqliteParameter("@reason", reasonCode),
                    new SqliteParameter("@retryAfter", retryAfterIso),
                    new SqliteParameter("@now", nowIso),
                    new SqliteParameter("@jobId", jobId),
                    new SqliteParameter("@token", fencingToken)
                },
                cancellationToken).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new FencingTokenStaleException(jobId, fencingToken);
            }
        }
        else
        {
            const string failSql = @"
                UPDATE Job
                SET revision = revision + 1,
                    state = 'Failed',
                    reason_code = @reason,
                    lease_expires_at_utc = NULL,
                    updated_at_utc = @now
                WHERE id = @jobId
                  AND fencing_token = @token
                  AND state IN ('Reserved', 'Running');";

            var rows = await dbContext.Database.ExecuteSqlRawAsync(
                failSql,
                new object[]
                {
                    new SqliteParameter("@reason", reasonCode),
                    new SqliteParameter("@now", nowIso),
                    new SqliteParameter("@jobId", jobId),
                    new SqliteParameter("@token", fencingToken)
                },
                cancellationToken).ConfigureAwait(false);

            if (rows == 0)
            {
                throw new FencingTokenStaleException(jobId, fencingToken);
            }
        }

        // Giải phóng slot occupancy
        var occupancy = await dbContext.ActiveSlotOccupancies
            .FirstOrDefaultAsync(o => o.JobId == jobId, cancellationToken).ConfigureAwait(false);
        if (occupancy != null)
        {
            dbContext.ActiveSlotOccupancies.Remove(occupancy);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hoàn trả Job về trạng thái Queued khi không chiếm được tab lease (ví dụ role tab đang bận).
    /// Giảm attempts đi 1 vì tác vụ chưa từng thực sự được chạy trên worker và giải phóng slot occupancy.
    /// </summary>
    public async Task ReleaseReservationDueToNoCapacityAsync(
        string jobId,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var nowIso = UtcNow.ToString("o");

        const string updateSql = @"
            UPDATE Job
            SET revision = revision + 1,
                state = 'Queued',
                attempts = MAX(0, attempts - 1),
                lease_owner_id = NULL,
                lease_expires_at_utc = NULL,
                reason_code = 'tab_capacity_unavailable',
                updated_at_utc = @now
            WHERE id = @jobId
              AND fencing_token = @token
              AND state IN ('Reserved', 'Running');";

        await dbContext.Database.ExecuteSqlRawAsync(
            updateSql,
            new object[]
            {
                new SqliteParameter("@now", nowIso),
                new SqliteParameter("@jobId", jobId),
                new SqliteParameter("@token", fencingToken)
            },
            cancellationToken).ConfigureAwait(false);

        // Giải phóng slot occupancy
        var occupancy = await dbContext.ActiveSlotOccupancies
            .FirstOrDefaultAsync(o => o.JobId == jobId, cancellationToken).ConfigureAwait(false);
        if (occupancy != null)
        {
            dbContext.ActiveSlotOccupancies.Remove(occupancy);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
