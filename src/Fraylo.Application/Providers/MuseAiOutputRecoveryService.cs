using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Content;
using Fraylo.Application.Jobs;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Application.Providers;

/// <summary>
/// Recovers the video of a MuseAI render that is awaiting reconciliation: downloads it from the saved side chat,
/// then marks the job succeeded. Never resends the prompt; only runs when the side effect was sent and no result exists.
/// </summary>
public sealed class MuseAiOutputRecoveryService
{
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly MuseAiPlaywrightAutomation _automation;
    private readonly JobService _jobs;
    private readonly ProjectWorkflowService _workflow;

    public MuseAiOutputRecoveryService(DbContextOptions<FrayloDbContext> dbOptions, MuseAiPlaywrightAutomation automation,
        JobService jobs, ProjectWorkflowService workflow)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    }

    // Static: mọi instance dùng chung một tập job đang khôi phục trong tiến trình.
    private static readonly ConcurrentDictionary<string, byte> RecoveringJobIds = new(StringComparer.Ordinal);

    public async Task<JobReconcileResponseData> RecoverAsync(string jobId, CancellationToken ct = default)
    {
        // Một tác vụ chỉ có một lượt khôi phục: bấm lại khi lượt trước còn chạy không được tải thêm một bản.
        if (!RecoveringJobIds.TryAdd(jobId, 0))
            throw new JobValidationException("Đang khôi phục video cho tác vụ này. Hãy chờ lượt hiện tại hoàn tất.", nameof(jobId));
        try
        {
            return await RecoverOnceAsync(jobId, ct).ConfigureAwait(false);
        }
        finally
        {
            RecoveringJobIds.TryRemove(jobId, out _);
        }
    }

    private async Task<JobReconcileResponseData> RecoverOnceAsync(string jobId, CancellationToken ct)
    {
        await using var db = new FrayloDbContext(_dbOptions);
        var job = await db.Jobs.AsNoTracking()
            .Include(j => j.ExecutionBinding)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct).ConfigureAwait(false)
            ?? throw new JobNotFoundException(jobId);

        var hasResult = !string.IsNullOrWhiteSpace(job.ResultReference) || !string.IsNullOrWhiteSpace(job.ResultText);
        if (job.TaskType != JobTaskType.VideoRender || job.ProviderKey != ProviderKeys.MuseAI ||
            job.State != JobState.AwaitingUser || job.SideEffectSubmitted != 1 || hasResult)
        {
            throw new JobValidationException(
                "Chỉ khôi phục video cho tác vụ render MuseAI đang chờ đối soát, đã gửi và chưa có kết quả.", nameof(jobId));
        }

        var profileId = job.ExecutionBinding?.ProfileId;
        if (string.IsNullOrWhiteSpace(profileId))
            throw new JobValidationException("Tác vụ chưa có profile MuseAI để mở lại side chat.", nameof(jobId));

        var result = await _automation.RecoverRenderAsync(job.Id, profileId, job.ProjectId, job.StorageRootId, ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.ResultReference))
            throw new JobValidationException(result.ErrorMessage ?? "Không khôi phục được video MuseAI.", nameof(jobId));

        var reconciled = await _jobs.ReconcileAsync(
            new JobReconcileRequest(job.Id, "mark_succeeded", ResultReference: result.ResultReference), ct).ConfigureAwait(false);

        // Reconcile does not apply workflow output by itself, so apply it here to link the video to the Scene and Project.
        try
        {
            await _workflow.ApplyJobOutputAsync(job.Id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MuseAI] Apply recovered output failed for job {job.Id}: {ex}");
        }

        return reconciled;
    }
}
