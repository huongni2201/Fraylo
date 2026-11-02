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

/// <summary>Runs a text task in the job's isolated Gemini profile.</summary>
public sealed class GeminiPlaywrightAutomation
{
    private readonly IBrowserProcessController _browser;
    private readonly IProfileRegistry _profiles;
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly StorageRootRegistry _roots;
    private readonly AssetService _assets;
    private readonly JobActivityLog _activity;

    public GeminiPlaywrightAutomation(IBrowserProcessController browser, IProfileRegistry profiles,
        DbContextOptions<FrayloDbContext> dbOptions, StorageRootRegistry roots, AssetService assets)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _activity = new JobActivityLog(dbOptions);
    }

    public async Task<JobExecutionResult> ExecuteTextAsync(
        JobExecutionContext job,
        Func<Task> beforeSubmit,
        Func<int, string, Task>? progress,
        CancellationToken ct)
    {
        await _activity.WriteAsync(job.JobId, "validate", "Bắt đầu kiểm tra prompt và phiên Gemini.", ct).ConfigureAwait(false);
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
            return new(false, ReasonCode: "login_required", ErrorMessage: "Phiên Gemini của tài khoản này chưa được xác minh. Hãy đăng nhập và kiểm tra phiên.");

        var handle = _browser.TryGetContext(job.ProfileId);
        if (handle is null)
            return new(false, ReasonCode: "automation_blocked", ErrorMessage: "Profile Gemini chưa được mở cho tác vụ thử nghiệm.");

        IPage? page = null;
        var submitted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            page = await handle.Context.NewPageAsync().ConfigureAwait(false);
            await _activity.WriteAsync(job.JobId, "browser", "Đã mở tab Gemini cho job.", ct).ConfigureAwait(false);
            await page.GotoAsync("https://gemini.google.com/app", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).WaitAsync(ct).ConfigureAwait(false);

            var composer = page.Locator("rich-textarea [contenteditable='true'], [role='textbox'][contenteditable='true']").First;
            await composer.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }).WaitAsync(ct).ConfigureAwait(false);
            var imagePaths = await ResolveInputImagesAsync(job, ct).ConfigureAwait(false);
            await _activity.WriteAsync(job.JobId, "media", $"Đã kiểm tra {imagePaths.Count} ảnh tham chiếu.", ct).ConfigureAwait(false);
            var assistantMessages = page.Locator("message-content, .model-response-text");
            var initialAssistantCount = await assistantMessages.CountAsync().ConfigureAwait(false);
            if (imagePaths.Count > 0)
            {
                // Gemini renders its file inputs only after the upload menu opens; the first input is the document picker, so target the image one.
                var fileInput = page.Locator("input[type='file'][accept*='image']").First;
                if (await fileInput.CountAsync().ConfigureAwait(false) == 0)
                {
                    var attach = page.Locator("button[aria-label*='Upload' i], button[aria-label*='Tải' i], button[aria-label*='Add files' i]").First;
                    await attach.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                await fileInput.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 }).WaitAsync(ct).ConfigureAwait(false);
                await fileInput.SetInputFilesAsync(imagePaths).WaitAsync(ct).ConfigureAwait(false);
                // Do not poll input.files: job log showed a 30s timeout here, and the send-button wait below covers the upload.
            }
            await composer.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
            await page.Keyboard.InsertTextAsync(prompt).WaitAsync(ct).ConfigureAwait(false);
            var send = page.Locator("button[aria-label*='Send' i]:visible, button[aria-label*='Gửi' i]:visible, button.send-button:visible").First;
            await send.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 }).WaitAsync(ct).ConfigureAwait(false);
            // Send is "Gửi tin nhắn" in Vietnamese UI; uploads can take well over 30 seconds, so wait longer.
            await page.WaitForFunctionAsync("() => [...document.querySelectorAll('button[aria-label*=Send i], button[aria-label*=Gửi i], button.send-button')].some(button => button.getClientRects().length && !button.disabled && button.getAttribute('aria-disabled') !== 'true')",
                null, new() { Timeout = 120000 }).WaitAsync(ct).ConfigureAwait(false);
            await beforeSubmit().ConfigureAwait(false);
            await _activity.WriteAsync(job.JobId, "submit", "Đã ghi checkpoint trước khi gửi.", ct).ConfigureAwait(false);
            submitted = true; // The checkpoint is durable before the click; an uncertain click must never be retried blindly.
            await send.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
            await _activity.WriteAsync(job.JobId, "submit", "Đã bấm gửi trên Gemini.", ct).ConfigureAwait(false);
            if (progress is not null) await progress(50, "Đang chờ Gemini trả lời").ConfigureAwait(false);

            await page.WaitForFunctionAsync("previous => document.querySelectorAll('message-content, .model-response-text').length > previous",
                initialAssistantCount, new() { Timeout = 900000 }).WaitAsync(ct).ConfigureAwait(false);
            var response = assistantMessages.Last;
            await response.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 900000 }).WaitAsync(ct).ConfigureAwait(false);
            // The prompt's own "Sao chép câu lệnh" button sits before the reply, so the copy gate is scoped to the reply container.
            await page.Locator("response-container").Last.Locator("button[aria-label*='Copy' i], button[aria-label*='Sao chép' i]").First
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 600000 }).WaitAsync(ct).ConfigureAwait(false);
            if (job.TaskType == JobTaskType.Image)
            {
                // Gemini renders generated images next to the text (message-content), not inside it, so search the whole reply container.
                var reply = page.Locator("response-container").Last;
                await page.WaitForFunctionAsync("() => { const reply = [...document.querySelectorAll('response-container')].at(-1); return !!reply && [...reply.querySelectorAll('img')].some(img => img.naturalWidth >= 256 && img.naturalHeight >= 256); }",
                    null, new() { Timeout = 300000 }).WaitAsync(ct).ConfigureAwait(false);
                var source = await reply.EvaluateAsync<string>("reply => [...reply.querySelectorAll('img')].filter(img => img.naturalWidth >= 256 && img.naturalHeight >= 256).at(-1)?.currentSrc || ''").WaitAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(source))
                    throw new InvalidOperationException("Không tìm thấy ảnh tạo đủ kích thước trong phản hồi Gemini.");
                string dataUrl;
                if (source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    dataUrl = source;
                }
                else if (source.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                {
                    // Gemini keeps swapping/revoking blob URLs while it is still generating, so read only once the same image src is stable and retry on failure.
                    dataUrl = await page.EvaluateAsync<string>("""
                        async () => {
                          const pick = () => [...(([...document.querySelectorAll('response-container')].at(-1)) ?? document).querySelectorAll('img')]
                            .filter(i => i.naturalWidth >= 256 && i.naturalHeight >= 256).at(-1);
                          const toDataUrl = blob => new Promise((resolve, reject) => { const r = new FileReader(); r.onload = () => resolve(r.result); r.onerror = reject; r.readAsDataURL(blob); });
                          let last = '';
                          for (let i = 0; i < 45; i++) {
                            const img = pick();
                            const src = img?.currentSrc || img?.src || '';
                            if (src && src === last) {
                              try { return await toDataUrl(await (await fetch(src)).blob()); } catch { }
                              try {
                                const c = document.createElement('canvas'); c.width = img.naturalWidth; c.height = img.naturalHeight;
                                c.getContext('2d').drawImage(img, 0, 0); return c.toDataURL('image/png');
                              } catch { }
                            }
                            last = src;
                            await new Promise(r => setTimeout(r, 2000));
                          }
                          throw new Error('Gemini chưa trả ảnh ổn định để lưu.');
                        }
                        """).WaitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    if (!Uri.TryCreate(source, UriKind.Absolute, out var imageUri) || imageUri.Scheme != Uri.UriSchemeHttps)
                        throw new InvalidOperationException("URL ảnh Gemini không hợp lệ.");

                    using var playwright = await Playwright.CreateAsync().WaitAsync(ct).ConfigureAwait(false);
                    await using var request = await playwright.APIRequest.NewContextAsync(new()
                    {
                        StorageState = await handle.Context.StorageStateAsync().WaitAsync(ct).ConfigureAwait(false)
                    }).WaitAsync(ct).ConfigureAwait(false);
                    var imageResponse = await request.GetAsync(source, new() { Timeout = 30000 }).WaitAsync(ct).ConfigureAwait(false);
                    if (!imageResponse.Ok) throw new InvalidOperationException("Không tải được ảnh kết quả từ Gemini.");
                    var imageBytes = await imageResponse.BodyAsync().WaitAsync(ct).ConfigureAwait(false);
                    var contentType = imageResponse.Headers.TryGetValue("content-type", out var header) ? header.Split(';')[0].Trim() : "";
                    dataUrl = $"data:{contentType};base64,{Convert.ToBase64String(imageBytes)}";
                }
                var (fileName, base64) = ValidateGeneratedImage(dataUrl);
                var uploaded = await _assets.UploadAssetAsync(new UploadAssetRequest(job.ProjectId, "Image", fileName, base64, StorageRootId: job.StorageRootId), ct).ConfigureAwait(false);
                if (uploaded.Asset is null)
                    return new(false, ReasonCode: "asset_save_failed", ErrorMessage: "Không lưu được ảnh Gemini.", SideEffectSubmitted: true);
                await _activity.WriteAsync(job.JobId, "result", "Đã lưu ảnh kết quả thành Asset.", ct).ConfigureAwait(false);
                return new(true, ResultReference: uploaded.Asset.AssetId, SideEffectSubmitted: true);
            }
            // Read from the same container as the copy gate and poll: Gemini can render the text after the copy button appears.
            var replyBox = page.Locator("response-container").Last;
            const string readReply = "box => [...box.querySelectorAll('message-content, .model-response-text')].map(e => e.innerText.trim()).sort((a, b) => b.length - a.length)[0] ?? ''";
            var result = "";
            for (var i = 0; i < 30 && string.IsNullOrWhiteSpace(result); i++)
            {
                if (i > 0) await Task.Delay(500, ct).ConfigureAwait(false);
                result = (await replyBox.EvaluateAsync<string>(readReply).WaitAsync(ct).ConfigureAwait(false)).Trim();
            }
            if (string.IsNullOrWhiteSpace(result))
            {
                var diag = await replyBox.EvaluateAsync<string>("box => `containers=${document.querySelectorAll('response-container').length}, message-content=${box.querySelectorAll('message-content').length}, model-response-text=${box.querySelectorAll('.model-response-text').length}, text=${box.innerText.trim().slice(0, 200)}`").WaitAsync(ct).ConfigureAwait(false);
                await _activity.WriteAsync(job.JobId, "error", $"Reply rỗng sau 15s. DOM: {diag}", ct).ConfigureAwait(false);
                return new(false, ReasonCode: "empty_response", ErrorMessage: "Gemini trả về nội dung rỗng.", SideEffectSubmitted: true);
            }

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
                ErrorMessage: $"Không thể hoàn tất thao tác Gemini Web: {ex.Message}", SideEffectSubmitted: submitted);
        }
        catch (TimeoutException ex)
        {
            await RecordFailureAsync(job.JobId, submitted ? $"Hết thời gian sau khi gửi: {ex.Message.Split((char)10)[0]}" : "Hết thời gian trước khi gửi.").ConfigureAwait(false);
            return new(false, ReasonCode: submitted ? "execution_failed" : "automation_blocked",
                ErrorMessage: $"Quá thời gian chờ Gemini Web: {ex.Message}", SideEffectSubmitted: submitted);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(job.JobId, submitted ? $"Tác vụ lỗi sau khi gửi: {ex.Message.Split((char)10)[0]}" : $"Tác vụ lỗi trước khi gửi: {ex.Message.Split((char)10)[0]}").ConfigureAwait(false);
            return new(false, ReasonCode: submitted ? "execution_failed" : "automation_blocked",
                ErrorMessage: $"Không hoàn tất được tác vụ Gemini Web: {ex.Message}", SideEffectSubmitted: submitted);
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
            throw new InvalidOperationException("Gemini không trả dữ liệu ảnh hợp lệ.");
        var fileName = dataUrl.StartsWith("data:image/png;", StringComparison.OrdinalIgnoreCase) ? "generated.png" :
            dataUrl.StartsWith("data:image/jpeg;", StringComparison.OrdinalIgnoreCase) ? "generated.jpg" :
            dataUrl.StartsWith("data:image/webp;", StringComparison.OrdinalIgnoreCase) ? "generated.webp" : null;
        if (fileName is null) throw new InvalidOperationException("Định dạng ảnh Gemini không được hỗ trợ.");
        var base64 = dataUrl[(comma + 1)..];
        var bytes = Convert.FromBase64String(base64);
        if (bytes.Length < 10_000) throw new InvalidOperationException("Ảnh Gemini quá nhỏ hoặc chưa tải xong.");
        var validMagic = fileName switch
        {
            "generated.png" => bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            "generated.jpg" => bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            _ => bytes.AsSpan().StartsWith(new byte[] { 0x52, 0x49, 0x46, 0x46 }) &&
                bytes.AsSpan(8).StartsWith(new byte[] { 0x57, 0x45, 0x42, 0x50 })
        };
        if (!validMagic) throw new InvalidOperationException("Nội dung ảnh Gemini không khớp định dạng công bố.");
        return (fileName, base64);
    }
}
