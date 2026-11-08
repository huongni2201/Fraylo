using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Prompts;
using Fraylo.Application.Assets;
using Fraylo.Application.Prompts.Models;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Prompts;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Application.Jobs;

/// <summary>
/// Chịu trách nhiệm chụp và đóng băng bất biến (Immutable Input Snapshots) các tham số đầu vào
/// tại thời điểm Job được Enqueue vào hệ thống.
/// Bảo đảm các thay đổi sau này trong Popup Cài đặt hoặc Prompt Editor không làm biến đổi dữ liệu của job cũ.
/// </summary>
public sealed class JobSnapshotFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly SettingsService _settingsService;
    private readonly PromptTemplateService? _promptTemplateService;
    private readonly DbContextOptions<FrayloDbContext>? _dbOptions;

    public JobSnapshotFactory(
        SettingsService settingsService,
        PromptTemplateService? promptTemplateService = null,
        DbContextOptions<FrayloDbContext>? dbOptions = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _promptTemplateService = promptTemplateService;
        _dbOptions = dbOptions;
    }

    /// <summary>
    /// Chụp và đóng băng toàn bộ tham số đầu vào: AI Provider, Storage Root, Prompt Snapshot và Config Snapshot.
    /// </summary>
    public async Task<JobSnapshotResult> CreateSnapshotAsync(
        JobEnqueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!JobTaskType.IsValid(request.TaskType))
        {
            throw new JobValidationException($"Loại tác vụ '{request.TaskType}' không hợp lệ. Các loại cho phép: {string.Join(", ", JobTaskType.All)}.", nameof(request.TaskType));
        }

        // 1. Phân giải Cài đặt hiện tại từ SettingsService
        var currentSettings = await _settingsService.GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        // 2. Phân giải AI Provider Key bất biến (Strict Provider Validation - không fallback ngầm)
        string? providerKey = request.TaskType switch
        {
            JobTaskType.Script => currentSettings.ScriptProvider,
            JobTaskType.Image => currentSettings.ImageProvider,
            JobTaskType.VideoPrompt when request.MediaSnapshot?.ReferenceAnalysisVersion == 1 => currentSettings.ScriptProvider,
            JobTaskType.VideoPrompt => currentSettings.VideoPromptProvider,
            JobTaskType.VideoRender => "museai", // VideoRender mặc định dùng MuseAI theo kiến trúc
            _ => null
        };

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            throw new ProviderNotConfiguredException(request.MediaSnapshot?.ReferenceAnalysisVersion == 1 ? JobTaskType.Script : request.TaskType);
        }

        // 3. Phân giải Storage Root ID bất biến (Đóng băng nơi lưu trữ ảnh/tài liệu/video)
        string? storageRootId = request.TaskType switch
        {
            JobTaskType.Script => currentSettings.DocumentsRootId,
            JobTaskType.Image => currentSettings.ImagesRootId,
            JobTaskType.VideoPrompt => currentSettings.DocumentsRootId,
            JobTaskType.VideoRender => currentSettings.VideosRootId,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(storageRootId))
        {
            throw new StorageRootUnavailableException(request.TaskType);
        }

        // 4. Phân giải Prompt Snapshot bất biến
        string promptSnapshotText;
        if (!string.IsNullOrWhiteSpace(request.CustomPromptText))
        {
            // Custom prompt trực tiếp
            var customSnapshot = new PromptRenderSnapshotDto(
                TemplateId: "custom",
                RenderedText: request.CustomPromptText,
                Variables: request.PromptVariables ?? new Dictionary<string, string>(),
                CapturedAtUtc: DateTimeOffset.UtcNow.ToString("o")
            );
            promptSnapshotText = JsonSerializer.Serialize(customSnapshot, JsonOptions);
        }
        else if (!string.IsNullOrWhiteSpace(request.PromptTemplateId))
        {
            PromptRenderSnapshotDto snapshotDto;
            if (_promptTemplateService is not null)
            {
                snapshotDto = await _promptTemplateService.CreateSnapshotAsync(
                    templateId: request.PromptTemplateId,
                    purpose: request.TaskType,
                    variables: request.PromptVariables,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else if (_dbOptions is not null)
            {
                await using var dbContext = new FrayloDbContext(_dbOptions);
                var templateService = new PromptTemplateService(dbContext);
                snapshotDto = await templateService.CreateSnapshotAsync(
                    templateId: request.PromptTemplateId,
                    purpose: request.TaskType,
                    variables: request.PromptVariables,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException("PromptTemplateService chưa được nạp trong bộ điều phối JobSnapshotFactory.");
            }

            promptSnapshotText = JsonSerializer.Serialize(snapshotDto, JsonOptions);
        }
        else
        {
            throw new JobValidationException("Tác vụ yêu cầu phải có 'promptTemplateId' hoặc 'customPromptText'.", nameof(request.CustomPromptText));
        }

        // 5. Đóng gói Config Snapshot JSON (bất biến bao gồm ConfigVersion, WorkflowVersion và MediaSnapshot PV-R6)
        JobMediaSnapshotDto? resolvedMediaSnapshot = request.MediaSnapshot;
        if (request.MediaSnapshot != null && _dbOptions != null)
        {
            await using var dbContext = new FrayloDbContext(_dbOptions);
            var assetIds = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.MediaSnapshot.ModelAssetId)) assetIds.Add(request.MediaSnapshot.ModelAssetId);
            if (!string.IsNullOrWhiteSpace(request.MediaSnapshot.ProductAssetId)) assetIds.Add(request.MediaSnapshot.ProductAssetId);
            if (!string.IsNullOrWhiteSpace(request.MediaSnapshot.ReadyImageAssetId)) assetIds.Add(request.MediaSnapshot.ReadyImageAssetId);
            if (!string.IsNullOrWhiteSpace(request.MediaSnapshot.SourceImageAssetId)) assetIds.Add(request.MediaSnapshot.SourceImageAssetId);
            if (!string.IsNullOrWhiteSpace(request.MediaSnapshot.AudioAssetId)) assetIds.Add(request.MediaSnapshot.AudioAssetId);
            if (!string.IsNullOrWhiteSpace(request.MediaSnapshot.ReferenceVideoAssetId)) assetIds.Add(request.MediaSnapshot.ReferenceVideoAssetId);
            if (request.MediaSnapshot.ImageAssetIds is { } imageIds) assetIds.AddRange(imageIds);

            if (assetIds.Count > 0)
            {
                var assets = await dbContext.Assets
                    .AsNoTracking()
                    .Where(a => assetIds.Contains(a.Id))
                    .ToDictionaryAsync(a => a.Id, cancellationToken).ConfigureAwait(false);

                foreach (var aId in assetIds)
                {
                    if (!assets.TryGetValue(aId, out var assetEntity))
                    {
                        throw new JobValidationException($"Không tìm thấy tệp tài nguyên '{aId}' được yêu cầu trong MediaSnapshot.", nameof(request.MediaSnapshot));
                    }
                    if (assetEntity.ProjectId is not null && assetEntity.ProjectId != request.ProjectId)
                    {
                        throw new UnauthorizedAccessException($"Tài nguyên '{aId}' thuộc Project '{assetEntity.ProjectId}', không dùng được trong Project '{request.ProjectId}'.");
                    }
                }

                string? modelHash = request.MediaSnapshot.ModelAssetHash ?? (request.MediaSnapshot.ModelAssetId != null && assets.TryGetValue(request.MediaSnapshot.ModelAssetId, out var ma) ? ma.Sha256Hash : null);
                string? prodHash = request.MediaSnapshot.ProductAssetHash ?? (request.MediaSnapshot.ProductAssetId != null && assets.TryGetValue(request.MediaSnapshot.ProductAssetId, out var pa) ? pa.Sha256Hash : null);
                string? readyHash = request.MediaSnapshot.ReadyImageAssetHash ?? (request.MediaSnapshot.ReadyImageAssetId != null && assets.TryGetValue(request.MediaSnapshot.ReadyImageAssetId, out var ra) ? ra.Sha256Hash : null);
                string? sourceHash = request.MediaSnapshot.SourceImageAssetHash ?? (request.MediaSnapshot.SourceImageAssetId != null && assets.TryGetValue(request.MediaSnapshot.SourceImageAssetId, out var sa) ? sa.Sha256Hash : null);
                string? audioHash = request.MediaSnapshot.AudioAssetHash ?? (request.MediaSnapshot.AudioAssetId != null && assets.TryGetValue(request.MediaSnapshot.AudioAssetId, out var aa) ? aa.Sha256Hash : null);

                resolvedMediaSnapshot = request.MediaSnapshot with
                {
                    ModelAssetHash = modelHash,
                    ProductAssetHash = prodHash,
                    ReadyImageAssetHash = readyHash,
                    SourceImageAssetHash = sourceHash,
                    AudioAssetHash = audioHash
                };
            }
        }

        if (resolvedMediaSnapshot?.ReferenceVideoAssetId is not null || resolvedMediaSnapshot?.ReferenceVideoMode is not null)
        {
            var isAnalysis = request.TaskType == JobTaskType.VideoPrompt &&
                resolvedMediaSnapshot?.ReferenceAnalysisVersion == 1 && request.WorkflowRunId is not null;
            if (_dbOptions is null || (!isAnalysis && request.TaskType != JobTaskType.VideoRender))
                throw new JobValidationException("Video mẫu cần tác vụ phân tích hoặc render có kiểm chứng media.");
            var validated = await ReferenceVideoMedia.ValidateAsync(_dbOptions, request.ProjectId, resolvedMediaSnapshot!,
                requireSnapshotHashes: resolvedMediaSnapshot!.ReferenceAnalysisJobId is not null, cancellationToken).ConfigureAwait(false);
            ReferenceVideoMedia.EnsureProviderSupported(validated.Snapshot);
            resolvedMediaSnapshot = validated.Snapshot;
        }

        var configSnapshot = new
        {
            ConfigVersion = resolvedMediaSnapshot?.ReferenceVideoAssetId is null ? 1 : 3,
            WorkflowVersion = 1,
            TimeoutSeconds = 60,
            MaxAttempts = 3,
            LeaseDurationSeconds = 45,
            HeartbeatIntervalSeconds = 15,
            CapturedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            WorkflowRunId = request.WorkflowRunId,
            WorkflowStepKey = request.WorkflowStepKey,
            MediaSnapshot = resolvedMediaSnapshot
        };
        string configSnapshotJson = JsonSerializer.Serialize(configSnapshot, JsonOptions);

        return new JobSnapshotResult(
            ProviderKey: providerKey,
            StorageRootId: storageRootId,
            PromptSnapshotText: promptSnapshotText,
            ConfigSnapshotJson: configSnapshotJson
        );
    }
}
