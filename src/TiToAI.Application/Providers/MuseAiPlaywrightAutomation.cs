using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using TiToAI.Application.Assets;
using TiToAI.Application.Jobs;
using TiToAI.Browser.Abstractions;
using TiToAI.Contracts.Jobs;
using TiToAI.Contracts.Providers;
using TiToAI.Browser.Models;
using TiToAI.Infrastructure.Persistence;
using TiToAI.Infrastructure.Persistence.Entities;
using TiToAI.Infrastructure.Storage;

namespace TiToAI.Application.Providers;

/// <summary>
/// Runs one MuseAI render in a new side chat of the job's profile: attaches ordered images, sends the prompt,
/// waits for the video, downloads it and stores it as a Video Asset. Selectors come from a read-only DOM
/// inspection of muse.ai on 2026-10-28 and are NOT verified by a live render.
/// </summary>
public sealed class MuseAiPlaywrightAutomation
{
    private const string NewChatUrl = "https://muse.ai/thread/new";
    private const string AttachSnapshotScript = "() => JSON.stringify({ blobImages: document.querySelectorAll('img[src^=\"blob:\"]').length, fileInputs: [...document.querySelectorAll(\"input[type='file']\")].map(i => ({ multiple: i.multiple, files: i.files.length })) })";
    private static readonly Regex ThreadUrl = new(@"/thread/[0-9a-f-]{36}$", RegexOptions.IgnoreCase);
    private static readonly float RenderTimeoutMs = (float)TimeSpan.FromMinutes(30).TotalMilliseconds;
    private static readonly Regex OpenVideoName = new("^Open animated video", RegexOptions.IgnoreCase);
    private const string VideoReadyScript = "() => !document.querySelector('[aria-label=\"Assistant is typing\"]') && !!document.querySelector(\"[data-message-role='assistant'] video\")";
    private const string ThreadUrlKeyPrefix = "job.museai.thread.";

    private readonly IBrowserProcessController _browser;
    private readonly IBrowserGatekeeper _gatekeeper;
    private readonly IProfileRegistry _profiles;
    private readonly DbContextOptions<TiToAIDbContext> _dbOptions;
    private readonly StorageRootRegistry _roots;
    private readonly AssetService _assets;
    private readonly JobActivityLog _activity;

    public MuseAiPlaywrightAutomation(IBrowserProcessController browser, IBrowserGatekeeper gatekeeper, IProfileRegistry profiles,
        DbContextOptions<TiToAIDbContext> dbOptions, StorageRootRegistry roots, AssetService assets)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _gatekeeper = gatekeeper ?? throw new ArgumentNullException(nameof(gatekeeper));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _activity = new JobActivityLog(dbOptions);
    }

    public async Task<JobExecutionResult> ExecuteRenderAsync(
        JobExecutionContext job,
        Func<Task> beforeSubmit,
        Func<int, string, Task>? progress,
        CancellationToken ct)
    {
        await _activity.WriteAsync(job.JobId, "validate", "Bắt đầu kiểm tra prompt, ảnh và phiên MuseAI.", ct).ConfigureAwait(false);
        // Đã có side chat nghĩa là prompt đã gửi: chỉ chờ lại video, tuyệt đối không gửi lại.
        await using var db = new TiToAIDbContext(_dbOptions);
        var savedThreadUrl = await ReadSavedThreadUrlAsync(db, job.JobId, ct).ConfigureAwait(false);
        if (savedThreadUrl is not null)
            return await ResumeRenderAsync(job, savedThreadUrl, progress, ct).ConfigureAwait(false);

        var prompt = ReadPrompt(job.PromptSnapshotText);
        var (durationSeconds, imageIds) = ReadRenderInput(job.ConfigSnapshotJson);
        if (string.IsNullOrWhiteSpace(job.ProfileId) || string.IsNullOrWhiteSpace(prompt) || durationSeconds <= 0 || imageIds.Count == 0)
            return new(false, ReasonCode: "invalid_input", ErrorMessage: "Thiếu profile, prompt, thời lượng hoặc ảnh tham chiếu của job.");

        var imagePaths = await ResolveImagePathsAsync(job, imageIds, ct).ConfigureAwait(false);
        var fullPrompt = $"{prompt}\n\nThời lượng yêu cầu: {durationSeconds} giây.";

        var profile = await _profiles.GetProfileAsync(job.ProfileId, ct).ConfigureAwait(false);
        if (profile is null || !string.Equals(profile.ProviderKey, job.ProviderKey, StringComparison.OrdinalIgnoreCase) ||
            profile.SessionStatus != BrowserSessionStatus.Connected || string.IsNullOrWhiteSpace(profile.AccountIdentity))
            return new(false, ReasonCode: "login_required", ErrorMessage: "Phiên MuseAI của tài khoản này chưa được xác minh. Hãy đăng nhập và kiểm tra phiên.");

        var handle = _browser.TryGetContext(job.ProfileId);
        if (handle is null)
            return new(false, ReasonCode: "automation_blocked", ErrorMessage: "Profile MuseAI chưa được mở cho tác vụ render.");

        IPage? page = null;
        var submitted = false;
        var threadSaved = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            page = await handle.Context.NewPageAsync().ConfigureAwait(false);
            await page.GotoAsync(NewChatUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).WaitAsync(ct).ConfigureAwait(false);

            var composer = page.Locator("textarea[placeholder='Message']");
            await composer.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 }).WaitAsync(ct).ConfigureAwait(false);
            await page.Locator("input[type='file']").First.SetInputFilesAsync(imagePaths.ToArray()).WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await page.WaitForFunctionAsync("n => document.querySelectorAll('img[src^=\"blob:\"]').length >= n",
                    imagePaths.Count, new() { Timeout = 15000 }).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Selectors come from an unverified DOM read; the snapshot shows what the page actually has.
                var snapshot = await page.EvaluateAsync<string>(AttachSnapshotScript).ConfigureAwait(false);
                // The page may render previews without blob: URLs; the file input still holding every file means the picker accepted them.
                var inputHolds = await page.EvaluateAsync<bool>(
                    "n => [...document.querySelectorAll(\"input[type='file']\")].some(i => i.files.length >= n)", imagePaths.Count).ConfigureAwait(false);
                if (!inputHolds)
                {
                    await _activity.WriteAsync(job.JobId, "media", $"Không thấy đủ ảnh đính kèm: {snapshot}", ct).ConfigureAwait(false);
                    throw new InvalidOperationException($"Không xác nhận được {imagePaths.Count} ảnh trong composer MuseAI: {snapshot}", ex);
                }
                await _activity.WriteAsync(job.JobId, "media", $"Không thấy ảnh xem trước dạng blob nhưng input đã nhận đủ file, tiếp tục: {snapshot}", ct).ConfigureAwait(false);
            }
            await _activity.WriteAsync(job.JobId, "media", $"Đã đính kèm {imagePaths.Count} ảnh theo thứ tự.", ct).ConfigureAwait(false);

            await composer.FillAsync(fullPrompt).WaitAsync(ct).ConfigureAwait(false);
            await page.WaitForFunctionAsync("() => { const b = document.querySelector(\"button[aria-label='Send']\"); return !!b && !b.disabled; }",
                null, new() { Timeout = 60000 }).WaitAsync(ct).ConfigureAwait(false);

            await beforeSubmit().ConfigureAwait(false);
            await _activity.WriteAsync(job.JobId, "submit", "Đã ghi checkpoint trước khi gửi.", ct).ConfigureAwait(false);
            submitted = true; // Once the checkpoint is durable, a failed click must be reconciled, never blindly resent.
            await page.Locator("button[aria-label='Send']").ClickAsync().WaitAsync(ct).ConfigureAwait(false);
            await page.WaitForURLAsync(ThreadUrl, new() { Timeout = 30000 }).WaitAsync(ct).ConfigureAwait(false);
            await SaveThreadUrlAsync(job.JobId, page.Url, ct).ConfigureAwait(false);
            threadSaved = true;
            await _activity.WriteAsync(job.JobId, "submit", $"Đã gửi trong side chat {page.Url}.", ct).ConfigureAwait(false);
            return await FinishRenderAsync(page, job.JobId, job.ProjectId, job.StorageRootId, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PlaywrightException ex)
        {
            await RecordFailureAsync(job.JobId, submitted ? "Lỗi sau khi gửi." : "Lỗi thao tác browser trước khi gửi.").ConfigureAwait(false);
            return new(false, ReasonCode: PostSubmitFailureReason(submitted, threadSaved, "automation_blocked"),
                ErrorMessage: $"Không thể hoàn tất thao tác MuseAI: {ex.Message}", SideEffectSubmitted: submitted);
        }
        catch (TimeoutException ex)
        {
            // Trước khi gửi: an toàn để thử lại từ đầu. Chưa lưu side chat sau khi gửi: chưa rõ đã gửi hay chưa, phải đối soát.
            await RecordFailureAsync(job.JobId, submitted ? "Hết thời gian sau khi gửi." : "Hết thời gian trước khi gửi; tác vụ thất bại, có thể thử lại.").ConfigureAwait(false);
            return new(false, ReasonCode: PostSubmitFailureReason(submitted, threadSaved, JobReasonCode.RenderTimeout),
                ErrorMessage: $"Quá thời gian chờ MuseAI: {ex.Message}", SideEffectSubmitted: submitted);
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(job.JobId, submitted ? "Tác vụ lỗi sau khi gửi." : "Tác vụ lỗi trước khi gửi.").ConfigureAwait(false);
            return new(false, ReasonCode: PostSubmitFailureReason(submitted, threadSaved, "automation_blocked"),
                ErrorMessage: $"Không hoàn tất được render MuseAI: {ex.Message}", SideEffectSubmitted: submitted);
        }
        finally
        {
            if (page is not null && !page.IsClosed)
                try { await page.CloseAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Reopens the job's saved side chat and downloads its video without sending anything. Does not change the job
    /// state; the caller reconciles it once the Asset exists.
    /// </summary>
    public async Task<JobExecutionResult> RecoverRenderAsync(string jobId, string profileId, string projectId, string storageRootId, CancellationToken ct)
    {
        var threadUrl = await ReadThreadUrlAsync(jobId, ct).ConfigureAwait(false);
        if (threadUrl is null)
            return new(false, ReasonCode: "invalid_input", ErrorMessage: "Không có URL side chat MuseAI hợp lệ đã lưu cho tác vụ này.");

        try
        {
            ct.ThrowIfCancellationRequested();
            // Sau khi mở lại app profile có thể chưa chạy: gatekeeper mở đúng profile và cấp tab lease (cùng đường với scheduler) trước khi tải.
            using var lease = await _gatekeeper.TryAcquireTabLeaseAsync(profileId, JobTaskType.VideoRender, jobId, ct).ConfigureAwait(false);
            if (lease is null)
                return new(false, ReasonCode: "automation_blocked", ErrorMessage: "Profile MuseAI đang bận tác vụ khác. Hãy thử khôi phục lại sau.");

            var handle = _browser.TryGetContext(profileId)
                ?? throw new InvalidOperationException("Profile MuseAI không có browser đang hoạt động sau khi cấp lease.");

            IPage? page = null;
            try
            {
                page = await handle.Context.NewPageAsync().ConfigureAwait(false);
                await page.GotoAsync(threadUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).WaitAsync(ct).ConfigureAwait(false);
                await WaitForVideoAsync(page, ct).ConfigureAwait(false);
                var assetId = await DownloadVideoAssetAsync(page, jobId, projectId, storageRootId, ct).ConfigureAwait(false);
                return new(true, ResultReference: assetId, SideEffectSubmitted: true);
            }
            finally
            {
                if (page is not null && !page.IsClosed)
                    try { await page.CloseAsync().ConfigureAwait(false); } catch { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(jobId, "Khôi phục video MuseAI thất bại.").ConfigureAwait(false);
            return new(false, ReasonCode: "automation_blocked", ErrorMessage: $"Không khôi phục được video MuseAI: {ex.Message}");
        }
    }

    /// <summary>True when the URL is a MuseAI side chat (<c>https://muse.ai/thread/&lt;uuid&gt;</c>).</summary>
    public static bool IsSideChatUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("muse.ai", StringComparison.OrdinalIgnoreCase) && ThreadUrl.IsMatch(uri.AbsolutePath);

    private async Task WaitForVideoAsync(IPage page, CancellationToken ct)
    {
        await page.WaitForFunctionAsync(VideoReadyScript, null, new() { Timeout = RenderTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Opens the video preview, downloads it and stores it as the Project's Video Asset.</summary>
    private async Task<string> DownloadVideoAssetAsync(IPage page, string jobId, string projectId, string storageRootId, CancellationToken ct)
    {
        // Match the accessible name, not aria-label: the button's label may come from its text or title.
        await page.GetByRole(AriaRole.Button, new() { NameRegex = OpenVideoName })
            .Last.ClickAsync().WaitAsync(ct).ConfigureAwait(false);
        var download = await page.RunAndWaitForDownloadAsync(
            () => page.Locator("[role=dialog]").GetByRole(AriaRole.Button, new() { Name = "Download" }).ClickAsync(),
            new() { Timeout = 120000 }).WaitAsync(ct).ConfigureAwait(false);

        // Playwright's temp path has no extension; save with .mp4 so the asset service accepts it.
        var downloaded = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mp4");
        try
        {
            await download.SaveAsAsync(downloaded).WaitAsync(ct).ConfigureAwait(false);
            var asset = await _assets.SaveVideoAssetAsync(projectId, downloaded, storageRootId, ct).ConfigureAwait(false);
            await _activity.WriteAsync(jobId, "result", "Đã lưu video MuseAI thành Asset.", ct).ConfigureAwait(false);
            return asset.AssetId;
        }
        finally
        {
            await TryDeleteTempDownloadAsync(jobId, downloaded).ConfigureAwait(false);
        }
    }

    /// <summary>Stores the side chat URL right after send, so a later reconcile can reopen this chat without resending.</summary>
    private async Task SaveThreadUrlAsync(string jobId, string threadUrl, CancellationToken ct)
    {
        await using var db = new TiToAIDbContext(_dbOptions);
        db.AppSettings.Add(new AppSetting
        {
            Key = ThreadUrlKeyPrefix + jobId,
            Value = threadUrl,
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("o")
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> ReadThreadUrlAsync(string jobId, CancellationToken ct)
    {
        await using var db = new TiToAIDbContext(_dbOptions);
        return await ReadSavedThreadUrlAsync(db, jobId, ct).ConfigureAwait(false);
    }

    /// <summary>URL side chat đã lưu của job (chỉ có sau khi prompt đã gửi); null nếu chưa gửi hoặc URL không hợp lệ.</summary>
    public static async Task<string?> ReadSavedThreadUrlAsync(TiToAIDbContext db, string jobId, CancellationToken ct)
    {
        var url = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == ThreadUrlKeyPrefix + jobId)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return IsSideChatUrl(url) ? url : null;
    }

    /// <summary>
    /// Render MuseAI đã gửi prompt (có side chat đã lưu) và chưa có kết quả: có thể tiếp tục chờ video mà không gửi lại.
    /// Dùng chung cho executor và đường retry/reservation để không gửi lại prompt.
    /// </summary>
    public static async Task<bool> CanResumeRenderAsync(TiToAIDbContext db, Job job, CancellationToken ct) =>
        job.TaskType == JobTaskType.VideoRender
        && string.Equals(job.ProviderKey, ProviderKeys.MuseAI, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(job.ResultReference) && string.IsNullOrWhiteSpace(job.ResultText)
        && await ReadSavedThreadUrlAsync(db, job.Id, ct).ConfigureAwait(false) is not null;

    /// <summary>
    /// Lý do khi render lỗi: chưa gửi thì dùng lý do mặc định; đã gửi nhưng chưa lưu side chat thì phải đối soát (chưa rõ
    /// prompt đã vào chat hay chưa); đã lưu side chat thì job Failed và thử lại được từ bước chờ, không gửi lại.
    /// </summary>
    private static string PostSubmitFailureReason(bool submitted, bool threadSaved, string notSubmittedReason)
    {
        if (!submitted) return notSubmittedReason;
        return threadSaved ? JobReasonCode.RenderInterrupted : "reconcile_required";
    }

    /// <summary>Mở lại side chat đã lưu rồi chờ video. Không gửi lại prompt; hết thời gian thì trả render_timeout.</summary>
    private async Task<JobExecutionResult> ResumeRenderAsync(JobExecutionContext job, string threadUrl, Func<int, string, Task>? progress, CancellationToken ct)
    {
        var handle = string.IsNullOrWhiteSpace(job.ProfileId) ? null : _browser.TryGetContext(job.ProfileId);
        if (handle is null)
            return new(false, ReasonCode: "automation_blocked", ErrorMessage: "Profile MuseAI chưa được mở để tiếp tục chờ video.");

        IPage? page = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            await _activity.WriteAsync(job.JobId, "resume", "Đã gửi prompt trước đó: mở lại side chat để chờ video, không gửi lại.", ct).ConfigureAwait(false);
            page = await handle.Context.NewPageAsync().ConfigureAwait(false);
            await page.GotoAsync(threadUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).WaitAsync(ct).ConfigureAwait(false);
            return await FinishRenderAsync(page, job.JobId, job.ProjectId, job.StorageRootId, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            await RecordFailureAsync(job.JobId, "Hết thời gian mở lại side chat; tác vụ thất bại, có thể thử lại từ bước chờ.").ConfigureAwait(false);
            return new(false, ReasonCode: JobReasonCode.RenderTimeout, ErrorMessage: $"Quá thời gian mở lại side chat MuseAI: {ex.Message}", SideEffectSubmitted: true);
        }
        catch (Exception ex)
        {
            // Side chat đã lưu từ trước: lỗi ở đây không làm mất prompt đã gửi, job thất bại và thử lại được từ bước chờ.
            await RecordFailureAsync(job.JobId, "Lỗi khi mở lại side chat sau khi gửi; tác vụ thất bại, có thể thử lại.").ConfigureAwait(false);
            return new(false, ReasonCode: JobReasonCode.RenderInterrupted, ErrorMessage: $"Không mở lại được side chat MuseAI: {ex.Message}", SideEffectSubmitted: true);
        }
        finally
        {
            if (page is not null && !page.IsClosed)
                try { await page.CloseAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>Chờ video trong side chat đang mở rồi tải về thành Asset. Hết thời gian chờ thì trả render_timeout (job Failed, có thể thử lại).</summary>
    private async Task<JobExecutionResult> FinishRenderAsync(IPage page, string jobId, string projectId, string storageRootId, Func<int, string, Task>? progress, CancellationToken ct)
    {
        if (progress is not null) await progress(50, "Đang chờ MuseAI tạo video").ConfigureAwait(false);
        try
        {
            await WaitForVideoAsync(page, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            await RecordFailureAsync(jobId, "Hết thời gian chờ video MuseAI; tác vụ thất bại, có thể thử lại từ bước chờ.").ConfigureAwait(false);
            return new(false, ReasonCode: JobReasonCode.RenderTimeout, ErrorMessage: $"Quá thời gian chờ MuseAI tạo video: {ex.Message}", SideEffectSubmitted: true);
        }
        await _activity.WriteAsync(jobId, "result", "MuseAI đã trả về video.", ct).ConfigureAwait(false);

        var assetId = await DownloadVideoAssetAsync(page, jobId, projectId, storageRootId, ct).ConfigureAwait(false);
        return new(true, ResultReference: assetId, SideEffectSubmitted: true);
    }

    private async Task RecordFailureAsync(string jobId, string message)
    {
        try { await _activity.WriteAsync(jobId, "error", message, CancellationToken.None).ConfigureAwait(false); }
        catch { /* Preserve the original provider error. */ }
    }

    /// <summary>Dọn tệp tạm sau khi video đã thành Asset (best effort). Lỗi xóa chỉ ghi nhật ký, không đổi kết quả render.</summary>
    private async Task TryDeleteTempDownloadAsync(string jobId, string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            try { await _activity.WriteAsync(jobId, "cleanup", $"Không xóa được tệp tạm '{path}': {ex.Message}", CancellationToken.None).ConfigureAwait(false); }
            catch { /* Nhật ký chỉ phục vụ theo dõi. */ }
        }
    }

    private static string ReadPrompt(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return "";
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

    private static (int DurationSeconds, IReadOnlyList<string> ImageIds) ReadRenderInput(string? configSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(configSnapshotJson)) return (0, []);
        using var doc = JsonDocument.Parse(configSnapshotJson);
        if (!doc.RootElement.TryGetProperty("MediaSnapshot", out var media) || media.ValueKind != JsonValueKind.Object)
            return (0, []);
        var duration = media.TryGetProperty("DurationSeconds", out var d) && d.TryGetInt32(out var seconds) ? seconds : 0;
        var ids = media.TryGetProperty("ImageAssetIds", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
                .Where(id => !string.IsNullOrWhiteSpace(id)).ToArray()
            : Array.Empty<string>();
        return (duration, ids);
    }

    /// <summary>Resolves ordered image assets to files; rejects missing, out-of-scope or modified files.</summary>
    private async Task<IReadOnlyList<string>> ResolveImagePathsAsync(JobExecutionContext job, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var paths = new List<string>(ids.Count);
        await using var db = new TiToAIDbContext(_dbOptions);
        foreach (var id in ids)
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
            if (!hash.Equals(asset.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Ảnh tham chiếu '{id}' đã thay đổi sau khi tạo job.");
            paths.Add(path);
        }
        return paths;
    }
}
