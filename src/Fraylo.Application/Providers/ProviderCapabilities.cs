using System;
using System.Collections.Generic;
using System.Linq;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;

namespace Fraylo.Application.Providers;

/// <summary>
/// Định nghĩa năng lực chuẩn (Capabilities) của các AI Provider theo execution-contract-v1.1 §1, §3.4.
/// Quy tắc an toàn: Khi chưa qua T19 verification thực tế trên môi trường live,
/// VerificationStatus luôn là NOT_VERIFIED, AutomationSupported = false,
/// ManualSupported = false; quy trình chỉ chạy tự động.
/// Tuyệt đối không giả mạo VERIFIED khi chưa xác minh thật.
/// </summary>
public static class ProviderCapabilities
{
    private static readonly IReadOnlyList<ProviderTaskCapabilityDto> ChatGPTCapabilities = new[]
    {
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.ChatGPTWeb,
            TaskType: JobTaskType.Script,
            VerificationStatus: ProviderVerificationStatus.NotVerified,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: false,
            MaxInputLength: 8000,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: false,
            SupportsImageInputForPrompt: false,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        ),
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.ChatGPTWeb,
            TaskType: JobTaskType.Image,
            VerificationStatus: ProviderVerificationStatus.NotVerified,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: true,
            MaxInputLength: 4000,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: true,
            SupportsImageInputForPrompt: false,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        ),
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.ChatGPTWeb,
            TaskType: JobTaskType.VideoPrompt,
            VerificationStatus: ProviderVerificationStatus.NotVerified,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: true,
            MaxInputLength: 4000,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: false,
            SupportsImageInputForPrompt: true,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        ),
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.ChatGPTWeb,
            TaskType: JobTaskType.VideoRender,
            VerificationStatus: ProviderVerificationStatus.Unsupported,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: false,
            MaxInputLength: 0,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: false,
            SupportsImageInputForPrompt: false,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        )
    };

    private static readonly IReadOnlyList<ProviderTaskCapabilityDto> GeminiCapabilities = new[]
    {
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.GeminiWeb,
            TaskType: JobTaskType.Script,
            VerificationStatus: ProviderVerificationStatus.NotVerified,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: false,
            MaxInputLength: 8000,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: false,
            SupportsImageInputForPrompt: false,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        ),
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.GeminiWeb,
            TaskType: JobTaskType.Image,
            VerificationStatus: ProviderVerificationStatus.NotVerified,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: true,
            MaxInputLength: 4000,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: true,
            SupportsImageInputForPrompt: false,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        ),
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.GeminiWeb,
            TaskType: JobTaskType.VideoPrompt,
            VerificationStatus: ProviderVerificationStatus.NotVerified,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: true,
            MaxInputLength: 4000,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: false,
            SupportsImageInputForPrompt: true,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        ),
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.GeminiWeb,
            TaskType: JobTaskType.VideoRender,
            VerificationStatus: ProviderVerificationStatus.Unsupported,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: false,
            MaxInputLength: 0,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: false,
            SupportsImageInputForPrompt: false,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        )
    };

    private static readonly IReadOnlyList<ProviderTaskCapabilityDto> MuseAICapabilities = new[]
    {
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.MuseAI,
            TaskType: JobTaskType.VideoRender,
            VerificationStatus: ProviderVerificationStatus.NotVerified,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: true,
            MaxInputLength: 2000,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: false,
            SupportsImageInputForPrompt: false,
            SupportsNoAudioVideo: true,
            AudioBehaviorDescription: "Không gửi audio: hệ thống Fraylo không tải tệp audio lên MuseAI. Video kết quả từ MuseAI có thể có nhạc nền mặc định của nhà cung cấp hoặc im lặng tùy phiên bản hệ thống MuseAI.",
            SupportedDurations: new[] { 10, 15, 30 },
            SupportsMultiShot: false,
            SupportsReferenceVideoApproximation: true,
            ReferenceVideoEvidenceId: "reference-video-approximation-audio-fit-20261107",
            SupportsReferenceVideoAudioFit: true
        ),
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.MuseAI,
            TaskType: JobTaskType.Script,
            VerificationStatus: ProviderVerificationStatus.Unsupported,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: false,
            MaxInputLength: 0,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: false,
            SupportsImageInputForPrompt: false,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        ),
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.MuseAI,
            TaskType: JobTaskType.Image,
            VerificationStatus: ProviderVerificationStatus.Unsupported,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: false,
            MaxInputLength: 0,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: false,
            SupportsImageInputForPrompt: false,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        ),
        new ProviderTaskCapabilityDto(
            ProviderKey: ProviderKeys.MuseAI,
            TaskType: JobTaskType.VideoPrompt,
            VerificationStatus: ProviderVerificationStatus.Unsupported,
            AutomationSupported: false,
            ManualSupported: false,
            RequiresReferenceImage: false,
            MaxInputLength: 0,
            EvidenceId: null,
            VerifiedAtUtc: null,
            ConfigVersion: 1,
            SupportsDualReferenceImages: false,
            SupportsImageInputForPrompt: false,
            SupportsNoAudioVideo: false,
            AudioBehaviorDescription: null
        )
    };

    private static IReadOnlyList<ProviderTaskCapabilityDto> WithAnalytics(IReadOnlyList<ProviderTaskCapabilityDto> capabilities) =>
        [.. capabilities,
            capabilities.Single(c => c.TaskType == JobTaskType.Script) with { TaskType = JobTaskType.AnalyticsAnalysis },
            capabilities.Single(c => c.TaskType == JobTaskType.Script) with
            { TaskType = JobTaskType.FacebookContentRewrite, MaxOutputLength = 24000 }];

    private static readonly Dictionary<string, IReadOnlyList<ProviderTaskCapabilityDto>> DefaultMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProviderKeys.ChatGPTWeb] = WithAnalytics(ChatGPTCapabilities),
        [ProviderKeys.GeminiWeb] = WithAnalytics(GeminiCapabilities),
        [ProviderKeys.MuseAI] = MuseAICapabilities
    };

    /// <summary>
    /// Lấy danh sách capabilities mặc định cho một providerKey cụ thể.
    /// Trả về mảng rỗng nếu không nhận diện được providerKey.
    /// </summary>
    public static IReadOnlyList<ProviderTaskCapabilityDto> GetDefaultCapabilities(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return Array.Empty<ProviderTaskCapabilityDto>();
        }

        return DefaultMap.TryGetValue(providerKey.Trim(), out var capabilities)
            ? capabilities
            : Array.Empty<ProviderTaskCapabilityDto>();
    }

    /// <summary>
    /// Lấy toàn bộ capabilities mặc định của tất cả các AI Providers đã định nghĩa.
    /// </summary>
    public static IReadOnlyList<ProviderTaskCapabilityDto> GetAllDefaultCapabilities()
    {
        var all = new List<ProviderTaskCapabilityDto>();
        foreach (var list in DefaultMap.Values)
        {
            all.AddRange(list);
        }
        return all;
    }

    /// <summary>
    /// Lấy capability cụ thể cho một cặp (providerKey, taskType).
    /// </summary>
    public static ProviderTaskCapabilityDto? GetDefaultCapability(string providerKey, string taskType)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(taskType))
        {
            return null;
        }

        var capabilities = GetDefaultCapabilities(providerKey);
        return capabilities.FirstOrDefault(c => string.Equals(c.TaskType, taskType.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Kiểm tra loại tác vụ có trong năng lực provider; readiness automation được kiểm tra riêng.
    /// </summary>
    public static bool IsSupportedTask(string providerKey, string taskType)
    {
        var cap = GetDefaultCapability(providerKey, taskType);
        return cap != null && cap.VerificationStatus != ProviderVerificationStatus.Unsupported;
    }

    /// <summary>
    /// Lấy danh sách thời lượng clip (giây) được hỗ trợ bởi provider theo CLIP_DURATION_STORYBOARD_PLAN.
    /// </summary>
    public static IReadOnlyList<int> GetSupportedDurations(string providerKey, string taskType = JobTaskType.VideoRender)
    {
        var cap = GetDefaultCapability(providerKey, taskType);
        return cap?.SupportedDurations ?? Array.Empty<int>();
    }

    /// <summary>
    /// Kiểm tra xem một thời lượng cụ thể có được hỗ trợ bởi provider hay không.
    /// Giá trị 30s chỉ hợp lệ khi provider được xác minh tạo trực tiếp một clip 30s.
    /// </summary>
    public static bool IsDurationSupported(string providerKey, int durationSeconds, string taskType = JobTaskType.VideoRender)
    {
        var supported = GetSupportedDurations(providerKey, taskType);
        return supported.Contains(durationSeconds);
    }
}
