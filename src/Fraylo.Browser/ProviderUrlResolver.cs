using System;

namespace Fraylo.Browser;

/// <summary>
/// Ánh xạ mã định danh Provider hoặc loại Profile sang URL trang chủ/đăng nhập tương ứng.
/// Giúp mở thẳng trang đăng nhập của Provider (ChatGPT, Gemini, MuseAI, TikTok)
/// thay vì mở một cửa sổ trình duyệt trắng (about:blank).
/// </summary>
public static class ProviderUrlResolver
{
    public const string DefaultChatGptUrl = "https://chatgpt.com/";
    public const string DefaultGeminiUrl = "https://gemini.google.com/";
    public const string DefaultMuseAiUrl = "https://muse.ai/";
    public const string DefaultTikTokUrl = "https://www.tiktok.com/";
    public const string DefaultFacebookUrl = "https://www.facebook.com/";

    /// <summary>
    /// Giải quyết URL đích dựa trên providerKey, kind hoặc customUrl được truyền vào.
    /// </summary>
    /// <param name="providerKey">Khóa định danh provider (chatgpt-web, gemini-web, museai, tiktok,...)</param>
    /// <param name="profileKind">Loại profile (SharedAI, MuseAI, TikTok)</param>
    /// <param name="customUrl">URL tùy biến do caller chỉ định (nếu có)</param>
    /// <returns>URL đích hợp lệ hoặc null nếu không thể xác định</returns>
    public static string? ResolveUrl(string? providerKey, string? profileKind = null, string? customUrl = null)
    {
        // 1. Nếu caller cung cấp URL tùy biến hợp lệ, ưu tiên sử dụng
        if (!string.IsNullOrWhiteSpace(customUrl) &&
            Uri.TryCreate(customUrl.Trim(), UriKind.Absolute, out var customUri) &&
            (customUri.Scheme == Uri.UriSchemeHttp || customUri.Scheme == Uri.UriSchemeHttps))
        {
            return customUri.AbsoluteUri;
        }

        // 2. Phân giải theo providerKey
        var normalizedKey = providerKey?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedKey))
        {
            if (normalizedKey is "chatgpt-web" or "chatgpt" or "openai")
            {
                return DefaultChatGptUrl;
            }

            if (normalizedKey is "gemini-web" or "gemini" or "google")
            {
                return DefaultGeminiUrl;
            }

            if (normalizedKey is "museai" or "museai-video" or "muse")
            {
                return DefaultMuseAiUrl;
            }

            if (normalizedKey is "tiktok" or "tiktok-channel" or "tiktok_channel")
            {
                return DefaultTikTokUrl;
            }

            if (normalizedKey == "facebook")
            {
                return DefaultFacebookUrl;
            }
        }

        // 3. Phân giải theo profileKind nếu providerKey chưa xác định
        var normalizedKind = profileKind?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedKind))
        {
            if (normalizedKind == "museai")
            {
                return DefaultMuseAiUrl;
            }

            if (normalizedKind is "tiktok" or "tiktok-channel" or "tiktok_channel")
            {
                return DefaultTikTokUrl;
            }

            if (normalizedKind == "facebook")
            {
                return DefaultFacebookUrl;
            }

            if (normalizedKind == "sharedai")
            {
                // SharedAI trong hệ thống mặc định là ChatGPT
                return DefaultChatGptUrl;
            }
        }

        return null;
    }
}
