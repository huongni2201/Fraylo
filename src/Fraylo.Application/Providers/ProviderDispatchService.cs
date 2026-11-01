using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Fraylo.Application.Jobs;
using Fraylo.Application.Settings;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;
using Fraylo.Infrastructure.Persistence;

namespace Fraylo.Application.Providers;

/// <summary>
/// Ngoại lệ ném ra khi việc xác thực provider thất bại hoặc không tương thích capability.
/// </summary>
public class ProviderValidationException : Exception
{
    public string ErrorCode { get; }

    public ProviderValidationException(string message, string errorCode = "PROVIDER_CAPABILITY_UNAVAILABLE")
        : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Dịch vụ phân giải và kiểm tra tính hợp lệ của AI Provider trước khi cấp slot / dispatch
/// theo execution-contract-v1.1 §3.1, §3.4 và SETTINGS_PLAN.md §5.4.
/// 
/// Nguyên tắc bảo mật & toàn vẹn:
/// 1. Chặn providerKey rỗng hoặc chưa cấu hình -> ném ProviderNotConfiguredException hoặc ProviderValidationException.
/// 2. Chặn sai provider và tuyệt đối không fallback ngầm giữa Gemini và ChatGPT (Strict Routing).
/// 3. Nếu capability là NOT_VERIFIED hoặc không có adapter -> ném lỗi rõ ràng, không giả mạo automation thành công.
/// </summary>
public sealed class ProviderDispatchService
{
    private readonly ProviderRegistry _registry;
    private readonly SettingsService _settingsService;
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;

    public ProviderDispatchService(
        ProviderRegistry registry,
        SettingsService settingsService,
        DbContextOptions<FrayloDbContext> dbOptions)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
    }

    /// <summary>
    /// Phân giải AI Provider Key được cấu hình cho loại tác vụ từ SettingsService.
    /// Chặn đứng tác vụ nếu provider chưa được chọn hoặc không hợp lệ (không fallback ngầm).
    /// </summary>
    public async Task<string> ResolveProviderKeyForTaskAsync(string taskType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskType) || !JobTaskType.IsValid(taskType))
        {
            throw new ProviderValidationException($"Loại tác vụ '{taskType}' không hợp lệ.", "INVALID_ARGUMENT");
        }

        var currentSettings = await _settingsService.GetSettingsAsync(ct).ConfigureAwait(false);

        string? providerKey = taskType switch
        {
            JobTaskType.Script => currentSettings.ScriptProvider,
            JobTaskType.Image => currentSettings.ImageProvider,
            JobTaskType.VideoPrompt => currentSettings.VideoPromptProvider,
            JobTaskType.VideoRender => ProviderKeys.MuseAI, // Cố định MuseAI theo execution contract
            _ => null
        };

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            throw new ProviderNotConfiguredException(taskType);
        }

        var key = providerKey.Trim();

        // Kiểm tra tính hợp lệ cơ bản của provider cho loại task
        ValidateTaskAndProviderCompatibility(key, taskType);

        // Kiểm tra xem provider có trong registry hoặc là provider chuẩn của hệ thống
        if (!_registry.HasAdapter(key) && !ProviderKeys.IsValid(key))
        {
            throw new ProviderNotConfiguredException(taskType);
        }

        return key;
    }

    /// <summary>
    /// Kiểm tra tính hợp lệ giữa provider được yêu cầu với provider đã được cấu hình trong Settings.
    /// Chặn đứng hoàn toàn việc hoán đổi ngầm hoặc fallback ngầm giữa Gemini và ChatGPT.
    /// </summary>
    public async Task ValidateProviderMatchesConfiguredAsync(
        string taskType,
        string requestedProviderKey,
        CancellationToken ct = default)
    {
        var configuredProvider = await ResolveProviderKeyForTaskAsync(taskType, ct).ConfigureAwait(false);

        if (!string.Equals(configuredProvider, requestedProviderKey?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderValidationException(
                $"Xung đột cấu hình nhà cung cấp: Tác vụ '{taskType}' được cấu hình sử dụng '{configuredProvider}', " +
                $"nhưng yêu cầu dispatch tới '{requestedProviderKey}'. Hệ thống chặn đứng hành vi fallback ngầm.",
                "PROVIDER_MISMATCH");
        }
    }

    /// <summary>
    /// Xác thực capability của Provider trước khi dispatch.
    /// </summary>
    public ProviderTaskCapabilityDto ValidateDispatch(string providerKey, string taskType)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            throw new ProviderValidationException("Mã nhà cung cấp (providerKey) không được để trống.", "INVALID_ARGUMENT");
        }

        if (string.IsNullOrWhiteSpace(taskType) || !JobTaskType.IsValid(taskType))
        {
            throw new ProviderValidationException($"Loại tác vụ '{taskType}' không hợp lệ.", "INVALID_ARGUMENT");
        }

        var key = providerKey.Trim();
        var tType = taskType.Trim();

        // Kiểm tra tính tương thích giữa task và provider
        ValidateTaskAndProviderCompatibility(key, tType);

        var capability = _registry.GetCapability(key, tType);
        if (capability == null || string.Equals(capability.VerificationStatus, ProviderVerificationStatus.Unsupported, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderValidationException(
                $"Tác vụ '{tType}' không được hỗ trợ bởi nhà cung cấp '{key}'.",
                "PROVIDER_CAPABILITY_UNAVAILABLE");
        }

        return capability;
    }

    /// <summary>
    /// Xác thực điều kiện sẵn sàng cho tự động hóa (Automation Dispatch).
    /// Chặn đứng dispatch nếu provider đang ở trạng thái NOT_VERIFIED, chưa hỗ trợ automation hoặc chưa đăng ký adapter.
    /// Tuyệt đối không giả mạo automation thành công.
    /// </summary>
    public ProviderTaskCapabilityDto ValidateAutomationDispatch(string providerKey, string taskType)
    {
        var capability = ValidateDispatch(providerKey, taskType);

        if (string.Equals(capability.VerificationStatus, ProviderVerificationStatus.NotVerified, StringComparison.OrdinalIgnoreCase)
            || !capability.AutomationSupported)
        {
            throw new ProviderValidationException(
                $"Nhà cung cấp '{providerKey}' cho tác vụ '{taskType}' đang ở trạng thái NOT_VERIFIED hoặc chưa hỗ trợ automation. " +
                "Tuyệt đối không giả mạo tự động hóa thành công khi chưa xác minh thật qua T19.",
                "PROVIDER_NOT_VERIFIED");
        }

        if (!_registry.HasAdapter(providerKey))
        {
            throw new ProviderValidationException(
                $"Chưa tìm thấy adapter thực thi cho nhà cung cấp '{providerKey}'.",
                "ADAPTER_NOT_FOUND");
        }

        return capability;
    }

    /// <summary>
    /// Lấy adapter thực thi đã được xác thực cho loại tác vụ dựa trên cấu hình Settings hiện hành.
    /// </summary>
    public async Task<IProviderAdapter> ResolveAdapterForTaskAsync(string taskType, CancellationToken ct = default)
    {
        var providerKey = await ResolveProviderKeyForTaskAsync(taskType, ct).ConfigureAwait(false);
        ValidateAutomationDispatch(providerKey, taskType);

        return _registry.GetAdapter(providerKey);
    }

    /// <summary>
    /// Kiểm tra tính tương thích giữa loại task và provider dựa trên năng lực (capabilities) đăng ký trong ProviderRegistry.
    /// Tra cứu capability động thay vì hardcode switch-case / if-else:
    /// Nếu capability == null hoặc VerificationStatus == Unsupported -> ném ProviderValidationException
    /// với ErrorCode = "PROVIDER_CAPABILITY_UNAVAILABLE".
    /// </summary>
    private void ValidateTaskAndProviderCompatibility(string providerKey, string taskType)
    {
        var capability = _registry.GetCapability(providerKey, taskType);
        if (capability == null || string.Equals(capability.VerificationStatus, ProviderVerificationStatus.Unsupported, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderValidationException(
                $"Tác vụ '{taskType}' không được hỗ trợ bởi nhà cung cấp '{providerKey}'.",
                "PROVIDER_CAPABILITY_UNAVAILABLE");
        }
    }
}
