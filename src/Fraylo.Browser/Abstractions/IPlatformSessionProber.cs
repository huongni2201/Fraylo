using System.Threading;
using System.Threading.Tasks;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Accounts;
using Fraylo.Contracts.Browser;

namespace Fraylo.Browser.Abstractions;

/// <summary>
/// Hợp đồng phân giải và thẩm định phiên làm việc cho từng nền tảng/provider cụ thể (ChatGPT, Gemini, MuseAI, TikTok,...).
/// </summary>
public interface IPlatformSessionProber
{
    /// <summary>
    /// Kiểm tra prober có hỗ trợ nền tảng hoặc providerKey được cung cấp hay không.
    /// </summary>
    bool SupportsPlatform(string platformOrProviderKey);

    /// <summary>
    /// URL mặc định để điều hướng hoặc kiểm tra phiên của nền tảng.
    /// </summary>
    string DefaultUrl { get; }

    /// <summary>
    /// Thực hiện probe thực tế trên Chromium Persistent Context của profile để xác định trạng thái phiên và danh tính.
    /// </summary>
    Task<ProfileIdentityResolutionResult> ProbeAsync(
        BrowserProfileInfo profile,
        IBrowserProcessController processController,
        CancellationToken cancellationToken);
}
