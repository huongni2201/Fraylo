using Microsoft.Playwright;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
using Fraylo.Application.Assets;
using Fraylo.Application.Jobs;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Assets;
using Fraylo.Contracts.Jobs;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Application.Providers;

/// <summary>Runs a text task in the job's isolated ChatGPT profile.</summary>
public sealed class ChatGptPlaywrightAutomation
{
    private readonly IBrowserProcessController _browser;
    private readonly IProfileRegistry _profiles;
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly StorageRootRegistry _roots;
    private readonly AssetService _assets;
    private readonly JobActivityLog _activity;

    public ChatGptPlaywrightAutomation(IBrowserProcessController browser, IProfileRegistry profiles,
        DbContextOptions<FrayloDbContext> dbOptions, StorageRootRegistry roots, AssetService assets)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _activity = new JobActivityLog(dbOptions);
    }

    // ChatGPT renames these attributes between releases; seen 2026-10-28 on a logged-in account: Send is "Send", replies lack data-message-author-role.
    private const string AssistantSelector = "[data-message-author-role='assistant'], [data-markdown-text-style='assistant-message']";
    private const string CopySelector = "[data-testid='copy-turn-action-button'], button[aria-label='Copy']";

    // Seen 2026-10-30: generated images live in this gallery with a blob: src (not https), and image-only replies have no assistant-message attributes.
    private const string GeneratedImages = "[...document.querySelectorAll(\"[data-testid='generated-image-gallery'] img\")].filter(img => img.naturalWidth >= 256 && img.naturalHeight >= 256 && img.currentSrc)";

    public async Task<JobExecutionResult> ExecuteTextAsync(
        JobExecutionContext job,
        Func<Task> beforeSubmit,
        Func<int, string, Task>? progress,
        CancellationToken ct)
    {
        await _activity.WriteAsync(job.JobId, "validate", "Bắt đầu kiểm tra prompt và phiên ChatGPT.", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(job.ProfileId) || string.IsNullOrWhiteSpace(job.PromptSnapshotText))
        {
            await _activity.WriteAsync(job.JobId, "error", "Thiếu profile hoặc prompt snapshot.", ct).ConfigureAwait(false);
            return new(false, ReasonCode: "invalid_input", ErrorMessage: "Thiếu profile hoặc prompt đã chụp của job.");
        }

        var prompt = ReadPrompt(job.PromptSnapshotText);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            await _activity.WriteAsync(job.JobId, "error", "Prompt snapshot không có renderedText.", ct).ConfigureAwait(false);
            return new(false, ReasonCode: "invalid_input", ErrorMessage: "Prompt snapshot không có nội dung đã render.");
        }
        await _activity.WriteAsync(job.JobId, "validate", "Prompt snapshot hợp lệ.", ct).ConfigureAwait(false);

        var profile = await _profiles.GetProfileAsync(job.ProfileId, ct).ConfigureAwait(false);
        if (profile is null || profile.ProviderKey != job.ProviderKey ||
            profile.SessionStatus != BrowserSessionStatus.Connected || string.IsNullOrWhiteSpace(profile.AccountIdentity))
            return new(false, ReasonCode: "login_required", ErrorMessage: "Phiên ChatGPT của tài khoản này chưa được xác minh. Hãy đăng nhập và kiểm tra phiên.");

        var handle = _browser.TryGetContext(job.ProfileId);
        if (handle is null)
            return new(false, ReasonCode: "automation_blocked", ErrorMessage: "Profile ChatGPT chưa được mở cho tác vụ thử nghiệm.");

        IPage? page = null;
        var submitted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            page = await handle.Context.NewPageAsync().ConfigureAwait(false);
            await _activity.WriteAsync(job.JobId, "browser", "Đã mở tab ChatGPT cho job.", ct).ConfigureAwait(false);
            await page.GotoAsync("https://chatgpt.com/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).WaitAsync(ct).ConfigureAwait(false);
            await _activity.WriteAsync(job.JobId, "browser", "Đã tải trang ChatGPT; đang chờ ô nhập.", ct).ConfigureAwait(false);

            // A fresh page avoids accidentally appending to another job's conversation.
            var composer = page.Locator("#prompt-textarea:visible, #mobile-composer-prompt:visible, textarea[placeholder='Ask ChatGPT']:visible, [contenteditable='true'][role='textbox']:visible").First;
            try
            {
                await composer.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var editableCount = await page.Locator("textarea:visible, [contenteditable='true']:visible, [role='textbox']:visible").CountAsync().ConfigureAwait(false);
                await RecordFailureAsync(job.JobId, $"Không tìm thấy ô nhập ChatGPT; số ô chỉnh sửa hiển thị: {editableCount}.").ConfigureAwait(false);
                throw;
            }
            await _activity.WriteAsync(job.JobId, "browser", "Đã tìm thấy ô nhập ChatGPT.", ct).ConfigureAwait(false);
            var imagePaths = await ResolveInputImagesAsync(job, ct).ConfigureAwait(false);
            await _activity.WriteAsync(job.JobId, "media", $"Đã kiểm tra {imagePaths.Count} ảnh tham chiếu.", ct).ConfigureAwait(false);
            var assistantMessages = page.Locator(AssistantSelector);
            var initialAssistantCount = await assistantMessages.CountAsync().ConfigureAwait(false);
            var initialCopyCount = await page.Locator(CopySelector).CountAsync().ConfigureAwait(false);
            if (imagePaths.Count > 0)
            {
                // The composer mounts its hidden file input after the textarea, so counting at this moment races the render.
                var fileInput = page.Locator("input[type='file']").First;
                try
                {
                    await fileInput.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 }).WaitAsync(ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // shortcut: current ChatGPT uses "Add files and more" instead of composer-plus-btn; upgrade if the picker menu changes.
                    var attach = page.Locator("[data-testid='composer-plus-btn'], button[aria-label^='Add files']").First;
                    await attach.ClickAsync(new() { Timeout = 15000 }).WaitAsync(ct).ConfigureAwait(false);
                    await fileInput.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 }).WaitAsync(ct).ConfigureAwait(false);
                }
                await _activity.WriteAsync(job.JobId, "media", "Đang chọn ảnh để tải lên ChatGPT.", ct).ConfigureAwait(false);
                await fileInput.SetInputFilesAsync(imagePaths).WaitAsync(ct).ConfigureAwait(false);
                // ChatGPT empties the file input once it takes the files, so the input cannot prove the upload; the Send gate below does.
                await _activity.WriteAsync(job.JobId, "media", "Đã chọn ảnh; chờ ChatGPT xử lý tệp trước khi gửi.", ct).ConfigureAwait(false);
            }
            await composer.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
            await page.Keyboard.InsertTextAsync(prompt).WaitAsync(ct).ConfigureAwait(false);
            var send = page.Locator("[data-testid='send-button']:visible, button[aria-label='Send']:visible, button[aria-label='Send message']:visible").First;
            await send.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 }).WaitAsync(ct).ConfigureAwait(false);
            // Uploads keep Send disabled until the image finishes, which can exceed 30 seconds.
            await page.WaitForFunctionAsync("() => [...document.querySelectorAll(\"[data-testid=send-button], button[aria-label='Send'], button[aria-label='Send message']\")].some(button => button.getClientRects().length && !button.disabled && button.getAttribute('aria-disabled') !== 'true')",
                null, new() { Timeout = 120000 }).WaitAsync(ct).ConfigureAwait(false);
            await beforeSubmit().ConfigureAwait(false);
            await _activity.WriteAsync(job.JobId, "submit", "Đã ghi checkpoint trước khi gửi.", ct).ConfigureAwait(false);
            submitted = true; // The checkpoint is durable before the click; an uncertain click must never be retried blindly.
            await send.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
            await _activity.WriteAsync(job.JobId, "submit", "Đã bấm gửi trên ChatGPT.", ct).ConfigureAwait(false);
            if (progress is not null) await progress(50, "Đang chờ ChatGPT trả lời").ConfigureAwait(false);

            if (job.TaskType == JobTaskType.Image)
            {
                // An image-only reply has neither the assistant-message attributes nor a Copy button, so gate on a generated image outside the user's turn plus a finished stream.
                await page.WaitForFunctionAsync($"() => {GeneratedImages}.length > 0 && !document.querySelector(\"[data-testid='stop-button'], button[aria-label='Stop streaming']\")",
                    null, new() { Timeout = 600000 }).WaitAsync(ct).ConfigureAwait(false);
                var source = await page.EvaluateAsync<string>($"() => {GeneratedImages}.at(-1)?.currentSrc || ''").WaitAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(source))
                    throw new InvalidOperationException("Không tìm thấy ảnh tạo đủ kích thước trong phản hồi ChatGPT.");
                await _activity.WriteAsync(job.JobId, "result", "Đã thấy ảnh kết quả; đang tải về.", ct).ConfigureAwait(false);

                // Fetch inside the page so the request carries the real browser's cookies and TLS fingerprint.
                var dataUrl = await page.EvaluateAsync<string>("async src => { const r = await fetch(src, { credentials: 'include' }); if (!r.ok) throw new Error('HTTP ' + r.status); const blob = await r.blob(); return await new Promise((resolve, reject) => { const f = new FileReader(); f.onload = () => resolve(f.result); f.onerror = reject; f.readAsDataURL(blob); }); }", source).WaitAsync(ct).ConfigureAwait(false);
                var (fileName, base64) = ValidateGeneratedImage(dataUrl);
                var uploaded = await _assets.UploadAssetAsync(new UploadAssetRequest(job.ProjectId, "Image", fileName, base64, StorageRootId: job.StorageRootId), ct).ConfigureAwait(false);
                if (uploaded.Asset is null)
                    return new(false, ReasonCode: "asset_save_failed", ErrorMessage: "Không lưu được ảnh ChatGPT.", SideEffectSubmitted: true);
                await _activity.WriteAsync(job.JobId, "result", "Đã lưu ảnh kết quả thành Asset.", ct).ConfigureAwait(false);
                return new(true, ResultReference: uploaded.Asset.AssetId, SideEffectSubmitted: true);
            }
            await page.WaitForFunctionAsync("previous => document.querySelectorAll('[data-message-author-role=assistant], [data-markdown-text-style=assistant-message]').length > previous",
                initialAssistantCount, new() { Timeout = 300000 }).WaitAsync(ct).ConfigureAwait(false);
            var response = assistantMessages.Last;
            await response.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 300000 }).WaitAsync(ct).ConfigureAwait(false);
            // The copy action sits beside the reply, not inside it. Wait for a new one: the previous turn's copy button is already visible while this reply is still streaming.
            await page.WaitForFunctionAsync($"previous => document.querySelectorAll(\"{CopySelector}\").length > previous",
                initialCopyCount, new() { Timeout = 600000 }).WaitAsync(ct).ConfigureAwait(false);
            await page.Locator(CopySelector).Last
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 600000 }).WaitAsync(ct).ConfigureAwait(false);
            var result = (await response.InnerTextAsync().WaitAsync(ct).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(result))
                return new(false, ReasonCode: "empty_response", ErrorMessage: "ChatGPT trả về nội dung rỗng.", SideEffectSubmitted: true);

            await _activity.WriteAsync(job.JobId, "result", "Đã nhận nội dung phản hồi.", ct).ConfigureAwait(false);
            return new(true, ResultReference: page.Url, ResultText: result, SideEffectSubmitted: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PlaywrightException ex)
        {
            await RecordFailureAsync(job.JobId, submitted ? $"Lỗi sau khi gửi: {ex.Message.Split((char)10)[0]}" : "Lỗi thao tác browser trước khi gửi.").ConfigureAwait(false);
            return new(false, ReasonCode: submitted ? "execution_failed" : "automation_blocked",
                ErrorMessage: $"Không thể hoàn tất thao tác ChatGPT Web: {ex.Message}", SideEffectSubmitted: submitted);
        }
        catch (TimeoutException ex)
        {
            await RecordFailureAsync(job.JobId, submitted ? $"Hết thời gian sau khi gửi: {ex.Message.Split((char)10)[0]}" : "Hết thời gian trước khi gửi.").ConfigureAwait(false);
            return new(false, ReasonCode: submitted ? "execution_failed" : "automation_blocked",
                ErrorMessage: $"Quá thời gian chờ ChatGPT Web: {ex.Message}", SideEffectSubmitted: submitted);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(job.JobId, submitted ? $"Tác vụ lỗi sau khi gửi: {ex.Message.Split((char)10)[0]}" : "Tác vụ lỗi trước khi gửi.").ConfigureAwait(false);
            return new(false, ReasonCode: submitted ? "execution_failed" : "automation_blocked",
                ErrorMessage: $"Không hoàn tất được tác vụ ChatGPT Web: {ex.Message}", SideEffectSubmitted: submitted);
        }
        finally
        {
            if (page is not null && !page.IsClosed)
                try { await page.CloseAsync().ConfigureAwait(false); } catch { }
        }
    }

    private async Task RecordFailureAsync(string jobId, string message)
    {
        try { await _activity.WriteAsync(jobId, "error", message, CancellationToken.None).ConfigureAwait(false); }
        catch { /* Preserve the original provider error. */ }
    }

    private static string ReadPrompt(string snapshot)
    {
        try
        {
            using var doc = JsonDocument.Parse(snapshot);
            return (doc.RootElement.TryGetProperty("renderedText", out var text) ||
                    doc.RootElement.TryGetProperty("RenderedText", out text)) ? text.GetString()?.Trim() ?? "" : "";
        }
        catch (JsonException)
        {
            return snapshot.Trim();
        }
    }

    private async Task<IReadOnlyList<string>> ResolveInputImagesAsync(JobExecutionContext job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ConfigSnapshotJson))
        {
            if (job.TaskType == JobTaskType.Image || job.TaskType == JobTaskType.VideoPrompt)
                throw new InvalidOperationException("Tác vụ cần ảnh tham chiếu nhưng thiếu media snapshot.");
            return [];
        }
        using var doc = JsonDocument.Parse(job.ConfigSnapshotJson);
        if (!doc.RootElement.TryGetProperty("MediaSnapshot", out var media) || media.ValueKind != JsonValueKind.Object)
        {
            if (job.TaskType == JobTaskType.Image || job.TaskType == JobTaskType.VideoPrompt)
                throw new InvalidOperationException("Tác vụ cần ảnh tham chiếu nhưng media snapshot không hợp lệ.");
            return [];
        }
        string? Id(string name) => media.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var model = Id("ModelAssetId");
        var product = Id("ProductAssetId");
        var ready = Id("ReadyImageAssetId");
        var source = Id("SourceImageAssetId");
        var expectedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        void Expect(string? id, string? hash)
        {
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(hash)) expectedHashes[id] = hash;
        }
        Expect(model, Id("ModelAssetHash"));
        Expect(product, Id("ProductAssetHash"));
        Expect(ready, Id("ReadyImageAssetHash"));
        Expect(source, Id("SourceImageAssetHash"));
        // Job Image ghép ảnh cần cả mẫu và sản phẩm; job ảnh storyboard 3×3 chỉ có một ảnh nguồn.
        var composeImage = !string.IsNullOrWhiteSpace(model) || !string.IsNullOrWhiteSpace(product);
        if (job.TaskType == JobTaskType.Image && composeImage && (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(product)))
            throw new InvalidOperationException("Chế độ ghép mẫu và sản phẩm cần đủ hai ảnh tham chiếu.");
        if (job.TaskType == JobTaskType.Image && !composeImage && string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("Tác vụ tạo ảnh cần ảnh nguồn nhưng media snapshot không có.");
        var ids = job.TaskType switch
        {
            JobTaskType.Image => composeImage ? new[] { model, product } : new[] { source },
            JobTaskType.VideoPrompt => new[] { source ?? ready ?? product },
            _ => new[] { model, product, ready }
        };
        var paths = new List<string>();
        await using var db = new FrayloDbContext(_dbOptions);
        foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
            if (asset is null || asset.AssetType != "Image" || asset.Status != "Linked" ||
                (asset.ProjectId is not null && asset.ProjectId != job.ProjectId))
                throw new InvalidOperationException($"Ảnh tham chiếu '{id}' không hợp lệ cho Project.");
            var root = await _roots.ResolveRootPathAsync(asset.StorageRootId, ct).ConfigureAwait(false);
            StoragePathGuard.EnsureValidRelativePath(asset.RelativePath);
            var path = Path.GetFullPath(Path.Combine(root, asset.RelativePath));
            if (!StoragePathGuard.IsSubPathOf(root, path) || !File.Exists(path))
                throw new InvalidOperationException($"Ảnh tham chiếu '{id}' bị thiếu hoặc vượt ngoài thư mục lưu trữ.");
            await using var file = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            if (!hash.Equals(asset.Sha256Hash, StringComparison.OrdinalIgnoreCase) ||
                (expectedHashes.TryGetValue(id!, out var expected) && !hash.Equals(expected, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Ảnh tham chiếu '{id}' đã thay đổi sau khi tạo job.");
            paths.Add(path);
        }
        return paths;
    }

    private static (string FileName, string Base64) ValidateGeneratedImage(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0 || !dataUrl[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ChatGPT không trả dữ liệu ảnh hợp lệ.");
        var fileName = dataUrl.StartsWith("data:image/png;", StringComparison.OrdinalIgnoreCase) ? "generated.png" :
            dataUrl.StartsWith("data:image/jpeg;", StringComparison.OrdinalIgnoreCase) ? "generated.jpg" :
            dataUrl.StartsWith("data:image/webp;", StringComparison.OrdinalIgnoreCase) ? "generated.webp" : null;
        if (fileName is null) throw new InvalidOperationException("Định dạng ảnh ChatGPT không được hỗ trợ.");
        var base64 = dataUrl[(comma + 1)..];
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length < 10_000) throw new InvalidOperationException("Ảnh ChatGPT quá nhỏ hoặc chưa tải xong.");
        var validMagic = fileName switch
        {
            "generated.png" => bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            "generated.jpg" => bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            _ => bytes.AsSpan().StartsWith(new byte[] { 0x52, 0x49, 0x46, 0x46 }) &&
                bytes.AsSpan(8).StartsWith(new byte[] { 0x57, 0x45, 0x42, 0x50 })
        };
        if (!validMagic) throw new InvalidOperationException("Nội dung ảnh ChatGPT không khớp định dạng công bố.");
        return (fileName, base64);
    }
}
