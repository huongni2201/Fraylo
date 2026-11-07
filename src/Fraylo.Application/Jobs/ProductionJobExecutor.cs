using System;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Application.Providers;
using Fraylo.Application.Analytics;
using Fraylo.Contracts.Providers;

namespace Fraylo.Application.Jobs;

/// <summary>
/// Bộ thực thi tác vụ Production định tuyến theo Job Snapshot (context.ProviderKey).
/// Tích hợp ProviderRegistry:
/// - Nếu adapter cho ProviderKey đã được đăng ký và sẵn sàng: gọi ExecuteAsync của adapter đó.
/// - Nếu adapter chưa có browser automation hoặc chưa sẵn sàng:
///   Trả về Success = false, ReasonCode = "automation_blocked" (hoặc "adapter_not_available"),
///   dừng tác vụ với lỗi rõ ràng, không chuyển giao thủ công,
///   TUYỆT ĐỐI KHÔNG sinh kết quả giả mạo hoặc báo URI ảo (PV-R1).
/// </summary>
public sealed class ProductionJobExecutor : IJobExecutor
{
    private readonly ProviderRegistry _providerRegistry;

    public ProductionJobExecutor(ProviderRegistry providerRegistry)
    {
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
    }

    public async Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        Func<Task> onPreSideEffectCommitAsync,
        Func<int, string, Task>? onProgressAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TaskType == Fraylo.Contracts.Jobs.JobTaskType.FacebookContentRewrite && !string.IsNullOrWhiteSpace(context.ExistingResultText))
            return Facebook.FacebookContentRewriteResultParser.Validate(context, new(true, ResultReference: context.ExistingResultReference, ResultText: context.ExistingResultText));

        var providerKey = context.ProviderKey;
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return new JobExecutionResult(
                Success: false,
                ResultReference: null,
                ResultText: null,
                ReasonCode: "missing_provider",
                ErrorMessage: "Job snapshot không chứa thông tin ProviderKey.",
                SideEffectSubmitted: false
            );
        }

        if (_providerRegistry.TryGetAdapter(providerKey, out var adapter) && adapter != null)
        {
            var capability = await adapter.GetCapabilityForTaskAsync(context.TaskType, cancellationToken).ConfigureAwait(false);
            if (capability is null || capability.VerificationStatus == ProviderVerificationStatus.Unsupported)
            {
                return new JobExecutionResult(
                    Success: false,
                    ReasonCode: "CAPABILITY_UNSUPPORTED",
                    ErrorMessage: $"Nhà cung cấp '{providerKey}' không hỗ trợ tác vụ '{context.TaskType}'."
                );
            }

            if (!capability.AutomationSupported ||
                capability.VerificationStatus is not (ProviderVerificationStatus.Verified or ProviderVerificationStatus.Trial))
            {
                return new JobExecutionResult(
                    Success: false,
                    ResultReference: null,
                    ResultText: null,
                    ReasonCode: "automation_blocked",
                    ErrorMessage: $"Nhà cung cấp '{providerKey}' cho tác vụ '{context.TaskType}' chưa sẵn sàng tự động hóa (VerificationStatus: {capability.VerificationStatus}, AutomationSupported: {capability.AutomationSupported}).",
                    SideEffectSubmitted: false
                );
            }

            var result = await adapter.ExecuteAsync(context, onPreSideEffectCommitAsync, onProgressAsync, cancellationToken).ConfigureAwait(false);
            return Facebook.FacebookContentRewriteResultParser.Validate(context, AnalyticsAnalysisResultParser.Validate(context, result));
        }

        return new JobExecutionResult(
            Success: false,
            ResultReference: null,
            ResultText: null,
            ReasonCode: "adapter_not_available",
            ErrorMessage: $"Bộ điều hợp tự động hóa cho nhà cung cấp '{providerKey}' chưa sẵn sàng trong phiên bản này.",
            SideEffectSubmitted: false
        );
    }
}
