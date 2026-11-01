using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Workflows;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Workflows;

public class WorkflowValidationException : Exception
{
    public string? Field { get; }
    public string ErrorCode => "INVALID_ARGUMENT";

    public WorkflowValidationException(string message, string? field = null) : base(message)
    {
        Field = field;
    }
}

public class WorkflowConcurrencyException : Exception
{
    public string ErrorCode => "CONCURRENCY_CONFLICT";

    public WorkflowConcurrencyException(string message) : base(message) { }
}

public class WorkflowNotFoundException : Exception
{
    public string ErrorCode => "NOT_FOUND";

    public WorkflowNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Quản lý định nghĩa quy trình sản xuất video (Workflow) tuyến tính có phiên bản bất biến
/// theo execution-contract-v1.1 §6:
/// - Quy trình mặc định v1: Script (RequiresReview=true) -> Image -> VideoPrompt -> VideoRender.
/// - Lưu trữ các phiên bản bất biến trong AppSettings của SQLite.
/// - Hỗ trợ lưu phiên bản mới, kích hoạt và rollback an toàn với Optimistic Concurrency Control (Revision).
/// </summary>
public sealed class WorkflowDefinitionService
{
    public const string DefaultWorkflowId = "default-linear-workflow";
    public const string KeyActiveVersion = "workflow.active_version";
    public const string KeyGlobalRevision = "workflow.global_revision";
    public const string KeyMaxVersion = "workflow.max_version";
    public const string KeyDefinitionPrefix = "workflow.definition.v";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly DbContextOptions<FrayloDbContext> _dbOptions;

    public WorkflowDefinitionService(DbContextOptions<FrayloDbContext> dbOptions)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
    }

    /// <summary>
    /// Lấy định nghĩa workflow đang kích hoạt (Active Workflow).
    /// Nếu chưa từng khởi tạo, tự động tạo và lưu phiên bản mặc định Version 1.
    /// </summary>
    public async Task<WorkflowGetActiveResponseData> GetActiveWorkflowAsync(CancellationToken ct = default)
    {
        await using var dbContext = new FrayloDbContext(_dbOptions);

        var activeSetting = await dbContext.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == KeyActiveVersion, ct)
            .ConfigureAwait(false);

        if (activeSetting == null || !int.TryParse(activeSetting.Value, out var activeVersion) || activeVersion < 1)
        {
            // Tự động khởi tạo phiên bản mặc định v1
            return await InitializeDefaultVersionInternalAsync(dbContext, ct).ConfigureAwait(false);
        }

        var defKey = $"{KeyDefinitionPrefix}{activeVersion}";
        var defSetting = await dbContext.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == defKey, ct)
            .ConfigureAwait(false);

        if (defSetting == null || string.IsNullOrWhiteSpace(defSetting.Value))
        {
            // Fallback khởi tạo lại nếu dữ liệu bị thiếu
            return await InitializeDefaultVersionInternalAsync(dbContext, ct).ConfigureAwait(false);
        }

        var definition = JsonSerializer.Deserialize<WorkflowDefinitionDto>(defSetting.Value, JsonOptions)
            ?? throw new InvalidOperationException($"Không thể giải mã cấu hình workflow v{activeVersion}.");

        var globalRev = await GetGlobalRevisionInternalAsync(dbContext, ct).ConfigureAwait(false);

        return new WorkflowGetActiveResponseData(definition, globalRev);
    }

    /// <summary>
    /// Lấy một phiên bản workflow cụ thể theo số version.
    /// </summary>
    public async Task<WorkflowDefinitionDto> GetVersionAsync(int version, CancellationToken ct = default)
    {
        if (version < 1)
        {
            throw new WorkflowValidationException("Số phiên bản workflow phải lớn hơn hoặc bằng 1.", nameof(version));
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var defKey = $"{KeyDefinitionPrefix}{version}";
        var defSetting = await dbContext.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == defKey, ct)
            .ConfigureAwait(false);

        if (defSetting == null || string.IsNullOrWhiteSpace(defSetting.Value))
        {
            throw new WorkflowNotFoundException($"Không tìm thấy phiên bản workflow v{version}.");
        }

        return JsonSerializer.Deserialize<WorkflowDefinitionDto>(defSetting.Value, JsonOptions)
            ?? throw new InvalidOperationException($"Không thể giải mã cấu hình workflow v{version}.");
    }

    /// <summary>
    /// Lưu một phiên bản workflow mới bất biến (version = max_version + 1).
    /// Kiểm tra tính hợp lệ nghiêm ngặt: thứ tự các bước 1..4, Script luôn RequiresReview = true.
    /// Phiên bản mới lưu ở trạng thái sẵn sàng nhưng CHƯA tự động kích hoạt.
    /// </summary>
    public async Task<WorkflowSaveVersionResponseData> SaveVersionAsync(
        WorkflowSaveVersionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new WorkflowValidationException("Tên quy trình workflow không được để trống.", nameof(request.Name));
        }

        if (string.IsNullOrWhiteSpace(request.ClientRequestId))
        {
            throw new WorkflowValidationException("clientRequestId không được để trống.", nameof(request.ClientRequestId));
        }

        // Kiểm tra tính hợp lệ của danh sách các bước
        ValidateWorkflowSteps(request.Steps);

        await using var dbContext = new FrayloDbContext(_dbOptions);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // Kiểm tra optimistic concurrency revision
            var currentRev = await GetGlobalRevisionInternalAsync(dbContext, ct).ConfigureAwait(false);
            if (request.ExpectedRevision > 0 && request.ExpectedRevision != currentRev)
            {
                throw new WorkflowConcurrencyException(
                    $"Xung đột phiên bản: Kỳ vọng revision {request.ExpectedRevision} nhưng hệ thống hiện tại là {currentRev}.");
            }

            // Đảm bảo v1 đã tồn tại trong DB trước khi tăng lên version mới
            var activeSetting = await dbContext.AppSettings
                .FirstOrDefaultAsync(s => s.Key == KeyActiveVersion, ct)
                .ConfigureAwait(false);
            if (activeSetting == null)
            {
                var defaultDef = CreateDefaultDefinition(1);
                var defaultJson = JsonSerializer.Serialize(defaultDef, JsonOptions);
                var nowIso = DateTimeOffset.UtcNow.ToString("O");
                await UpsertSettingAsync(dbContext, $"{KeyDefinitionPrefix}1", defaultJson, nowIso, ct).ConfigureAwait(false);
                await UpsertSettingAsync(dbContext, KeyActiveVersion, "1", nowIso, ct).ConfigureAwait(false);
                await UpsertSettingAsync(dbContext, KeyMaxVersion, "1", nowIso, ct).ConfigureAwait(false);
                await UpsertSettingAsync(dbContext, KeyGlobalRevision, "1", nowIso, ct).ConfigureAwait(false);
            }

            // Lấy max version hiện tại
            var maxVerSetting = await dbContext.AppSettings
                .FirstOrDefaultAsync(s => s.Key == KeyMaxVersion, ct)
                .ConfigureAwait(false);

            int currentMaxVersion = 1;
            if (maxVerSetting != null && int.TryParse(maxVerSetting.Value, out var parsedMax))
            {
                currentMaxVersion = parsedMax;
            }

            int newVersion = currentMaxVersion + 1;
            long newRevision = currentRev + 1;
            var now = DateTimeOffset.UtcNow.ToString("O");

            var newDefinition = new WorkflowDefinitionDto(
                WorkflowId: request.WorkflowId ?? DefaultWorkflowId,
                Version: newVersion,
                Name: request.Name.Trim(),
                Description: request.Description?.Trim() ?? string.Empty,
                Steps: request.Steps.ToList(),
                Revision: newRevision,
                CreatedAtUtc: now
            );

            var serialized = JsonSerializer.Serialize(newDefinition, JsonOptions);

            // Lưu version mới vào AppSettings
            await UpsertSettingAsync(dbContext, $"{KeyDefinitionPrefix}{newVersion}", serialized, now, ct).ConfigureAwait(false);
            await UpsertSettingAsync(dbContext, KeyMaxVersion, newVersion.ToString(), now, ct).ConfigureAwait(false);
            await UpsertSettingAsync(dbContext, KeyGlobalRevision, newRevision.ToString(), now, ct).ConfigureAwait(false);

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return new WorkflowSaveVersionResponseData(newDefinition, newRevision);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Kích hoạt một phiên bản workflow đã lưu trước đó (hỗ trợ cả Rollback).
    /// </summary>
    public async Task<WorkflowActivateResponseData> ActivateVersionAsync(
        WorkflowActivateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Version < 1)
        {
            throw new WorkflowValidationException("Số phiên bản workflow cần kích hoạt phải lớn hơn hoặc bằng 1.", nameof(request.Version));
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            var currentRev = await GetGlobalRevisionInternalAsync(dbContext, ct).ConfigureAwait(false);
            if (request.ExpectedRevision > 0 && request.ExpectedRevision != currentRev)
            {
                throw new WorkflowConcurrencyException(
                    $"Xung đột phiên bản: Kỳ vọng revision {request.ExpectedRevision} nhưng hệ thống hiện tại là {currentRev}.");
            }

            // Kiểm tra xem version có tồn tại trong DB không
            var defKey = $"{KeyDefinitionPrefix}{request.Version}";
            var defSetting = await dbContext.AppSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == defKey, ct)
                .ConfigureAwait(false);

            if (defSetting == null || string.IsNullOrWhiteSpace(defSetting.Value))
            {
                throw new WorkflowNotFoundException($"Không tìm thấy phiên bản workflow v{request.Version} để kích hoạt.");
            }

            long newRevision = currentRev + 1;
            var now = DateTimeOffset.UtcNow.ToString("O");

            // Kích hoạt version này và tăng revision
            await UpsertSettingAsync(dbContext, KeyActiveVersion, request.Version.ToString(), now, ct).ConfigureAwait(false);
            await UpsertSettingAsync(dbContext, KeyGlobalRevision, newRevision.ToString(), now, ct).ConfigureAwait(false);

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return new WorkflowActivateResponseData(
                WorkflowId: request.WorkflowId ?? DefaultWorkflowId,
                Version: request.Version,
                Revision: newRevision
            );
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Kiểm tra tính hợp lệ nghiêm ngặt của các bước workflow:
    /// - Phải có đúng 4 bước theo trình tự chuẩn (Script -> Image -> VideoPrompt -> VideoRender).
    /// - StepOrder phải là 1, 2, 3, 4 liên tiếp không trùng lặp và không ngắt quãng.
    /// - Bước 1 (Script) bắt buộc RequiresReview = true.
    /// - StepKey không được rỗng và phải duy nhất trong một definition.
    /// </summary>
    public static void ValidateWorkflowSteps(IReadOnlyList<WorkflowStepDefinitionDto>? steps)
    {
        if (steps == null || steps.Count == 0)
        {
            throw new WorkflowValidationException("Quy trình workflow phải chứa các bước định nghĩa.", nameof(steps));
        }

        if (steps.Count != 4)
        {
            throw new WorkflowValidationException(
                $"Quy trình workflow tuyến tính bắt buộc có đúng 4 bước (hiện có {steps.Count} bước).",
                nameof(steps));
        }

        var expectedSequence = new[]
        {
            (Order: 1, TaskType: JobTaskType.Script),
            (Order: 2, TaskType: JobTaskType.Image),
            (Order: 3, TaskType: JobTaskType.VideoPrompt),
            (Order: 4, TaskType: JobTaskType.VideoRender)
        };

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < 4; i++)
        {
            var step = steps[i];
            var expected = expectedSequence[i];

            if (string.IsNullOrWhiteSpace(step.StepKey))
            {
                throw new WorkflowValidationException($"StepKey ở vị trí thứ {i + 1} không được để trống.", nameof(step.StepKey));
            }

            if (!seenKeys.Add(step.StepKey.Trim()))
            {
                throw new WorkflowValidationException($"StepKey '{step.StepKey}' bị trùng lặp trong quy trình.", nameof(step.StepKey));
            }

            if (step.StepOrder != expected.Order)
            {
                throw new WorkflowValidationException(
                    $"Thứ tự bước (StepOrder) tại bước {i + 1} phải là {expected.Order}, nhận được {step.StepOrder}.",
                    nameof(step.StepOrder));
            }

            if (!string.Equals(step.TaskType, expected.TaskType, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkflowValidationException(
                    $"Bước thứ {expected.Order} phải là tác vụ '{expected.TaskType}', nhận được '{step.TaskType}'.",
                    nameof(step.TaskType));
            }

            // Quy tắc cốt lõi: Script luôn luôn bắt buộc RequiresReview = true
            if (expected.Order == 1 && !step.RequiresReview)
            {
                throw new WorkflowValidationException(
                    "Bước 1 (Script) bắt buộc phải có RequiresReview = true để người dùng phê duyệt kịch bản.",
                    nameof(step.RequiresReview));
            }
        }
    }

    private static WorkflowDefinitionDto CreateDefaultDefinition(long revision = 1)
    {
        return new WorkflowDefinitionDto(
            WorkflowId: DefaultWorkflowId,
            Version: 1,
            Name: "Standard Video Production Pipeline",
            Description: "Quy trình sản xuất video 4 bước tuyến tính: Kịch bản -> Tạo ảnh -> Prompt video -> Kết xuất video.",
            Steps: new[]
            {
                new WorkflowStepDefinitionDto("step-script", JobTaskType.Script, 1, RequiresReview: true),
                new WorkflowStepDefinitionDto("step-image", JobTaskType.Image, 2, RequiresReview: false),
                new WorkflowStepDefinitionDto("step-videoprompt", JobTaskType.VideoPrompt, 3, RequiresReview: false),
                new WorkflowStepDefinitionDto("step-videorender", JobTaskType.VideoRender, 4, RequiresReview: false)
            },
            Revision: revision,
            CreatedAtUtc: DateTimeOffset.UtcNow.ToString("O")
        );
    }

    private async Task<WorkflowGetActiveResponseData> InitializeDefaultVersionInternalAsync(
        FrayloDbContext dbContext,
        CancellationToken ct)
    {
        var defaultDef = CreateDefaultDefinition(1);
        var json = JsonSerializer.Serialize(defaultDef, JsonOptions);
        var now = DateTimeOffset.UtcNow.ToString("O");

        await UpsertSettingAsync(dbContext, $"{KeyDefinitionPrefix}1", json, now, ct).ConfigureAwait(false);
        await UpsertSettingAsync(dbContext, KeyActiveVersion, "1", now, ct).ConfigureAwait(false);
        await UpsertSettingAsync(dbContext, KeyMaxVersion, "1", now, ct).ConfigureAwait(false);
        await UpsertSettingAsync(dbContext, KeyGlobalRevision, "1", now, ct).ConfigureAwait(false);

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        return new WorkflowGetActiveResponseData(defaultDef, 1);
    }

    private static async Task<long> GetGlobalRevisionInternalAsync(FrayloDbContext dbContext, CancellationToken ct)
    {
        var revItem = await dbContext.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == KeyGlobalRevision, ct)
            .ConfigureAwait(false);

        if (revItem != null && long.TryParse(revItem.Value, out var rev) && rev > 0)
        {
            return rev;
        }

        return 1;
    }

    private static async Task UpsertSettingAsync(
        FrayloDbContext dbContext,
        string key,
        string value,
        string nowIso,
        CancellationToken ct)
    {
        var setting = await dbContext.AppSettings
            .FirstOrDefaultAsync(s => s.Key == key, ct)
            .ConfigureAwait(false);

        if (setting == null)
        {
            dbContext.AppSettings.Add(new AppSetting
            {
                Key = key,
                Value = value,
                UpdatedAtUtc = nowIso
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAtUtc = nowIso;
        }
    }
}
