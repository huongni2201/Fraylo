using Fraylo.Application.Jobs;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;

namespace Fraylo.Application.Providers;

/// <summary>MuseAI video render; the Playwright automation runs only when the automation is wired.</summary>
public sealed class MuseAiVideoAdapter : IProviderAdapter
{
    private readonly MuseAiPlaywrightAutomation? _automation;

    public MuseAiVideoAdapter(MuseAiPlaywrightAutomation? automation = null) => _automation = automation;

    public string ProviderKey => ProviderKeys.MuseAI;

    public IReadOnlyList<ProviderTaskCapabilityDto> GetCapabilities() =>
        ProviderCapabilities.GetDefaultCapabilities(ProviderKey)
            .Select(capability => _automation is not null &&
                capability.TaskType == JobTaskType.VideoRender
                ? capability with
                {
                    VerificationStatus = ProviderVerificationStatus.Verified,
                    AutomationSupported = true,
                    EvidenceId = "playwright-automation",
                    SupportsReferenceVideoApproximation = true,
                    ReferenceVideoEvidenceId = "reference-video-approximation-audio-fit-20261107"
                }
                : capability).ToArray();

    public Task<ProviderTaskCapabilityDto?> GetCapabilityForTaskAsync(string taskType, CancellationToken ct = default) =>
        Task.FromResult(GetCapabilities().FirstOrDefault(c =>
            string.Equals(c.TaskType, taskType, StringComparison.OrdinalIgnoreCase)));

    public Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        Func<Task> onPreSideEffectCommitAsync,
        Func<int, string, Task>? onProgressAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var capability = ProviderCapabilities.GetDefaultCapability(ProviderKey, context.TaskType);
        if (capability is null || capability.VerificationStatus == ProviderVerificationStatus.Unsupported)
        {
            return Task.FromResult(new JobExecutionResult(
                Success: false,
                ReasonCode: "CAPABILITY_UNSUPPORTED",
                ErrorMessage: $"MuseAI không hỗ trợ tác vụ '{context.TaskType}'."));
        }

        if (_automation is not null && context.TaskType == JobTaskType.VideoRender)
            return _automation.ExecuteRenderAsync(context, onPreSideEffectCommitAsync, onProgressAsync, ct);

        return Task.FromResult(new JobExecutionResult(
            Success: false,
            ReasonCode: "automation_blocked",
            ErrorMessage: "Tự động hóa tạo video của MuseAI chưa sẵn sàng. Vui lòng thử lại khi bộ điều hợp đã được kiểm chứng."));
    }
}
