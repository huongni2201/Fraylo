using Fraylo.Application.Jobs;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;

namespace Fraylo.Application.Providers;

/// <summary>Gemini Web automation trial; selectors require live verification.</summary>
public sealed class GeminiWebAdapter : IProviderAdapter
{
    private readonly GeminiPlaywrightAutomation? _automation;

    public GeminiWebAdapter(GeminiPlaywrightAutomation? automation = null) => _automation = automation;

    public string ProviderKey => ProviderKeys.GeminiWeb;

    public IReadOnlyList<ProviderTaskCapabilityDto> GetCapabilities() =>
        ProviderCapabilities.GetDefaultCapabilities(ProviderKey)
            .Select(capability => GeminiTrialMode.Enabled && _automation is not null &&
                capability.TaskType is JobTaskType.Script or JobTaskType.Image or JobTaskType.VideoPrompt or JobTaskType.AnalyticsAnalysis or JobTaskType.FacebookContentRewrite
                ? capability with
                {
                    VerificationStatus = ProviderVerificationStatus.Trial,
                    AutomationSupported = true,
                    EvidenceId = capability.TaskType is JobTaskType.AnalyticsAnalysis or JobTaskType.FacebookContentRewrite ? null : "user-enabled-gemini-playwright-trial"
                }
                : capability).ToArray();

    public Task<ProviderTaskCapabilityDto?> GetCapabilityForTaskAsync(string taskType, CancellationToken ct = default) =>
        Task.FromResult(GetCapabilities().FirstOrDefault(capability =>
            string.Equals(capability.TaskType, taskType, StringComparison.OrdinalIgnoreCase)));

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
                ErrorMessage: $"Gemini Web không hỗ trợ tác vụ '{context.TaskType}'."));
        }

        if (_automation is not null && context.TaskType is JobTaskType.Script or JobTaskType.Image or JobTaskType.VideoPrompt or JobTaskType.AnalyticsAnalysis or JobTaskType.FacebookContentRewrite)
            return _automation.ExecuteTextAsync(context, onPreSideEffectCommitAsync, onProgressAsync, ct);

        return Task.FromResult(new JobExecutionResult(
            Success: false,
            ReasonCode: "automation_blocked",
            ErrorMessage: "Tự động hóa Gemini Web chưa được xác minh. Không thể thực thi cho đến khi bộ điều hợp tự động hóa sẵn sàng."));
    }
}
