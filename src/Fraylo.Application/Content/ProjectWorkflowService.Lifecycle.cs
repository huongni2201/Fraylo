using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Content.Workflows;
using Fraylo.Application.Jobs;
using Fraylo.Application.Prompts.Exceptions;
using Fraylo.Application.Providers;
using Fraylo.Contracts.Content;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;
using Fraylo.Contracts.Scenes;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Content;

public sealed partial class ProjectWorkflowService
{
    /// <summary>Số cảnh tối đa của một run nhiều video; quá nhiều cảnh làm số render và chi phí tăng khó kiểm soát.</summary>
    private const int MaxMultiVideoScenes = 10;

    private static bool IsVideoDurationSupported(int durationSeconds, string providerKey = ProviderKeys.MuseAI)
    {
        return ProviderCapabilities.IsDurationSupported(providerKey, durationSeconds);
    }

    private static IReadOnlyList<int> GetSupportedVideoDurations(string providerKey = ProviderKeys.MuseAI)
    {
        return ProviderCapabilities.GetSupportedDurations(providerKey);
    }

    /// <summary>
    /// Kích hoạt quy trình sản xuất video (Bấm Bắt đầu tạo video một lần duy nhất).
    /// Kiểm tra tính đầy đủ của dữ liệu theo chế độ đã chọn:
    /// - ModelAndProduct: Bắt buộc cả Model và Product.
    /// - ReadyImage: Bắt buộc ReadyImage.
    /// - ProductOnly: Bắt buộc Product.
    /// Tự động lập danh sách cảnh nếu chưa có cảnh nào, gán ảnh nền tảng và viết prompt.
    /// </summary>
    public Task<StartProjectWorkflowResponseData> StartWorkflowAsync(
        StartProjectWorkflowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedProjectId = NormalizeAndValidateGuid(request.ProjectId, nameof(request.ProjectId));
        // Dưới khóa Project như hủy/tiếp tục/apply: kiểm ClientRequestId và RunState rồi mới commit run, nên hai lệnh Start cùng lúc không tạo hai run.
        return RunUnderProjectGateAsync(normalizedProjectId, () => StartWorkflowLockedAsync(request, normalizedProjectId, cancellationToken), cancellationToken);
    }

    private async Task<StartProjectWorkflowResponseData> StartWorkflowLockedAsync(
        StartProjectWorkflowRequest request,
        string normalizedProjectId,
        CancellationToken cancellationToken)
    {

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var config = await GetWorkflowConfigAsync(normalizedProjectId, cancellationToken);

        // Idempotency guard: nếu có ClientRequestId và đang có một Run active với cùng RequestId, trả về kết quả đó ngay (PV-R1)
        if (!string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            var activeRunKeyCheck = $"workflow.active_run.{normalizedProjectId}";
            var activeRunSettingCheck = await dbContext.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == activeRunKeyCheck, cancellationToken);
            if (activeRunSettingCheck != null && !string.IsNullOrWhiteSpace(activeRunSettingCheck.Value))
            {
                try
                {
                    var existingRun = JsonSerializer.Deserialize<WorkflowRunRecord>(activeRunSettingCheck.Value, JsonOptions);
                    if (existingRun != null && existingRun.ClientRequestId == request.ClientRequestId)
                    {
                        var sceneCount = await dbContext.Scenes.CountAsync(s => s.ProjectId == normalizedProjectId, cancellationToken);
                        return new StartProjectWorkflowResponseData(
                            RunId: existingRun.RunId,
                            RunState: existingRun.RunState,
                            CreatedSceneCount: sceneCount,
                            Message: "Quy trình tạo video đang được xử lý (trả kết quả theo ClientRequestId hiện tại)."
                        );
                    }
                }
                catch { }
            }
        }

        // Chặn bắt đầu lại nếu quy trình đang Running
        if (config.RunState == WorkflowRunState.Running)
        {
            // Kiểm tra xem thực tế có job nào đang active không
            bool hasActiveJobs = await dbContext.Jobs
                .AsNoTracking()
                .AnyAsync(j => j.ProjectId == normalizedProjectId &&
                               (j.State == JobState.Queued || j.State == JobState.Reserved || j.State == JobState.Running || j.State == JobState.AwaitingUser), cancellationToken);

            if (!hasActiveJobs)
            {
                // Tự phục hồi: Không còn job nào active nhưng state vẫn Running -> cho phép chạy lại
                config = config with { RunState = WorkflowRunState.Idle };
            }
            else
            {
                throw new ProjectWorkflowValidationException(
                    "Dự án đang có quy trình sản xuất video đang chạy (Running). Vui lòng chờ hoàn tất, tạm dừng hoặc hủy trước khi bắt đầu quy trình mới.");
            }
        }

        // Kiểm tra thời lượng cấu hình có được provider video hỗ trợ hay không (§2, CD04, CD05)
        if (config.WorkflowMode != WorkflowModes.ReferenceVideo && !IsVideoDurationSupported(config.TargetDurationPerSceneSeconds))
        {
            var supported = GetSupportedVideoDurations();
            throw new ProjectWorkflowValidationException(
                $"Thời lượng {config.TargetDurationPerSceneSeconds} giây không được nhà cung cấp video hỗ trợ. Vui lòng chọn lại thời lượng hợp lệ ({string.Join("s, ", supported)}s).");
        }

        // 1. Kiểm tra tính hợp lệ của đầu vào theo chế độ (§2)
        var workflow = VideoWorkflows.For(config.WorkflowMode);
        workflow.ValidateInputs(config);
        var images = workflow.PlanImages(config);

        var nowIso = DateTimeOffset.UtcNow.ToString("o");
        var runId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        bool singleVideo = config.VideoStrategy == VideoStrategies.SingleVideo;
        if (!singleVideo && (config.TargetSceneCount < 1 || config.TargetSceneCount > MaxMultiVideoScenes))
        {
            throw new ProjectWorkflowValidationException($"Số cảnh phải từ 1 đến {MaxMultiVideoScenes} khi tạo nhiều video.");
        }
        var runConfig = config with
        {
            CurrentRunId = runId,
            TargetSceneCount = singleVideo ? 1 : config.TargetSceneCount
        };

        // 2. Lấy danh sách các cảnh hiện có của dự án
        var existingScenes = await dbContext.Scenes
            .Where(s => s.ProjectId == normalizedProjectId)
            .OrderBy(s => s.SequenceOrder)
            .ToListAsync(cancellationToken);

        if (singleVideo && existingScenes.Count > 1)
        {
            throw new ProjectWorkflowValidationException(
                $"Chế độ một video chỉ dùng một phân cảnh, dự án đang có {existingScenes.Count} cảnh. Hãy chuyển sang 'Nhiều video' hoặc tạo dự án mới.");
        }

        // Kiểm tra thời lượng của từng cảnh hiện có theo capability provider (§2, CD04, CD08)
        var supportedDurations = GetSupportedVideoDurations();
        var invalidDurationScene = config.WorkflowMode == WorkflowModes.ReferenceVideo ? null : existingScenes.FirstOrDefault(s => !IsVideoDurationSupported(Convert.ToInt32(s.TargetDurationSeconds)));
        if (invalidDurationScene != null)
        {
            throw new ProjectWorkflowValidationException(
                $"Cảnh '{invalidDurationScene.Name}' có thời lượng {Convert.ToInt32(invalidDurationScene.TargetDurationSeconds)}s không được hỗ trợ bởi nhà cung cấp video. Vui lòng chọn lại thời lượng ({string.Join("s, ", supportedDurations)}s) trước khi bắt đầu.");
        }

        // Mẫu + sản phẩm (một video) không viết kịch bản: ảnh ghép là bước đầu, video chỉ dùng đúng ảnh đó.
        // Ở chế độ storyboard, Script chạy trước bước ghép ảnh và tạo lưới.
        bool gridMode = VideoWorkflowPromptComposer.IsGridMode(config);
        if (ReferenceImageModes.GridSize(config.ReferenceImageMode) > 0 && !singleVideo)
        {
            throw new ProjectWorkflowValidationException(
                "Ảnh storyboard chỉ dùng cho chế độ một video.");
        }
        bool composeFirst = images.ComposeBeforePlanning && existingScenes.Count == 0 && _jobService != null;
        bool prepareSceneFirst = composeFirst || (images.CreateSceneBeforePlanning && existingScenes.Count == 0 && _jobService != null);
        bool needsGridReplan = false;
        if (config.ReferenceImageMode == ReferenceImageModes.StoryboardGrid25 && existingScenes.Count == 1)
        {
            var storyboard = await new StoryboardContentService(_dbOptions).GetAsync(normalizedProjectId, cancellationToken);
            var gridSize = ReferenceImageModes.GridSize(config.ReferenceImageMode);
            needsGridReplan = storyboard?.Shots.Count != gridSize * gridSize || storyboard.DurationSeconds != config.TargetDurationPerSceneSeconds;
        }

        // Kiểm tra mẫu ảnh trước khi gửi kịch bản để báo sớm nếu mẫu đã chọn thiếu biến.
        try
        {
            if (images.GenerationPromptPurpose is { } imagePurpose)
                await VideoWorkflowPromptComposer.RenderImageTemplateAsync(dbContext, imagePurpose, cancellationToken);
            if (gridMode && images.GenerationPromptPurpose != JobTaskType.Image)
                await VideoWorkflowPromptComposer.RenderImageTemplateAsync(dbContext, JobTaskType.Image, cancellationToken);
        }
        catch (PromptEngineException ex)
        {
            throw new ProjectWorkflowValidationException(ex.Message);
        }

        if (prepareSceneFirst)
        {
            var composeScene = new Scene
            {
                Id = Guid.NewGuid().ToString("D").ToLowerInvariant(),
                ProjectId = normalizedProjectId,
                SequenceOrder = 1,
                Name = "Cảnh 1",
                TargetDurationSeconds = config.TargetDurationPerSceneSeconds,
                Status = "Draft",
                AudioAssetId = config.AudioOption == AudioOptions.CustomAudio ? config.AudioAssetId : null,
                CreatedAtUtc = nowIso,
                UpdatedAtUtc = nowIso
            };
            dbContext.Scenes.Add(composeScene);
            // JobService kiểm tra cảnh đã có trong DB trước khi xếp hàng job Image.
            await dbContext.SaveChangesAsync(cancellationToken);
            existingScenes.Add(composeScene);
        }

        // NGUYÊN TẮC MỐC 1 & MỐC 4:
        // - Không dùng 3 cảnh/prompt hardcode mẫu tĩnh giả mạo là kết quả AI production.
        // - Nếu dự án chưa có cảnh nào:
        //   + Nếu không có enqueuer/jobService: từ chối sinh cảnh mẫu cố định.
        //   + Nếu có jobService hoặc enqueuer: Enqueue Job Script (với SceneId = null) theo template đã render.
        int enqueuedJobCount = 0;

        if (existingScenes.Count == 0 || needsGridReplan)
        {
            if (needsGridReplan)
            {
                var markerKey = WorkflowSceneJobs.GridMarkerKey(existingScenes[0].Id);
                var marker = await dbContext.AppSettings.FindAsync([markerKey], cancellationToken);
                if (marker != null) dbContext.AppSettings.Remove(marker);
            }
            if (_durableJobEnqueuer != null)
            {
                enqueuedJobCount = await _durableJobEnqueuer(request, cancellationToken);
            }
            else if (_jobService != null)
            {
                enqueuedJobCount = await EnqueueScriptPlanningJobAsync(dbContext, normalizedProjectId, runConfig, cancellationToken);
            }
            else
            {
                throw new ProjectWorkflowValidationException(
                    "Dự án chưa có cảnh nào để sản xuất video. Hệ thống từ chối sinh cảnh mẫu cố định khi chưa có dịch vụ AI lập cảnh. Vui lòng thêm cảnh cho dự án hoặc cấu hình Provider AI.");
            }
        }
        else
        {
            // Cập nhật audio từ project cho các cảnh hiện có nếu scene chưa có audio riêng
            foreach (var s in existingScenes)
            {
                if (images.CreateSceneBeforePlanning)
                {
                    // Keep candidates/revisions in history, but do not present the previous render as this run's output.
                    s.VideoAssetId = null;
                    s.PromptText = null;
                    s.TargetDurationSeconds = config.TargetDurationPerSceneSeconds;
                    s.Status = "Draft";
                    s.UpdatedAtUtc = nowIso;
                }
                if (config.AudioOption == AudioOptions.CustomAudio && s.AudioAssetId == null && config.AudioAssetId != null)
                {
                    s.AudioAssetId = config.AudioAssetId;
                    s.UpdatedAtUtc = nowIso;
                }
            }

            if (_durableJobEnqueuer != null)
            {
                enqueuedJobCount = await _durableJobEnqueuer(request, cancellationToken);
            }
            else if (_jobService != null)
            {
                enqueuedJobCount = await EnqueueJobsForExistingScenesAsync(dbContext, normalizedProjectId, runConfig, existingScenes, cancellationToken, stageWithRun: true);
            }
        }

        if (enqueuedJobCount <= 0)
        {
            if (existingScenes.Count > 0 && (_jobService != null || _durableJobEnqueuer != null))
            {
                throw new ProjectWorkflowValidationException(
                    "Các cảnh hiện có đã đủ ảnh và prompt nên không còn bước nào để chạy lại. Hãy sửa cảnh cần làm lại, hoặc bấm \"Tạo video\" trong bảng Project để render.");
            }
            throw new ProjectWorkflowValidationException(
                "Chưa thể bắt đầu sản xuất video: Hệ thống chưa có tác vụ nền bền vững nào được tạo trong hàng đợi (chờ kết nối Provider/Scheduler). Hệ thống từ chối chuyển sang trạng thái Đang chạy để tránh treo tiến trình. Dữ liệu cấu hình của bạn đã được bảo toàn.");
        }

        // 4. CHỈ KHI có công việc bền vững thực sự được enqueue, mới chuyển RunState = Running
        var updatedConfig = runConfig with
        {
            RunState = WorkflowRunState.Running,
            CurrentRunId = runId,
            UpdatedAtUtc = nowIso
        };

        var key = GetConfigKey(normalizedProjectId);
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting != null)
        {
            setting.Value = SerializeStoredConfig(updatedConfig);
            setting.UpdatedAtUtc = nowIso;
        }

        var activeRun = new WorkflowRunRecord(
            RunId: runId,
            ProjectId: normalizedProjectId,
            WorkflowMode: runConfig.WorkflowMode,
            GoalPrompt: runConfig.GoalPrompt,
            ModelAssetId: runConfig.ModelAssetId,
            ProductAssetId: runConfig.ProductAssetId,
            ReadyImageAssetId: runConfig.ReadyImageAssetId,
            AudioOption: runConfig.AudioOption,
            AudioAssetId: runConfig.AudioAssetId,
            TargetSceneCount: runConfig.TargetSceneCount,
            TargetDurationPerSceneSeconds: runConfig.TargetDurationPerSceneSeconds,
            RunState: WorkflowRunState.Running,
            ClientRequestId: request.ClientRequestId,
            SelectedSceneIds: existingScenes.Select(s => s.Id).ToList(),
            CreatedAtUtc: nowIso,
            UpdatedAtUtc: nowIso,
            ReferenceVideoAssetId: runConfig.ReferenceVideoAssetId,
            UseReferenceVideoAnalysis: runConfig.UseReferenceVideoAnalysis
        );
        var activeRunKey = $"workflow.active_run.{normalizedProjectId}";
        var activeRunSetting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == activeRunKey, cancellationToken);
        var activeRunJson = JsonSerializer.Serialize(activeRun, JsonOptions);
        if (activeRunSetting == null)
        {
            dbContext.AppSettings.Add(new AppSetting { Key = activeRunKey, Value = activeRunJson, UpdatedAtUtc = nowIso });
        }
        else
        {
            activeRunSetting.Value = activeRunJson;
            activeRunSetting.UpdatedAtUtc = nowIso;
        }

        var prj = await dbContext.Projects.FirstOrDefaultAsync(p => p.Id == normalizedProjectId, cancellationToken);
        if (prj != null)
        {
            prj.Status = "InProgress";
            // Run mới thay thế video cuối cũ (revision giữ làm lịch sử); nếu không, auto-render bị bỏ qua vì FinalVideoAssetId còn đó.
            prj.CurrentVideoRevisionId = null;
            prj.UpdatedAtUtc = nowIso;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        _jobService?.NotifyWorkflowRunReady();

        return new StartProjectWorkflowResponseData(
            RunId: runId,
            RunState: WorkflowRunState.Running,
            CreatedSceneCount: existingScenes.Count,
            Message: $"Đã bắt đầu quy trình tạo video. {enqueuedJobCount} tác vụ bền vững đã được đưa vào hàng đợi xử lý."
        );
    }

    /// <summary>
    /// Tạm dừng quy trình đang chạy (Pause Run).
    /// </summary>
    public async Task<ProjectWorkflowConfigDto> PauseWorkflowAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var config = await GetStoredWorkflowConfigAsync(projectId, cancellationToken);
        var updated = config with
        {
            RunState = WorkflowRunState.Paused,
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o")
        };

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var key = GetConfigKey(config.ProjectId);
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting != null)
        {
            setting.Value = SerializeStoredConfig(updated);
            setting.UpdatedAtUtc = updated.UpdatedAtUtc;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return updated;
    }

    /// <summary>
    /// Tiếp tục quy trình sau khi tạm dừng (Resume Run).
    /// </summary>
    public async Task<ProjectWorkflowConfigDto> ResumeWorkflowAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var normalizedProjectId = NormalizeAndValidateGuid(projectId, nameof(projectId));
        return await RunUnderProjectGateAsync(normalizedProjectId,
            () => ResumeWorkflowLockedAsync(normalizedProjectId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectWorkflowConfigDto> ResumeWorkflowLockedAsync(
        string normalizedProjectId,
        CancellationToken cancellationToken)
    {
        var config = await GetStoredWorkflowConfigAsync(normalizedProjectId, cancellationToken).ConfigureAwait(false);
        var nowIso = DateTimeOffset.UtcNow.ToString("o");
        var updated = config with
        {
            RunState = WorkflowRunState.Running,
            UpdatedAtUtc = nowIso
        };

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var key = GetConfigKey(config.ProjectId);
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (setting != null)
        {
            setting.Value = SerializeStoredConfig(updated);
            setting.UpdatedAtUtc = updated.UpdatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Kích hoạt tiếp các cảnh chưa xong và chưa có active job
        if (_jobService != null)
        {
            var scenes = await dbContext.Scenes
                .Where(s => s.ProjectId == normalizedProjectId)
                .OrderBy(s => s.SequenceOrder)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var activeJobSceneIds = await dbContext.Jobs
                .Where(j => j.ProjectId == normalizedProjectId &&
                            (j.State == JobState.Queued || j.State == JobState.Reserved || j.State == JobState.Running || j.State == JobState.AwaitingUser) &&
                            j.SceneId != null)
                .Select(j => j.SceneId!)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var activeSet = new HashSet<string>(activeJobSceneIds);
            var eligibleScenes = scenes.Where(s => !activeSet.Contains(s.Id)).ToList();
            if (eligibleScenes.Count > 0)
            {
                await EnqueueJobsForExistingScenesAsync(dbContext, normalizedProjectId, updated, eligibleScenes, cancellationToken).ConfigureAwait(false);
            }

            await RunContinuationIntentsLockedAsync(normalizedProjectId, cancellationToken).ConfigureAwait(false);
        }

        _jobService?.NotifyWorkflowRunReady();
        return updated;
    }

    /// <summary>
    /// Hủy bỏ quy trình đang chạy (Cancel Run).
    /// Đổi RunState = Idle và hủy bỏ các tác vụ đang chờ/đang thực thi theo chuẩn T16.
    /// </summary>
    public async Task<ProjectWorkflowConfigDto> CancelWorkflowAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var normalizedProjectId = NormalizeAndValidateGuid(projectId, nameof(projectId));

        // Giữ khóa Project suốt lượt hủy: apply/continuation đang chờ sẽ thấy run đã hủy và không đưa job mới vào hàng đợi.
        return await RunUnderProjectGateAsync(normalizedProjectId,
            () => CancelWorkflowLockedAsync(normalizedProjectId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectWorkflowConfigDto> CancelWorkflowLockedAsync(
        string normalizedProjectId,
        CancellationToken cancellationToken)
    {
        var config = await GetStoredWorkflowConfigAsync(normalizedProjectId, cancellationToken).ConfigureAwait(false);
        var nowIso = DateTimeOffset.UtcNow.ToString("o");

        // Hủy job trước khi báo Idle: job không hủy được thì run dừng ở Paused để đối soát, không báo hủy giả.
        var unresolvedJobIds = await CancelActiveWorkflowJobsAsync(normalizedProjectId, cancellationToken).ConfigureAwait(false);
        var updated = config with
        {
            RunState = unresolvedJobIds.Count == 0 ? WorkflowRunState.Idle : WorkflowRunState.Paused,
            CurrentRunId = null,
            UpdatedAtUtc = nowIso
        };

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var key = GetConfigKey(normalizedProjectId);
        var setting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (setting != null)
        {
            setting.Value = SerializeStoredConfig(updated);
            setting.UpdatedAtUtc = updated.UpdatedAtUtc;
        }

        // Xóa active run record
        var activeRunKey = $"workflow.active_run.{normalizedProjectId}";
        var activeRunSetting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == activeRunKey, cancellationToken).ConfigureAwait(false);
        if (activeRunSetting != null)
        {
            dbContext.AppSettings.Remove(activeRunSetting);
        }

        if (!string.IsNullOrWhiteSpace(config.CurrentRunId))
        {
            var continuationSettings = await dbContext.AppSettings
                .Where(s => s.Key.StartsWith("workflow.continuation."))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var continuationSetting in continuationSettings)
            {
                try
                {
                    var intent = JsonSerializer.Deserialize<WorkflowContinuationIntent>(continuationSetting.Value, JsonOptions);
                    if (intent?.RunId == config.CurrentRunId)
                    {
                        dbContext.AppSettings.Remove(continuationSetting);
                    }
                }
                catch (JsonException)
                {
                    // Keep malformed data for diagnosis.
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (unresolvedJobIds.Count > 0)
        {
            throw new ProjectWorkflowCancelIncompleteException(unresolvedJobIds);
        }

        return updated;
    }

    /// <summary>
    /// Hủy các job queued/reserved/running/awaitingUser của Project (PV-R4). Trả về job chưa hủy được để báo lỗi và đối soát.
    /// </summary>
    private async Task<List<string>> CancelActiveWorkflowJobsAsync(string projectId, CancellationToken cancellationToken)
    {
        var unresolved = new List<string>();
        if (_jobService == null) return unresolved;

        List<string> activeJobIds;
        await using (var jobDb = new FrayloDbContext(_dbOptions))
        {
            activeJobIds = await jobDb.Jobs.AsNoTracking()
                .Where(j => j.ProjectId == projectId && (j.State == JobState.Queued || j.State == JobState.Reserved || j.State == JobState.Running || j.State == JobState.AwaitingUser))
                .Select(j => j.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var jobId in activeJobIds)
        {
            try
            {
                var response = await _jobService.CancelAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (response.State != JobState.Canceled)
                {
                    unresolved.Add(jobId); // Job đã gửi side effect vẫn chạy: chờ đối soát.
                }
            }
            catch (JobConcurrencyConflictException)
            {
                // Job đã kết thúc trước khi hủy: không còn gì để dừng.
            }
            catch (JobNotFoundException)
            {
                // Job đã bị xóa: không còn gì để dừng.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unresolved.Add(jobId);
            }
        }

        return unresolved;
    }

    /// <summary>
    /// Enqueue tác vụ lập cảnh tự động (Job Script) khi dự án chưa có cảnh nào.
    /// Tổng hợp prompt đầy đủ từ template Script đã render qua VideoWorkflowPromptComposer.
    /// </summary>
    private async Task<int> EnqueueScriptPlanningJobAsync(
        FrayloDbContext dbContext,
        string projectId,
        ProjectWorkflowConfigDto config,
        CancellationToken cancellationToken)
    {
        if (_jobService == null) return 0;

        var composer = new VideoWorkflowPromptComposer(_dbOptions);
        var composition = await composer.ComposeScriptPlanningAiRequestAsync(config, cancellationToken).ConfigureAwait(false);

        var enqueueRequest = new JobEnqueueRequest(
            ProjectId: projectId,
            SceneId: null, // Bắt buộc null cho Script theo DB constraint CK_Job_TaskType_Scene
            TaskType: JobTaskType.Script,
            CustomPromptText: composition.ComposedPrompt,
            WorkflowRunId: config.CurrentRunId,
            WorkflowStepKey: "script.planning",
            MediaSnapshot: BuildMediaSnapshot(config)
        );

        var response = await _jobService.StageWorkflowJobAsync(enqueueRequest, dbContext, cancellationToken).ConfigureAwait(false);
        return response != null ? 1 : 0;
    }
}
