using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TiToAI.Application.Jobs;
using TiToAI.Application.Providers;
using TiToAI.Contracts.Content;
using TiToAI.Contracts.Jobs;
using TiToAI.Contracts.Scenes;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;

namespace TiToAI.Application.Content;

public sealed partial class ProjectWorkflowService
{
    // Static: nhiều instance ProjectWorkflowService cùng tiến trình phải dùng chung khóa theo Project.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectRunGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Khóa theo Project cho mọi bước đưa job workflow vào hàng đợi (apply, continuation, render tự động, hủy, tiếp tục).
    /// Bước nào đọc trạng thái run rồi enqueue đều chạy dưới khóa này, nên lượt hủy thấy đủ mọi job đã đưa vào.
    /// Không lồng khóa: hàm đã giữ khóa phải gọi bản "Locked" thay vì bản công khai.
    /// </summary>
    private static async Task<T> RunUnderProjectGateAsync<T>(string projectId, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = ProjectRunGates.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Áp dụng kết quả hợp lệ của một Job hoàn tất thành công (Succeeded) và kích hoạt bước tiếp theo (§3.3).
    /// </summary>
    public async Task<bool> ApplyJobOutputAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return false;

        string? projectId;
        await using (var lookupDb = new TiToAIDbContext(_dbOptions))
        {
            projectId = await lookupDb.Jobs.AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => j.ProjectId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        if (projectId == null) return false;

        return await RunUnderProjectGateAsync(projectId, () => ApplyJobOutputLockedAsync(jobId, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ApplyJobOutputLockedAsync(string jobId, CancellationToken cancellationToken)
    {
        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var job = await dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);
        if (job == null || job.State != JobState.Succeeded)
        {
            return false;
        }

        // Idempotency guard: kiểm tra đã áp dụng kết quả của job này chưa (PV-R3)
        var appliedKey = $"workflow.applied.{job.Id}";
        var appliedSetting = await dbContext.AppSettings
            .FirstOrDefaultAsync(s => s.Key == appliedKey, cancellationToken).ConfigureAwait(false);
        if (appliedSetting != null)
        {
            return true;
        }

        var nowIso = DateTimeOffset.UtcNow.ToString("o");
        var config = await GetWorkflowConfigAsync(job.ProjectId, cancellationToken).ConfigureAwait(false);
        var jobRunId = GetWorkflowRunId(job.ConfigSnapshotJson);
        if (jobRunId != null && !string.Equals(jobRunId, config.CurrentRunId, StringComparison.Ordinal))
        {
            dbContext.AppSettings.Add(new AppSetting
            {
                Key = appliedKey,
                Value = JsonSerializer.Serialize(new { JobId = job.Id, AppliedAtUtc = nowIso, IgnoredAsStaleRun = true }, JsonOptions),
                UpdatedAtUtc = nowIso
            });
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Đánh dấu job đã được apply trong transaction này
        dbContext.AppSettings.Add(new AppSetting
        {
            Key = appliedKey,
            Value = JsonSerializer.Serialize(new { JobId = job.Id, AppliedAtUtc = nowIso }, JsonOptions),
            UpdatedAtUtc = nowIso
        });

        bool isPaused = config.RunState == WorkflowRunState.Paused;
        bool promptRejected = false;
        WorkflowContinuationIntent? continuationIntent = null;
        var composer = new VideoWorkflowPromptComposer(_dbOptions);

        // 1. Áp dụng kết quả Script -> Tự động tạo các Scene trong SQLite
        if (job.TaskType == JobTaskType.Script)
        {
            if (string.IsNullOrWhiteSpace(job.ResultText))
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var existingScenes = await dbContext.Scenes
                .Where(s => s.ProjectId == job.ProjectId)
                .OrderBy(s => s.SequenceOrder)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (existingScenes.Count > 0)
            {
                // Cảnh đã tồn tại (do người dùng tạo hoặc đã sinh trước đó), KHÔNG nhân bản thêm cảnh mới (PV-R3)
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            IReadOnlyList<ScenePlanItemDto> sceneItems;
            if (config.VideoStrategy == VideoStrategies.SingleVideo)
            {
                var storyboard = TryParseStoryboard(job.ResultText, config.TargetDurationPerSceneSeconds);
                if (storyboard != null)
                {
                    await new StoryboardContentService(_dbOptions).SaveAsync(job.ProjectId, job.ResultText, config.TargetDurationPerSceneSeconds, cancellationToken).ConfigureAwait(false);
                }
                sceneItems = storyboard != null ? StoryboardScenePlan(storyboard) : Array.Empty<ScenePlanItemDto>();
            }
            else
            {
                sceneItems = ExtractScenePlanItems(job.ResultText, config.TargetSceneCount, config.TargetDurationPerSceneSeconds);
            }
            if (sceneItems.Count == 0)
            {
                var cfgSetting = await dbContext.AppSettings.FirstOrDefaultAsync(
                    setting => setting.Key == GetConfigKey(job.ProjectId), cancellationToken).ConfigureAwait(false);
                if (cfgSetting != null)
                {
                    cfgSetting.Value = await SerializeStoredConfigAsync(dbContext, config with
                    {
                        RunState = WorkflowRunState.Failed,
                        UpdatedAtUtc = nowIso
                    }, cancellationToken).ConfigureAwait(false);
                    cfgSetting.UpdatedAtUtc = nowIso;
                }
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await WriteAutoRenderLogAsync(job.Id, "error", config.VideoStrategy == VideoStrategies.SingleVideo
                    ? "Phản hồi Script không phải storyboard hợp lệ nên chưa tạo cảnh. Hãy kiểm tra mẫu Script và bắt đầu lại."
                    : $"Phản hồi Script chưa hợp lệ nên chưa tạo cảnh: cần JSON có đúng {Math.Max(1, config.TargetSceneCount)} cảnh và mỗi cảnh có imagePrompt. Hãy kiểm tra mẫu Script và bắt đầu lại.",
                    CancellationToken.None).ConfigureAwait(false);
                return false;
            }
            if (sceneItems.Count > 0)
            {
                var createdScenes = new List<Scene>();
                for (int i = 0; i < sceneItems.Count; i++)
                {
                    var item = sceneItems[i];
                    var newScene = new Scene
                    {
                        Id = Guid.NewGuid().ToString("D").ToLowerInvariant(),
                        ProjectId = job.ProjectId,
                        SequenceOrder = i + 1,
                        Name = item.Name,
                        TargetDurationSeconds = item.TargetDurationSeconds > 0 ? item.TargetDurationSeconds : config.TargetDurationPerSceneSeconds,
                        // Nhiều video: prompt video do Script trả sẵn; ảnh theo cảnh tạo riêng, trừ ReadyImage.
                        PromptText = config.VideoStrategy == VideoStrategies.MultiVideo && !string.IsNullOrWhiteSpace(item.MotionPromptDraft) ? item.MotionPromptDraft : null,
                        ImageAssetId = config.WorkflowMode == WorkflowModes.ReadyImage ? config.ReadyImageAssetId :
                                       config.WorkflowMode == WorkflowModes.ProductOnly && config.VideoStrategy == VideoStrategies.SingleVideo ? config.ProductAssetId : null,
                        AudioAssetId = config.AudioOption == AudioOptions.CustomAudio ? config.AudioAssetId : null,
                        Status = "Draft",
                        CreatedAtUtc = nowIso,
                        UpdatedAtUtc = nowIso
                    };
                    dbContext.Scenes.Add(newScene);
                    dbContext.AppSettings.Add(new AppSetting
                    {
                        Key = $"scene.plan.{newScene.Id}",
                        Value = item.Purpose,
                        UpdatedAtUtc = nowIso
                    });
                    if (config.VideoStrategy == VideoStrategies.MultiVideo)
                    {
                        // Bảng Project đọc hàng từ AppSettings; cảnh do Script tạo phải có hàng để bấm "Tạo video".
                        await SaveOrUpdateContentItemAsync(dbContext, job.ProjectId, new VideoContentItemDto(
                            Id: newScene.Id, ProjectId: job.ProjectId, SceneId: newScene.Id, SequenceOrder: newScene.SequenceOrder,
                            Idea: newScene.Name, Hook: string.Empty, VideoScript: item.Purpose, VideoPrompt: newScene.PromptText ?? string.Empty,
                            TikTokContent: item.TikTokContent, ImageAssetId: newScene.ImageAssetId, VideoAssetId: null, AudioAssetId: newScene.AudioAssetId,
                            Status: newScene.PromptText == null ? "Draft" : "ReadyForVideo", CreatedAtUtc: nowIso, UpdatedAtUtc: nowIso,
                            ContentText: newScene.Name, ImagePrompt: item.Purpose), cancellationToken).ConfigureAwait(false);
                    }
                    createdScenes.Add(newScene);
                }

                // Persist scenes and continuation intents in one commit. A restart can safely replay each intent.
                if (!isPaused && _jobService != null)
                {
                    await EnqueueJobsForExistingScenesAsync(
                        dbContext, job.ProjectId, config, createdScenes, cancellationToken,
                        deferDispatch: true).ConfigureAwait(false);
                    await RunContinuationIntentsLockedAsync(job.ProjectId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            // Nhiều video: cảnh có ảnh và prompt từ Script được render ngay, không chờ bước VideoPrompt.
            if (!isPaused)
            {
                await TryAutoEnqueueVideoRenderAsync(job, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }

        if (string.IsNullOrWhiteSpace(job.SceneId))
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var scene = await dbContext.Scenes.FirstOrDefaultAsync(s => s.Id == job.SceneId, cancellationToken).ConfigureAwait(false);
        if (scene == null)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Lấy metadata setting hiện tại của scene để kiểm tra stale và candidate history
        var sceneKey = GetSceneKey(scene.Id);
        var sceneSetting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == sceneKey, cancellationToken).ConfigureAwait(false);
        SceneWorkflowDetailDto? sceneMeta = null;
        if (sceneSetting != null && !string.IsNullOrWhiteSpace(sceneSetting.Value))
        {
            try { sceneMeta = JsonSerializer.Deserialize<SceneWorkflowDetailDto>(sceneSetting.Value, JsonOptions); } catch { }
        }

        var candidateHistory = sceneMeta?.VideoCandidateHistory != null
            ? new List<string>(sceneMeta.VideoCandidateHistory)
            : new List<string>();
        var staleTaskTypes = new HashSet<string>(sceneMeta?.StaleTaskTypes ?? Array.Empty<string>(), StringComparer.Ordinal);

        bool isUserModified = string.CompareOrdinal(scene.UpdatedAtUtc, job.CreatedAtUtc) > 0;
        if (isUserModified)
        {
            if (job.TaskType == JobTaskType.VideoRender && !string.IsNullOrWhiteSpace(job.ResultReference) &&
                !candidateHistory.Contains(job.ResultReference))
            {
                candidateHistory.Add(job.ResultReference);
            }

            var staleDetail = new SceneWorkflowDetailDto(
                SceneId: scene.Id,
                ProjectId: scene.ProjectId,
                SequenceOrder: scene.SequenceOrder,
                Name: scene.Name,
                TargetDurationSeconds: Convert.ToInt32(scene.TargetDurationSeconds),
                WorkflowStatus: SceneWorkflowStatus.NeedsReview,
                ImageStrategy: sceneMeta?.ImageStrategy ?? SceneImageStrategy.ReuseFoundation,
                SourceImageAssetId: scene.ImageAssetId,
                SelectedImageAssetId: scene.ImageAssetId,
                MotionPromptText: scene.PromptText,
                AudioOption: scene.AudioAssetId != null ? AudioOptions.CustomAudio : AudioOptions.None,
                AudioAssetId: scene.AudioAssetId,
                VideoAssetId: scene.VideoAssetId,
                IsStale: true,
                StaleReason: "Đầu vào đã thay đổi khi tác vụ đang chạy. Kết quả được giữ trong lịch sử để người dùng quyết định.",
                ActiveJobId: null,
                ActiveJobType: null,
                ActiveJobState: null,
                ActiveJobProgressPercent: null,
                VideoCandidateHistory: candidateHistory,
                Revision: nowIso,
                StaleTaskTypes: staleTaskTypes.ToArray());
            var staleJson = JsonSerializer.Serialize(staleDetail, JsonOptions);
            if (sceneSetting == null)
            {
                dbContext.AppSettings.Add(new AppSetting { Key = sceneKey, Value = staleJson, UpdatedAtUtc = nowIso });
            }
            else
            {
                sceneSetting.Value = staleJson;
                sceneSetting.UpdatedAtUtc = nowIso;
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        // 2. Áp dụng kết quả Image -> Lưu ảnh và kích hoạt VideoPrompt
        if (job.TaskType == JobTaskType.Image)
        {
            // Kiểm tra stale: nếu user đã chỉnh sửa/chọn ảnh khác trong khi job đang chạy
            // Kết quả của job này chuyển vào candidateHistory thay vì ghi đè ảnh hiện tại (PV-R3)
            if (isUserModified && !string.IsNullOrWhiteSpace(scene.ImageAssetId) &&
                !string.Equals(scene.ImageAssetId, job.ResultReference, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(job.ResultReference) && !candidateHistory.Contains(job.ResultReference))
                {
                    candidateHistory.Add(job.ResultReference);
                }
            }
            else
            {
                scene.ImageAssetId = job.ResultReference;
                scene.UpdatedAtUtc = nowIso;
            }
            staleTaskTypes.Remove(JobTaskType.Image);

            // Kích hoạt bước tiếp theo: VideoPrompt
            // Chú ý: Kể cả khi scene.PromptText đã có draft từ Script, VẪN enqueue VideoPrompt
            // để AI writer hoàn thiện thành motion prompt đầy đủ với template đã render (§3.3, PV-R5, PV-R11).
            // Nhiều video đã có prompt từ Script thì bỏ qua bước này (VIDEO_TWO_FLOW_SPLIT §3.2).
            if (!isPaused && (config.VideoStrategy == VideoStrategies.SingleVideo || string.IsNullOrWhiteSpace(scene.PromptText)))
            {
                var composition = await composer.ComposeVideoPromptAiRequestAsync(
                    config,
                    scene,
                    cancellationToken).ConfigureAwait(false);

                continuationIntent = new WorkflowContinuationIntent(
                    IntentId: Guid.NewGuid().ToString("D").ToLowerInvariant(),
                    ProjectId: job.ProjectId,
                    SceneId: scene.Id,
                    TaskType: JobTaskType.VideoPrompt,
                    CustomPromptText: composition.ComposedPrompt,
                    MediaSnapshot: BuildMediaSnapshot(config, scene),
                    RunId: config.CurrentRunId ?? string.Empty,
                    CreatedAtUtc: nowIso,
                    WorkflowStepKey: $"prompt.{scene.Id}.{scene.UpdatedAtUtc}"
                );
            }
        }
        // 3. Áp dụng kết quả VideoPrompt -> Lưu prompt và kích hoạt VideoRender
        else if (job.TaskType == JobTaskType.VideoPrompt)
        {
            var contentParsed = ContentCandidateParser.ParseVideoContent(
                job.ResultText ?? string.Empty,
                job.ProjectId,
                scene.Id,
                scene.SequenceOrder,
                scene.ImageAssetId);

            var contentItem = contentParsed.Candidate;
            // Chỉ phản hồi đạt thẩm định mới thành prompt render; phản hồi lỗi giữ trong bảng để người dùng sửa.
            promptRejected = !contentParsed.IsCompliant || string.IsNullOrWhiteSpace(contentItem?.VideoPrompt);

            // Kiểm tra stale: nếu user đã tự tay sửa PromptText trong khi job VideoPrompt đang chạy
            // Kết quả này không được ghi đè prompt đã sửa của user (PV-R3)
            if (isUserModified && !string.IsNullOrWhiteSpace(scene.PromptText) &&
                !string.Equals(scene.PromptText, job.ResultText, StringComparison.Ordinal))
            {
                // Giữ nguyên prompt của người dùng
            }
            else if (!promptRejected)
            {
                scene.PromptText = contentItem!.VideoPrompt;
                if (!string.IsNullOrWhiteSpace(contentItem.Idea) && scene.Name.StartsWith("Cảnh "))
                {
                    scene.Name = contentItem.Idea;
                }
                scene.UpdatedAtUtc = nowIso;
            }
            // Phản hồi bị loại giữ cờ stale: prompt còn lại (nếu có) là của lượt trước và không được dùng để render.
            if (promptRejected)
            {
                staleTaskTypes.Add(JobTaskType.VideoPrompt);
            }
            else
            {
                staleTaskTypes.Remove(JobTaskType.VideoPrompt);
            }

            // Lưu bản ghi VideoContentItem vào AppSettings để hiển thị dạng bảng
            if (contentItem != null)
            {
                await SaveOrUpdateContentItemAsync(dbContext, job.ProjectId, contentItem with
                {
                    ImageAssetId = scene.ImageAssetId,
                    // Prompt bị AI từ chối không được giữ lại trong bảng, tránh render nhầm prompt cũ.
                    VideoPrompt = promptRejected ? string.Empty : scene.PromptText ?? contentItem.VideoPrompt,
                    Status = promptRejected ? "NeedsReview" : "ReadyForVideo",
                    UpdatedAtUtc = nowIso
                }, cancellationToken).ConfigureAwait(false);
            }

            // The selected image and motion prompt stay persisted; the render is enqueued once after the commit below.
        }
        // 4. Áp dụng kết quả VideoRender -> Lưu video và kiểm tra hoàn tất run
        else if (job.TaskType == JobTaskType.VideoRender)
        {
            scene.VideoAssetId = job.ResultReference;
            scene.Status = "Completed";
            scene.UpdatedAtUtc = nowIso;

            // Publish only a complete, linked video Asset as the Project's selected revision.
            // Chỉ Project một cảnh (luồng một video) mới có video cuối; nhiều video thì mỗi cảnh giữ clip của mình.
            var projectScenes = await dbContext.Scenes.Where(s => s.ProjectId == job.ProjectId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var hasFinalVideo = false;
            if (projectScenes.Count == 1 && !string.IsNullOrWhiteSpace(job.ResultReference))
            {
                var outputAsset = await dbContext.Assets.FirstOrDefaultAsync(
                    a => a.Id == job.ResultReference && a.AssetType == "Video" && a.Status == "Linked",
                    cancellationToken).ConfigureAwait(false);
                var project = await dbContext.Projects.FirstOrDefaultAsync(
                    p => p.Id == job.ProjectId, cancellationToken).ConfigureAwait(false);
                if (outputAsset != null && project != null)
                {
                    hasFinalVideo = true;
                    var revisionNumber = await dbContext.VideoRevisions
                        .Where(v => v.ProjectId == project.Id)
                        .Select(v => (int?)v.Revision)
                        .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
                    var videoRevision = new VideoRevision
                    {
                        Id = Guid.NewGuid().ToString("D").ToLowerInvariant(),
                        ProjectId = project.Id,
                        Revision = revisionNumber + 1,
                        State = "Ready",
                        AssetId = outputAsset.Id,
                        ContentHash = outputAsset.Sha256Hash,
                        CreatedAtUtc = nowIso
                    };
                    dbContext.VideoRevisions.Add(videoRevision);
                    project.CurrentVideoRevisionId = videoRevision.Id;
                    project.Revision += 1;
                    project.UpdatedAtUtc = nowIso;
                }
            }

            // Cập nhật trạng thái hoàn tất vào VideoContentItem
            await UpdateContentItemVideoStatusAsync(dbContext, job.ProjectId, scene.Id, job.ResultReference, "Completed", nowIso, cancellationToken).ConfigureAwait(false);

            // Xóa cờ Stale khi đã có video mới khớp cấu hình
            if (sceneMeta != null && sceneMeta.IsStale)
            {
                sceneMeta = sceneMeta with { IsStale = false, StaleReason = null, StaleTaskTypes = Array.Empty<string>() };
            }
            staleTaskTypes.Clear();

            if (!string.IsNullOrWhiteSpace(job.ResultReference) && !candidateHistory.Contains(job.ResultReference))
            {
                candidateHistory.Add(job.ResultReference);
            }

            // Nhiều video: run hoàn tất khi mọi cảnh đã có video. Scene đã tracked nên VideoAssetId vừa gán có hiệu lực.
            var multiVideoComplete = config.VideoStrategy == VideoStrategies.MultiVideo &&
                                     projectScenes.All(s => !string.IsNullOrWhiteSpace(s.VideoAssetId));
            if (hasFinalVideo || multiVideoComplete)
            {
                var updatedConfig = config with
                {
                    RunState = WorkflowRunState.Completed,
                    UpdatedAtUtc = nowIso
                };
                var cfgKey = GetConfigKey(job.ProjectId);
                var cfgSetting = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == cfgKey, cancellationToken).ConfigureAwait(false);
                if (cfgSetting != null)
                {
                    cfgSetting.Value = await SerializeStoredConfigAsync(dbContext, updatedConfig, cancellationToken).ConfigureAwait(false);
                    cfgSetting.UpdatedAtUtc = nowIso;
                }
            }
        }

        // Cập nhật lại Scene AppSetting chứa candidateHistory mới nhất
        var updatedDetail = new SceneWorkflowDetailDto(
            SceneId: scene.Id,
            ProjectId: scene.ProjectId,
            SequenceOrder: scene.SequenceOrder,
            Name: scene.Name,
            TargetDurationSeconds: Convert.ToInt32(scene.TargetDurationSeconds),
            WorkflowStatus: staleTaskTypes.Count > 0 ? SceneWorkflowStatus.NeedsReview :
                            !string.IsNullOrWhiteSpace(scene.VideoAssetId) ? SceneWorkflowStatus.VideoCompleted :
                            !string.IsNullOrWhiteSpace(scene.ImageAssetId) && !string.IsNullOrWhiteSpace(scene.PromptText) ? SceneWorkflowStatus.ReadyForVideo :
                            !string.IsNullOrWhiteSpace(scene.ImageAssetId) ? SceneWorkflowStatus.PreparingPrompt : SceneWorkflowStatus.PreparingImage,
            ImageStrategy: sceneMeta?.ImageStrategy ?? SceneImageStrategy.ReuseFoundation,
            SourceImageAssetId: scene.ImageAssetId,
            SelectedImageAssetId: scene.ImageAssetId,
            MotionPromptText: scene.PromptText,
            AudioOption: scene.AudioAssetId != null ? AudioOptions.CustomAudio : AudioOptions.None,
            AudioAssetId: scene.AudioAssetId,
            VideoAssetId: scene.VideoAssetId,
            IsStale: staleTaskTypes.Count > 0,
            StaleReason: staleTaskTypes.Count > 0 ? (sceneMeta?.StaleReason ?? (promptRejected ? PromptRejectedStaleReason : null)) : null,
            ActiveJobId: null,
            ActiveJobType: null,
            ActiveJobState: null,
            ActiveJobProgressPercent: null,
            VideoCandidateHistory: candidateHistory,
            Revision: nowIso,
            StaleTaskTypes: staleTaskTypes.ToArray()
        );

        var updatedDetailJson = JsonSerializer.Serialize(updatedDetail, JsonOptions);
        if (sceneSetting == null)
        {
            dbContext.AppSettings.Add(new AppSetting { Key = sceneKey, Value = updatedDetailJson, UpdatedAtUtc = nowIso });
        }
        else
        {
            sceneSetting.Value = updatedDetailJson;
            sceneSetting.UpdatedAtUtc = nowIso;
        }

        // Nếu có continuation intent: commit vào AppSettings trong cùng một transaction (PV-R7)
        AppSetting? intentSetting = null;
        if (continuationIntent != null)
        {
            intentSetting = new AppSetting
            {
                Key = $"workflow.continuation.{continuationIntent.IntentId}",
                Value = JsonSerializer.Serialize(continuationIntent, JsonOptions),
                UpdatedAtUtc = nowIso
            };
            dbContext.AppSettings.Add(intentSetting);
        }

        // ATOMIC TRANSACTION: Commit scene changes + applied marker + continuation intent
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await new JobActivityLog(_dbOptions).WriteAsync(job.Id, "result", "Đã lưu kết quả vào CSDL và cập nhật bảng dự án.", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nhật ký chỉ phục vụ theo dõi; lỗi ghi log không được chặn dispatch continuation đã commit.
        }
        if (promptRejected)
        {
            await WriteAutoRenderLogAsync(job.Id, "error", "Phản hồi chưa có prompt video hợp lệ nên chưa đưa vào render. Hãy kiểm tra và sửa nội dung trong bảng Project.", CancellationToken.None).ConfigureAwait(false);
        }

        // Dispatch continuation intent nếu có
        if (continuationIntent != null && _jobService != null)
        {
            try
            {
                await _jobService.EnqueueAsync(new JobEnqueueRequest(
                    ProjectId: continuationIntent.ProjectId,
                    SceneId: continuationIntent.SceneId,
                    TaskType: continuationIntent.TaskType,
                    CustomPromptText: continuationIntent.CustomPromptText,
                    WorkflowRunId: continuationIntent.RunId,
                    WorkflowStepKey: continuationIntent.WorkflowStepKey ?? GetWorkflowStepKey(continuationIntent.TaskType, continuationIntent.SceneId),
                    MediaSnapshot: continuationIntent.MediaSnapshot
                ), cancellationToken).ConfigureAwait(false);

                // Đã dispatch thành công -> xóa intent
                if (intentSetting != null)
                {
                    dbContext.AppSettings.Remove(intentSetting);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                // Nếu dispatch bị lỗi/crash, intent vẫn tồn tại trong DB và sẽ được phục hồi bởi RunWorkflowContinuationSweepAsync
            }
        }

        if ((job.TaskType == JobTaskType.VideoPrompt || job.TaskType == JobTaskType.Image) && !isPaused)
        {
            await TryAutoEnqueueVideoRenderAsync(job, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Khi mọi phân cảnh đã có ảnh và prompt, đưa render một lần cho cả Project vào hàng đợi (MuseAI).
    /// Khóa AppSettings theo run đảm bảo chỉ một lượt apply gửi render; bấm "Tạo video" vẫn là đường thủ công.
    /// </summary>
    private const int AutoRenderMaxRetries = 3;

    private const string PromptRejectedStaleReason =
        "AI từ chối hoặc phản hồi thiếu prompt video hợp lệ. Prompt cũ (nếu có) không được render cho đến khi tạo lại.";

    private Task TryAutoEnqueueVideoRenderAsync(Job job, CancellationToken cancellationToken) =>
        TryAutoEnqueueVideoRenderForProjectAsync(job.ProjectId, GetWorkflowRunId(job.ConfigSnapshotJson) ?? "none", job.Id, cancellationToken);

    /// <summary>
    /// Khởi động lại app: nếu run đang chạy mà render tự động chưa được đưa vào hàng đợi (app dừng sau khi apply VideoPrompt,
    /// hoặc lần enqueue trước lỗi và đã bỏ khóa), thử lại. Khóa theo run vẫn ngăn gửi trùng.
    /// </summary>
    public async Task<int> RunAutoRenderRecoverySweepAsync(CancellationToken cancellationToken = default)
    {
        if (!MuseAiTrialMode.Enabled || _jobService == null) return 0;

        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var configKeys = await dbContext.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("project.workflow.config.") && s.Value.Contains("\"runState\":\"Running\""))
            .Select(s => s.Key)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var recovered = 0;
        foreach (var key in configKeys)
        {
            var projectId = key["project.workflow.config.".Length..];
            try
            {
                // Đọc lại cấu hình dưới khóa: lượt hủy có thể vừa kết thúc run.
                recovered += await RunUnderProjectGateAsync(projectId, async () =>
                {
                    var config = await GetWorkflowConfigAsync(projectId, cancellationToken).ConfigureAwait(false);
                    if (config.RunState != WorkflowRunState.Running || string.IsNullOrWhiteSpace(config.CurrentRunId) || config.FinalVideoAssetId != null)
                    {
                        return 0;
                    }
                    await TryAutoEnqueueVideoRenderForProjectAsync(projectId, config.CurrentRunId, null, cancellationToken).ConfigureAwait(false);
                    return 1;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Một Project lỗi không chặn các Project khác; lần khởi động sau sẽ thử lại.
            }
        }
        return recovered;
    }

    /// <summary>
    /// Đưa render một lần cho cả Project khi mọi phân cảnh đã có ảnh và prompt. Khóa AppSettings theo run chặn gửi trùng.
    /// Nếu enqueue lỗi, khóa được bỏ để lượt apply hoặc lần khởi động sau thử lại.
    /// </summary>
    private async Task TryAutoEnqueueVideoRenderForProjectAsync(string projectId, string runId, string? logJobId, CancellationToken cancellationToken, int attempt = 0)
    {
        if (!MuseAiTrialMode.Enabled || _jobService == null) return;

        // Callback retry có thể đến sau khi run đã kết thúc hoặc đã chuyển sang run khác: không tạo render mới.
        var config = await GetWorkflowConfigAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (config.RunState != WorkflowRunState.Running || config.CurrentRunId != runId || config.FinalVideoAssetId != null) return;

        if (config.VideoStrategy == VideoStrategies.MultiVideo)
        {
            await TryAutoEnqueueSceneRendersAsync(projectId, runId, logJobId, cancellationToken, attempt).ConfigureAwait(false);
            return;
        }

        var claimKey = $"workflow.auto_render.{projectId}.{runId}";

        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var scenes = await dbContext.Scenes.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var allReady = scenes.Count > 0 && scenes.All(s => !string.IsNullOrWhiteSpace(s.ImageAssetId) && !string.IsNullOrWhiteSpace(s.PromptText));
        var anyPendingPrep = await dbContext.Jobs.AsNoTracking().AnyAsync(j =>
            j.ProjectId == projectId &&
            (j.TaskType == JobTaskType.Image || j.TaskType == JobTaskType.VideoPrompt) &&
            (j.State == JobState.Queued || j.State == JobState.Reserved || j.State == JobState.Running || j.State == JobState.AwaitingUser),
            cancellationToken).ConfigureAwait(false);
        var anyPendingRender = await dbContext.Jobs.AsNoTracking().AnyAsync(j =>
            j.ProjectId == projectId &&
            j.TaskType == JobTaskType.VideoRender &&
            (j.State == JobState.Queued || j.State == JobState.Reserved || j.State == JobState.Running || j.State == JobState.AwaitingUser),
            cancellationToken).ConfigureAwait(false);
        // Phân cảnh có prompt video bị đánh dấu stale (AI từ chối, phản hồi lỗi) thì không render, kể cả prompt cũ còn trong bảng.
        var sceneKeys = scenes.Select(s => GetSceneKey(s.Id)).ToList();
        var sceneSettings = await dbContext.AppSettings.AsNoTracking()
            .Where(s => sceneKeys.Contains(s.Key))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasStalePrompt = sceneSettings.Any(setting => IsPromptStale(setting.Value));
        if (hasStalePrompt && logJobId != null)
        {
            await WriteAutoRenderLogAsync(logJobId, "error", "Chưa tự render: có phân cảnh cần xem lại prompt video. Hãy sửa hoặc tạo lại prompt trong bảng Project.", cancellationToken).ConfigureAwait(false);
        }
        if (!allReady || anyPendingPrep || anyPendingRender || hasStalePrompt) return;

        if (!await TryClaimAutoRenderAsync(claimKey, projectId, runId, null, cancellationToken).ConfigureAwait(false))
        {
            return; // Lượt apply khác của cùng run đã giữ khóa và đã tạo job.
        }

        try
        {
            await CreateSceneVideoAsync(new CreateSceneVideoRequest(projectId), cancellationToken).ConfigureAwait(false);
            await WriteAutoRenderLogAsync(logJobId, "result", "Đã tự đưa render MuseAI vào hàng đợi cho Project.", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ReleaseAutoRenderClaimAsync(claimKey, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await ReleaseAutoRenderClaimAsync(claimKey, CancellationToken.None).ConfigureAwait(false);
            await WriteAutoRenderLogAsync(logJobId, "error", $"Chưa tự động tạo video: {ex.Message}. Sẽ thử lại tự động; có thể bấm Tạo video.", CancellationToken.None).ConfigureAwait(false);
            ScheduleAutoRenderRetry(projectId, runId, attempt);
        }
    }

    /// <summary>
    /// Nhiều video: mỗi cảnh đã có ảnh và prompt, chưa có video và không có job đang chờ thì đưa render riêng cho cảnh đó.
    /// Khóa AppSettings theo cảnh chặn gửi trùng; lỗi enqueue thì bỏ khóa và lên lịch thử lại.
    /// </summary>
    private async Task TryAutoEnqueueSceneRendersAsync(string projectId, string runId, string? logJobId, CancellationToken cancellationToken, int attempt)
    {
        List<Scene> scenes;
        HashSet<string> busySceneIds;
        HashSet<string> staleSceneIds;
        await using (var dbContext = new TiToAIDbContext(_dbOptions))
        {
            scenes = await dbContext.Scenes.AsNoTracking()
                .Where(s => s.ProjectId == projectId)
                .OrderBy(s => s.SequenceOrder)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            busySceneIds = (await dbContext.Jobs.AsNoTracking().Where(j =>
                    j.ProjectId == projectId && j.SceneId != null &&
                    (j.TaskType == JobTaskType.Image || j.TaskType == JobTaskType.VideoPrompt || j.TaskType == JobTaskType.VideoRender) &&
                    (j.State == JobState.Queued || j.State == JobState.Reserved || j.State == JobState.Running || j.State == JobState.AwaitingUser))
                .Select(j => j.SceneId!)
                .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            var sceneKeys = scenes.Select(s => GetSceneKey(s.Id)).ToList();
            var sceneSettings = await dbContext.AppSettings.AsNoTracking()
                .Where(s => sceneKeys.Contains(s.Key))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            staleSceneIds = sceneSettings.Where(s => IsPromptStale(s.Value))
                .Select(s => s.Key["scene.workflow.".Length..]).ToHashSet();
        }

        foreach (var scene in scenes)
        {
            if (scene.VideoAssetId != null || string.IsNullOrWhiteSpace(scene.ImageAssetId) || string.IsNullOrWhiteSpace(scene.PromptText)) continue;
            if (busySceneIds.Contains(scene.Id)) continue;
            if (staleSceneIds.Contains(scene.Id))
            {
                await WriteAutoRenderLogAsync(logJobId, "error", $"Chưa tự render cảnh {scene.SequenceOrder}: prompt video cần xem lại.", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var claimKey = $"workflow.auto_render.{projectId}.{runId}.{scene.Id}";
            if (!await TryClaimAutoRenderAsync(claimKey, projectId, runId, scene.Id, cancellationToken).ConfigureAwait(false))
            {
                continue; // Lượt apply khác đã giữ khóa của cảnh này và đã tạo job.
            }

            try
            {
                await CreateSceneVideoAsync(new CreateSceneVideoRequest(projectId, SceneId: scene.Id), cancellationToken).ConfigureAwait(false);
                await WriteAutoRenderLogAsync(logJobId, "result", $"Đã tự đưa render MuseAI cảnh {scene.SequenceOrder} vào hàng đợi.", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReleaseAutoRenderClaimAsync(claimKey, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await ReleaseAutoRenderClaimAsync(claimKey, CancellationToken.None).ConfigureAwait(false);
                await WriteAutoRenderLogAsync(logJobId, "error", $"Chưa tự động tạo video cảnh {scene.SequenceOrder}: {ex.Message}. Sẽ thử lại tự động; có thể bấm Tạo video.", CancellationToken.None).ConfigureAwait(false);
                ScheduleAutoRenderRetry(projectId, runId, attempt);
            }
        }
    }

    /// <summary>
    /// Thử lại render tự động sau lỗi enqueue, lùi dần 30s/60s/90s. Quá số lần thì để lượt apply, khởi động lại hoặc nút "Tạo video" xử lý.
    /// </summary>
    private static bool IsPromptStale(string? sceneSettingJson)
    {
        if (string.IsNullOrWhiteSpace(sceneSettingJson)) return false;
        try
        {
            var detail = JsonSerializer.Deserialize<SceneWorkflowDetailDto>(sceneSettingJson, JsonOptions);
            return detail?.StaleTaskTypes?.Contains(JobTaskType.VideoPrompt) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ScheduleAutoRenderRetry(string projectId, string runId, int attempt)
    {
        if (attempt >= AutoRenderMaxRetries) return;
        var delay = TimeSpan.FromSeconds(30 * (attempt + 1));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                await RunUnderProjectGateAsync(projectId, async () =>
                {
                    await TryAutoEnqueueVideoRenderForProjectAsync(projectId, runId, null, CancellationToken.None, attempt + 1).ConfigureAwait(false);
                    return 0;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Lượt thử kế tiếp hoặc lần khởi động sau sẽ xử lý.
            }
        });
    }

    /// <summary>
    /// Giữ khóa render tự động của run (hoặc cảnh). Khóa đã có nhưng không có job render nào của run thì app đã dừng
    /// giữa lúc giữ khóa và lúc tạo job: bỏ khóa mồ côi và giữ lại. Gọi dưới khóa Project nên không có lượt khác đang tạo job.
    /// </summary>
    private async Task<bool> TryClaimAutoRenderAsync(string claimKey, string projectId, string runId, string? sceneId, CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            await using (var claimDb = new TiToAIDbContext(_dbOptions))
            {
                claimDb.AppSettings.Add(new AppSetting { Key = claimKey, Value = runId, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o") });
                try
                {
                    await claimDb.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (DbUpdateException)
                {
                    // Khóa đã tồn tại: kiểm tra bên dưới.
                }
            }

            await using var checkDb = new TiToAIDbContext(_dbOptions);
            var hasRunJob = await checkDb.Jobs.AsNoTracking().AnyAsync(j =>
                j.ProjectId == projectId &&
                j.TaskType == JobTaskType.VideoRender &&
                (sceneId == null || j.SceneId == sceneId) &&
                j.ConfigSnapshotJson != null && j.ConfigSnapshotJson.Contains(runId),
                cancellationToken).ConfigureAwait(false);
            if (hasRunJob) return false;

            await ReleaseAutoRenderClaimAsync(claimKey, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private async Task ReleaseAutoRenderClaimAsync(string claimKey, CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = new TiToAIDbContext(_dbOptions);
            var claim = await dbContext.AppSettings.FirstOrDefaultAsync(s => s.Key == claimKey, cancellationToken).ConfigureAwait(false);
            if (claim is null) return;
            dbContext.AppSettings.Remove(claim);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Khóa còn lại chỉ làm chậm lượt thử sau; không che lỗi enqueue gốc.
        }
    }

    private async Task WriteAutoRenderLogAsync(string? jobId, string kind, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return;
        try
        {
            await new JobActivityLog(_dbOptions).WriteAsync(jobId, kind, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Nhật ký chỉ phục vụ theo dõi. */
        }
    }

    /// <summary>
    /// Quét và tiếp tục thực hiện các ý định chuyển tiếp (Continuation Intents) chưa hoàn tất sau khi ứng dụng crash hoặc khởi động lại (PV-R7).
    /// </summary>
    public async Task<int> RunWorkflowContinuationSweepAsync(CancellationToken cancellationToken = default)
    {
        if (_jobService == null) return 0;

        var projectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var listDb = new TiToAIDbContext(_dbOptions))
        {
            var intentValues = await listDb.AppSettings.AsNoTracking()
                .Where(s => s.Key.StartsWith("workflow.continuation."))
                .Select(s => s.Value)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var value in intentValues)
            {
                try
                {
                    var intent = JsonSerializer.Deserialize<WorkflowContinuationIntent>(value, JsonOptions);
                    if (intent != null) projectIds.Add(intent.ProjectId);
                }
                catch (JsonException)
                {
                    // Giữ lại intent lỗi để chẩn đoán.
                }
            }
        }

        int resumed = 0;
        foreach (var projectId in projectIds)
        {
            resumed += await RunUnderProjectGateAsync(projectId,
                () => RunContinuationIntentsLockedAsync(projectId, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        return resumed;
    }

    /// <summary>Đưa vào hàng đợi các intent chuyển tiếp của một Project. Chỉ gọi khi đang giữ khóa Project.</summary>
    private async Task<int> RunContinuationIntentsLockedAsync(string projectId, CancellationToken cancellationToken)
    {
        if (_jobService == null) return 0;

        await using var dbContext = new TiToAIDbContext(_dbOptions);
        var pendingIntentSettings = await dbContext.AppSettings
            .Where(s => s.Key.StartsWith("workflow.continuation."))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (pendingIntentSettings.Count == 0) return 0;

        int resumed = 0;
        foreach (var setting in pendingIntentSettings)
        {
            try
            {
                var intent = JsonSerializer.Deserialize<WorkflowContinuationIntent>(setting.Value, JsonOptions);
                if (intent != null && string.Equals(intent.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                {
                    var config = await GetWorkflowConfigAsync(intent.ProjectId, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(config.CurrentRunId, intent.RunId, StringComparison.Ordinal) ||
                        config.RunState is WorkflowRunState.Idle or WorkflowRunState.Failed or WorkflowRunState.Completed)
                    {
                        dbContext.AppSettings.Remove(setting);
                        resumed++;
                        continue;
                    }

                    if (config.RunState == WorkflowRunState.Paused)
                    {
                        continue;
                    }

                    await _jobService.EnqueueAsync(new JobEnqueueRequest(
                        ProjectId: intent.ProjectId,
                        SceneId: intent.SceneId,
                        TaskType: intent.TaskType,
                        CustomPromptText: intent.CustomPromptText,
                        WorkflowRunId: intent.RunId,
                        WorkflowStepKey: intent.WorkflowStepKey ?? GetWorkflowStepKey(intent.TaskType, intent.SceneId),
                        MediaSnapshot: intent.MediaSnapshot
                    ), cancellationToken).ConfigureAwait(false);

                    dbContext.AppSettings.Remove(setting);
                    resumed++;
                }
            }
            catch
            {
                // Giữ lại intent để thử lại lần sau nếu có lỗi tạm thời
            }
        }

        if (resumed > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return resumed;
    }

    public async Task<int> RunWorkflowApplySweepAsync(CancellationToken cancellationToken = default)
    {
        if (_jobService == null) return 0;

        await using var dbContext = new TiToAIDbContext(_dbOptions);

        // Một truy vấn lấy toàn bộ marker đã áp dụng; lịch sử job lớn không được kéo theo một truy vấn cho mỗi job khi mở app.
        var appliedKeys = new HashSet<string>(
            await dbContext.AppSettings.AsNoTracking()
                .Where(setting => setting.Key.StartsWith("workflow.applied."))
                .Select(setting => setting.Key)
                .ToListAsync(cancellationToken).ConfigureAwait(false),
            StringComparer.Ordinal);

        var succeededJobs = await dbContext.Jobs.AsNoTracking()
            .Where(job => job.State == JobState.Succeeded && job.ConfigSnapshotJson != null)
            .Select(job => new { job.Id, job.ConfigSnapshotJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        int applied = 0;
        foreach (var job in succeededJobs)
        {
            if (appliedKeys.Contains($"workflow.applied.{job.Id}")) continue;
            if (GetWorkflowRunId(job.ConfigSnapshotJson) == null) continue;
            if (await ApplyJobOutputAsync(job.Id, cancellationToken).ConfigureAwait(false))
            {
                applied++;
            }
        }

        return applied;
    }

    private static string? GetWorkflowRunId(string? configSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(configSnapshotJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(configSnapshotJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("WorkflowRunId", out var runId) && !root.TryGetProperty("workflowRunId", out runId))
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

    private static string GetWorkflowStepKey(string taskType, string? sceneId) => taskType switch
    {
        JobTaskType.Script => "script.planning",
        JobTaskType.Image => $"image.{sceneId}",
        JobTaskType.VideoPrompt => $"prompt.{sceneId}",
        JobTaskType.VideoRender => $"render.{sceneId}",
        _ => $"{taskType.ToLowerInvariant()}.{sceneId}"
    };

    /// <summary>Storyboard hợp lệ thì trả về; kết quả Script không phải storyboard sẽ đi theo đường scenes cũ.</summary>
    private static StoryboardContent? TryParseStoryboard(string resultText, int durationSeconds)
    {
        try
        {
            return StoryboardContentService.Parse(resultText, durationSeconds);
        }
        catch (ProjectWorkflowValidationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Luồng một video: cả storyboard là một phân cảnh. Mốc thời gian nằm trong purpose để prompt video giữ thứ tự các đoạn.
    /// </summary>
    private static IReadOnlyList<ScenePlanItemDto> StoryboardScenePlan(StoryboardContent storyboard) =>
        new[]
        {
            new ScenePlanItemDto(
                SequenceOrder: 1,
                Name: "Video",
                Purpose: string.Join("\n", storyboard.Shots.Select(shot => $"{shot.StartSeconds}–{shot.EndSeconds} giây: {shot.Description}")),
                TargetDurationSeconds: storyboard.DurationSeconds,
                ImageStrategy: SceneImageStrategy.ReuseFoundation,
                MotionPromptDraft: string.Empty)
        };

    /// <summary>
    /// Luồng nhiều video: Script trả đúng <paramref name="expectedCount"/> cảnh, mỗi cảnh có imagePrompt (bắt buộc)
    /// và videoPrompt (không có thì cảnh đi qua job VideoPrompt như trước).
    /// </summary>
    private static IReadOnlyList<ScenePlanItemDto> ExtractScenePlanItems(string resultText, int expectedCount, int durationSeconds)
    {
        var cleaned = resultText.Trim();
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(7);
        }
        if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(3);
        }
        if (cleaned.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 3);
        }
        cleaned = cleaned.Trim();

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            if (!doc.RootElement.TryGetProperty("scenes", out var scenesElement) || scenesElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ScenePlanItemDto>();
            }

            var list = new List<ScenePlanItemDto>();
            foreach (var el in scenesElement.EnumerateArray())
            {
                int seq = list.Count + 1;
                string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                string imagePrompt = el.TryGetProperty("imagePrompt", out var ip) ? ip.GetString() ?? string.Empty : string.Empty;
                string videoPrompt = el.TryGetProperty("videoPrompt", out var vp) ? vp.GetString() ?? string.Empty : string.Empty;
                string tiktokContent = el.TryGetProperty("tiktokContent", out var tc) && tc.ValueKind == JsonValueKind.String ? tc.GetString() ?? string.Empty : string.Empty;
                list.Add(new ScenePlanItemDto(
                    SequenceOrder: seq,
                    Name: string.IsNullOrWhiteSpace(name) ? $"Cảnh {seq}" : name,
                    Purpose: imagePrompt,
                    TargetDurationSeconds: durationSeconds,
                    ImageStrategy: SceneImageStrategy.ReuseFoundation,
                    MotionPromptDraft: videoPrompt,
                    TikTokContent: tiktokContent
                ));
            }

            if (list.Count == Math.Max(1, expectedCount) && list.All(item => !string.IsNullOrWhiteSpace(item.Purpose)))
            {
                return list;
            }
        }
        catch (JsonException)
        {
            // Phản hồi không phải JSON: không tạo cảnh, run báo lỗi ở bước Script.
        }

        return Array.Empty<ScenePlanItemDto>();
    }
}
