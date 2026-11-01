using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Application.Jobs;
using Fraylo.Contracts.Providers;

namespace Fraylo.Application.Providers;

/// <summary>
/// Giao diện adapter cho các nhà cung cấp AI (ChatGPT Web, Gemini Web, MuseAI).
/// Cung cấp capabilities chuẩn và thực thi tác vụ với cơ chế chống gửi lặp (pre-side-effect commit)
/// và báo cáo tiến độ theo execution-contract-v1.1 §1.3, §3.4.
/// </summary>
public interface IProviderAdapter
{
    /// <summary>
    /// Định danh provider (chatgpt-web, gemini-web, museai).
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Lấy danh sách toàn bộ capabilities của provider này.
    /// </summary>
    IReadOnlyList<ProviderTaskCapabilityDto> GetCapabilities();

    /// <summary>
    /// Lấy capability cụ thể theo loại tác vụ (Script, Image, VideoPrompt, VideoRender).
    /// </summary>
    Task<ProviderTaskCapabilityDto?> GetCapabilityForTaskAsync(string taskType, CancellationToken ct = default);

    /// <summary>
    /// Thực thi tác vụ AI với cơ chế gọi hook commit side-effect trước khi submit và báo cáo tiến trình.
    /// </summary>
    Task<JobExecutionResult> ExecuteAsync(
        JobExecutionContext context,
        Func<Task> onPreSideEffectCommitAsync,
        Func<int, string, Task>? onProgressAsync,
        CancellationToken ct = default);
}
