using Fraylo.Application.Jobs;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;

namespace Fraylo.Application.Providers;

/// <summary>ChatGPT Web script, image and prompt trial; not live verification.</summary>
public sealed class ChatGptWebAdapter : IProviderAdapter
{
    private readonly ChatGptPlaywrightAutomation? _automation;

    public ChatGptWebAdapter(ChatGptPlaywrightAutomation? automation = null) => _automation = automation;

    public string ProviderKey => ProviderKeys.ChatGPTWeb;

    public IReadOnlyList<ProviderTaskCapabilityDto> GetCapabilities() =>
        ProviderCapabilities.GetDefaultCapabilities(ProviderKey)
            .Select(capability => ChatGptTrialMode.Enabled && _automation is not null &&
                capability.TaskType is JobTaskType.Script or JobTaskType.Image or JobTaskType.VideoPrompt or JobTaskType.AnalyticsAnalysis or JobTaskType.FacebookContentRewrite
                ? capability with
                {
                    VerificationStatus = ProviderVerificationStatus.Trial,
                    AutomationSupported = true,
                    EvidenceId = capability.TaskType is JobTaskType.AnalyticsAnalysis or JobTaskType.FacebookContentRewrite ? null : "user-enabled-playwright-trial"
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
                ErrorMessage: $"ChatGPT Web không hỗ trợ tác vụ '{context.TaskType}'."));
        }

        if (_automation is not null && context.TaskType is JobTaskType.Script or JobTaskType.Image or JobTaskType.VideoPrompt or JobTaskType.AnalyticsAnalysis or JobTaskType.FacebookContentRewrite)
            return _automation.ExecuteTextAsync(context, onPreSideEffectCommitAsync, onProgressAsync, ct);

        return Task.FromResult(new JobExecutionResult(
            Success: false,
            ReasonCode: "automation_blocked",
            ErrorMessage: "Tự động hóa ChatGPT Web chưa được xác minh. Không thể thực thi cho đến khi bộ điều hợp tự động hóa sẵn sàng."));
    }
}
