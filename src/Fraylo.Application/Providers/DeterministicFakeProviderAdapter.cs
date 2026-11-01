using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Application.Jobs;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;

namespace Fraylo.Application.Providers;

/// <summary>
/// Bộ điều hợp giả lập kịch bản xác định (Deterministic Fake Adapter) dành riêng cho kiểm thử điều phối
/// quy trình video từ ảnh (Mốc 3 & Mốc 4), không gọi API/website bên ngoài gây tiêu hao quota.
/// 
/// Hỗ trợ:
/// 1. Mô phỏng tạo ảnh từ 2 ảnh tham chiếu (Mẫu + Sản phẩm) -> trả về asset reference hợp lệ.
/// 2. Mô phỏng đọc ảnh viết prompt chuyển động -> trả về JSON prompt chuyển động theo schema.
/// 3. Mô phỏng tạo video clip không gửi audio -> trả về video asset reference hợp lệ.
/// 4. Đảm bảo gọi onPreSideEffectCommitAsync trước khi hoàn tất (T16 CAS & crash safety).
/// </summary>
public sealed class DeterministicFakeProviderAdapter : IProviderAdapter
{
    private readonly string _providerKey;
    private readonly IReadOnlyList<ProviderTaskCapabilityDto> _capabilities;

    public string ProviderKey => _providerKey;

    public DeterministicFakeProviderAdapter(
        string providerKey,
        IReadOnlyList<ProviderTaskCapabilityDto>? customCapabilities = null)
    {
        _providerKey = providerKey ?? throw new ArgumentNullException(nameof(providerKey));

        if (customCapabilities != null)
        {
            _capabilities = customCapabilities;
        }
        else
        {
            // Mặc định cung cấp các capabilities ở trạng thái VERIFIED giả lập dành riêng cho kiểm thử logic
            _capabilities = new[]
            {
                new ProviderTaskCapabilityDto(
                    ProviderKey: providerKey,
                    TaskType: JobTaskType.Script,
                    VerificationStatus: ProviderVerificationStatus.Verified,
                    AutomationSupported: true,
                    ManualSupported: false,
                    RequiresReferenceImage: false,
                    MaxInputLength: 8000,
                    EvidenceId: "fake-evidence-script",
                    VerifiedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                    ConfigVersion: 1,
                    SupportsDualReferenceImages: false,
                    SupportsImageInputForPrompt: false,
                    SupportsNoAudioVideo: false
                ),
                new ProviderTaskCapabilityDto(
                    ProviderKey: providerKey,
                    TaskType: JobTaskType.Image,
                    VerificationStatus: ProviderVerificationStatus.Verified,
                    AutomationSupported: true,
                    ManualSupported: false,
                    RequiresReferenceImage: true,
                    MaxInputLength: 4000,
                    EvidenceId: "fake-evidence-image",
                    VerifiedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                    ConfigVersion: 1,
                    SupportsDualReferenceImages: true,
                    SupportsImageInputForPrompt: false,
                    SupportsNoAudioVideo: false
                ),
                new ProviderTaskCapabilityDto(
                    ProviderKey: providerKey,
                    TaskType: JobTaskType.VideoPrompt,
                    VerificationStatus: ProviderVerificationStatus.Verified,
                    AutomationSupported: true,
                    ManualSupported: false,
                    RequiresReferenceImage: true,
                    MaxInputLength: 4000,
                    EvidenceId: "fake-evidence-prompt",
                    VerifiedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                    ConfigVersion: 1,
                    SupportsDualReferenceImages: false,
                    SupportsImageInputForPrompt: true,
                    SupportsNoAudioVideo: false
                ),
                new ProviderTaskCapabilityDto(
                    ProviderKey: providerKey,
                    TaskType: JobTaskType.VideoRender,
                    VerificationStatus: ProviderVerificationStatus.Verified,
                    AutomationSupported: true,
                    ManualSupported: false,
                    RequiresReferenceImage: true,
                    MaxInputLength: 2000,
                    EvidenceId: "fake-evidence-video",
                    VerifiedAtUtc: DateTimeOffset.UtcNow.ToString("O"),
                    ConfigVersion: 1,
                    SupportsDualReferenceImages: false,
                    SupportsImageInputForPrompt: false,
                    SupportsNoAudioVideo: true,
                    AudioBehaviorDescription: "Kiểm thử giả lập: video xuất xưởng im lặng.",
                    SupportedDurations: new[] { 10, 15 },
                    SupportsMultiShot: false
                )
            };
        }
    }

    public IReadOnlyList<ProviderTaskCapabilityDto> GetCapabilities() => _capabilities;

    public Task<ProviderTaskCapabilityDto?> GetCapabilityForTaskAsync(string taskType, CancellationToken ct = default)
    {
        var cap = _capabilities.FirstOrDefault(c => string.Equals(c.TaskType, taskType, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(cap);
    }

    public async Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        Func<Task> onPreSideEffectCommitAsync,
        Func<int, string, Task>? onProgressAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var capability = await GetCapabilityForTaskAsync(context.TaskType, ct).ConfigureAwait(false);
        if (capability is null || capability.VerificationStatus == ProviderVerificationStatus.Unsupported)
        {
            return new JobExecutionResult(
                Success: false,
                ResultReference: null,
                ResultText: null,
                ReasonCode: "CAPABILITY_UNSUPPORTED",
                ErrorMessage: $"Provider '{_providerKey}' không hỗ trợ tác vụ '{context.TaskType}'.",
                SideEffectSubmitted: false
            );
        }

        // Cam kết intent trước khi submit (bắt buộc theo T16)
        if (onPreSideEffectCommitAsync != null)
        {
            await onPreSideEffectCommitAsync().ConfigureAwait(false);
        }

        if (onProgressAsync != null)
        {
            await onProgressAsync(50, $"Đang xử lý {context.TaskType} qua fake adapter...").ConfigureAwait(false);
        }

        // Sinh kết quả xác định theo TaskType
        return context.TaskType switch
        {
            JobTaskType.Script => new JobExecutionResult(
                Success: true,
                ResultReference: null,
                ResultText: "{\"scenes\": [{\"purpose\": \"Toàn thân trang phục\", \"visualPrompt\": \"Người mẫu sải bước tự tin\", \"motionPrompt\": \"Camera lia nhẹ từ chân lên đầu\"}]}",
                SideEffectSubmitted: true
            ),
            JobTaskType.Image => new JobExecutionResult(
                Success: true,
                ResultReference: $"asset://generated-image-{context.JobId}.png",
                ResultText: null,
                SideEffectSubmitted: true
            ),
            JobTaskType.VideoPrompt => new JobExecutionResult(
                Success: true,
                ResultReference: null,
                ResultText: "Góc quay cận cảnh chuyển động xoay người 45 độ, ánh sáng tự nhiên studio.",
                SideEffectSubmitted: true
            ),
            JobTaskType.VideoRender => new JobExecutionResult(
                Success: true,
                ResultReference: $"asset://rendered-clip-{context.JobId}.mp4",
                ResultText: null,
                SideEffectSubmitted: true
            ),
            _ => new JobExecutionResult(
                Success: true,
                ResultReference: $"asset://output-{context.JobId}",
                ResultText: "Mock output",
                SideEffectSubmitted: true
            )
        };
    }
}
