using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Browser.Publishing;
using Fraylo.Contracts.Publishing;
using Fraylo.Contracts.Content;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;
using Fraylo.Infrastructure.Storage;

namespace Fraylo.Application.Publishing;

public sealed class TikTokPublishingService(
    DbContextOptions<FrayloDbContext> dbOptions,
    StorageRootRegistry roots,
    IProfileRegistry profiles,
    ITikTokVideoUploader uploader)
{
    private const string AttemptPrefix = "publishing.attempt.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Active = new(StringComparer.Ordinal);
    private sealed record StoredAttempt(PublishAttemptDto Attempt, string ProfileId, string Sha256Hash,
        int OwnerProcessId, long OwnerStartedAtTicks, string? SoundName = null, string? FavoriteSoundKey = null);

    public async Task<PublishingListResponse> ListAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        IReadOnlyList<PublishAttemptDto> attempts;
        try
        {
            await RecoverInterruptedAsync(ct);
            await using var db = new FrayloDbContext(dbOptions);
            var journal = await db.AppSettings.AsNoTracking().Where(s => s.Key.StartsWith(AttemptPrefix))
                .OrderByDescending(s => s.UpdatedAtUtc).Take(50).Select(s => s.Value).ToListAsync(ct);
            attempts = journal.Select(Read).Select(s => s.Attempt).ToArray();
        }
        finally { Gate.Release(); }

        await using var readDb = new FrayloDbContext(dbOptions);
        // Dự án chưa có video nào không thể có video đăng được: bỏ qua sớm để khỏi chạy hàng chục truy vấn cho mỗi dự án nháp.
        var projects = await readDb.Projects.AsNoTracking()
            .Where(p => p.CurrentVideoRevisionId != null || readDb.Scenes.Any(s => s.ProjectId == p.Id && s.VideoAssetId != null))
            .OrderByDescending(p => p.UpdatedAtUtc).ToListAsync(ct);
        var videos = new List<PublishVideoDto>();
        foreach (var project in projects)
        {
            videos.AddRange(await FindReadyVideosAsync(readDb, project, ct));
        }
        return new(videos, attempts);
    }

    public async Task<FavoriteSoundsResponse> GetFavoriteSoundsAsync(FavoriteSoundsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParse(request.ProjectId, out _) || !Guid.TryParse(request.AssetId, out _) || string.IsNullOrWhiteSpace(request.AccountId))
            throw new ArgumentException("Cần chọn video và tài khoản TikTok để tải nhạc yêu thích.");

        await using var db = new FrayloDbContext(dbOptions);
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.ProjectId, ct)
            ?? throw new KeyNotFoundException("Không tìm thấy dự án.");
        if (!(await FindReadyVideosAsync(db, project, ct)).Any(v => v.AssetId == request.AssetId))
            throw new InvalidOperationException("Video không còn sẵn sàng để mở trình chỉnh sửa TikTok.");
        var profile = await profiles.GetProfileAsync(request.AccountId, ct);
        if (profile == null || profile.Kind != BrowserProfileKind.TikTok || profile.Lifecycle != "Active" ||
            profile.SessionStatus != BrowserSessionStatus.Connected || string.IsNullOrWhiteSpace(profile.AccountIdentity))
            throw new InvalidOperationException("Hãy đăng nhập và xác minh tài khoản TikTok đã chọn trong Cài đặt.");
        var asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == request.AssetId, ct);
        var root = await roots.ResolveRootPathAsync(asset.StorageRootId, ct);
        var path = StoragePathGuard.CombineAndVerify(root, asset.RelativePath);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".mp4" or ".mov" or ".webm"))
            throw new InvalidOperationException("Chỉ hỗ trợ video MP4, MOV hoặc WebM.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(asset.Sha256Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Video đã thay đổi so với bản đã tạo; chưa tải lên TikTok.");
        var result = await uploader.GetFavoriteSoundsAsync(new(profile.ProfileId, profile.AccountIdentity, path, ""), ct);
        return new(request.AccountId, result.Sounds, result.IsComplete);
    }

    public async Task<PublishAttemptDto> PublishAsync(PublishVideoRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);
        var attemptId = Guid.Parse(request.ClientRequestId).ToString("N");
        StoredAttempt stored;
        string filePath;
        await Gate.WaitAsync(ct);
        try
        {
            await RecoverInterruptedAsync(ct);
            await using var db = new FrayloDbContext(dbOptions);
            var prior = await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == AttemptPrefix + attemptId, ct);
            if (prior != null)
            {
                var existingStored = Read(prior.Value);
                var existing = existingStored.Attempt;
                if (existing.ProjectId != request.ProjectId || existing.AssetId != request.AssetId ||
                    existing.AccountId != request.AccountId || existing.Caption != request.Caption ||
                    existingStored.SoundName != request.SoundName?.Trim() || existingStored.FavoriteSoundKey != request.FavoriteSoundKey)
                    throw new ArgumentException("Mã yêu cầu đã được dùng với nội dung khác.");
                return existing;
            }
            var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.ProjectId, ct)
                ?? throw new KeyNotFoundException("Không tìm thấy dự án.");
            var readyVideos = await FindReadyVideosAsync(db, project, ct);
            if (!readyVideos.Any(v => v.AssetId == request.AssetId))
                throw new InvalidOperationException("Video cần hoàn chỉnh, chưa stale và dự án không có tác vụ đang chạy.");
            var asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == request.AssetId, ct);
            var account = await db.BrowserProfiles.AsNoTracking()
                .Where(p => p.Id == request.AccountId && p.Kind == "TikTok" && p.Lifecycle == "Active")
                .Select(p => new { Id = p.Id, ProfileId = p.Id, AccountIdentity = p.AccountIdentity ?? "" })
                .SingleOrDefaultAsync(ct);
            if (account == null) throw new KeyNotFoundException("Tài khoản TikTok không còn được liên kết.");
            var profile = await profiles.GetProfileAsync(account.ProfileId, ct);
            if (profile == null || profile.Kind != BrowserProfileKind.TikTok || profile.Lifecycle != "Active")
                throw new InvalidOperationException("Tài khoản không có profile TikTok đang hoạt động.");
            if (profile.SessionStatus != BrowserSessionStatus.Connected)
                throw new InvalidOperationException("Hãy đăng nhập và xác minh TikTok trong Cài đặt trước.");
            var accountIdentity = string.IsNullOrWhiteSpace(account.AccountIdentity) ? profile.AccountIdentity : account.AccountIdentity;
            if (string.IsNullOrWhiteSpace(accountIdentity))
                throw new InvalidOperationException("Tài khoản TikTok chưa có danh tính được xác minh trong Cài đặt.");

            var root = await roots.ResolveRootPathAsync(asset.StorageRootId, ct);
            filePath = StoragePathGuard.CombineAndVerify(root, asset.RelativePath);
            if (Path.GetExtension(filePath).ToLowerInvariant() is not (".mp4" or ".mov" or ".webm"))
                throw new InvalidOperationException("Chỉ hỗ trợ video MP4, MOV hoặc WebM.");
            var now = DateTimeOffset.UtcNow.ToString("O");
            stored = new(new(attemptId, project.Id, asset.Id, account.Id, accountIdentity, request.Caption,
                "Preparing", "Đang kiểm tra phiên, tải video và điền caption…", null, now, now),
                account.ProfileId, asset.Sha256Hash, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, request.SoundName?.Trim(), request.FavoriteSoundKey);
            if (stored.Sha256Hash.Length != 64) throw new InvalidOperationException("Video thiếu SHA-256 hợp lệ.");

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var guard = GuardKey(stored);
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AppSettings (key, value, updated_at_utc) VALUES ({guard}, {attemptId}, {now})
                ON CONFLICT(key) DO NOTHING
                """, ct);
            if (inserted != 1)
                throw new InvalidOperationException("Video này đã được đăng hoặc đang được đăng trên tài khoản đã chọn. Kiểm tra lịch sử trước.");
            db.AppSettings.Add(new() { Key = AttemptPrefix + attemptId, Value = JsonSerializer.Serialize(stored, JsonOptions), UpdatedAtUtc = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            Active.Add(attemptId);
        }
        finally { Gate.Release(); }

        bool submitted = false;
        try
        {
            // Giữ file mở chỉ đọc suốt upload để ngăn sửa/xóa giữa lúc kiểm hash và gửi file trên Windows.
            await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!hash.Equals(stored.Sha256Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Video đã thay đổi so với bản đã tạo; chưa tải lên TikTok.");
            var url = await uploader.PublishAsync(new(stored.ProfileId, stored.Attempt.AccountIdentity,
                filePath, request.Caption, request.SoundName?.Trim(), request.FavoriteSoundKey), async () =>
            {
                ct.ThrowIfCancellationRequested();
                await using var currentDb = new FrayloDbContext(dbOptions);
                var currentProject = await currentDb.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == request.ProjectId, ct);
                var currentVideos = currentProject == null ? [] : await FindReadyVideosAsync(currentDb, currentProject, ct);
                if (!currentVideos.Any(v => v.AssetId == request.AssetId))
                    throw new InvalidOperationException("Đầu ra dự án đã thay đổi trong lúc tải lên; đã dừng trước khi đăng.");
                stored = WithState(stored, "Submitted", "Đã bắt đầu gửi lệnh đăng; đang chờ TikTok xác nhận.");
                submitted = true;
                await SaveAsync(stored, releaseGuard: false);
            }, ct);
            if (!submitted) throw new InvalidOperationException("Adapter không ghi checkpoint trước khi đăng.");
            if (url != null && !TikTokVideoUploader.IsPostUrlForIdentity(url, stored.Attempt.AccountIdentity))
                throw new InvalidOperationException("URL xác nhận bài đăng không khớp tài khoản đã chọn.");
            stored = stored with
            { Attempt = stored.Attempt with { State = "Published", Message = "TikTok đã hiển thị xác nhận đăng video.", ExternalPostUrl = url, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") } };
        }
        catch (Exception ex)
        {
            stored = WithState(stored, "Failed", ex is OperationCanceledException ? "Đã dừng đăng video." : ex.Message);
        }
        await Gate.WaitAsync(CancellationToken.None);
        try
        {
            await SaveAsync(stored, releaseGuard: stored.Attempt.State == "Failed");
            return stored.Attempt;
        }
        finally { Active.Remove(attemptId); Gate.Release(); }
    }

    /// <summary>
    /// Video đăng được của Project: video cuối (revision đang chọn, hoặc clip duy nhất), hoặc với chiến lược nhiều video
    /// là clip của từng cảnh khi mọi cảnh đã có video. Rỗng khi còn job đang chạy hoặc có cảnh stale.
    /// </summary>
    private async Task<IReadOnlyList<PublishVideoDto>> FindReadyVideosAsync(FrayloDbContext db, Project project, CancellationToken ct)
    {
        if (await db.Jobs.AsNoTracking().AnyAsync(j => j.ProjectId == project.Id &&
            (j.State == "Queued" || j.State == "Reserved" || j.State == "Running" || j.State == "AwaitingUser"), ct)) return [];
        var scenes = await db.Scenes.AsNoTracking().Where(s => s.ProjectId == project.Id).OrderBy(s => s.SequenceOrder).ToListAsync(ct);
        var sceneKeys = scenes.Select(s => "scene.workflow." + s.Id).ToList();
        var sceneMetadata = await db.AppSettings.AsNoTracking().Where(s => sceneKeys.Contains(s.Key)).Select(s => s.Value).ToListAsync(ct);
        foreach (var value in sceneMetadata)
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.EnumerateObject().Any(p => p.Name.Equals("isStale", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True)) return [];
        }
        // SceneId chỉ có với clip của từng cảnh; caption của clip đó không được lấy từ cảnh khác.
        var candidates = new List<(string AssetId, string Name, string? SceneId)>();
        if (project.CurrentVideoRevisionId != null)
        {
            var revision = await db.VideoRevisions.AsNoTracking().SingleOrDefaultAsync(v =>
                v.Id == project.CurrentVideoRevisionId && v.ProjectId == project.Id && v.State == "Ready", ct);
            if (revision != null) candidates.Add((revision.AssetId!, project.Name, null));
        }
        else if (scenes.Count == 1)
        {
            // Tương thích output cũ: clip duy nhất là video cuối.
            if (scenes[0].VideoAssetId != null) candidates.Add((scenes[0].VideoAssetId!, project.Name, null));
        }
        else if (scenes.Count > 1 && scenes.All(s => s.VideoAssetId != null) && await IsMultiVideoAsync(db, project.Id, ct))
        {
            candidates.AddRange(scenes.Select(s => (s.VideoAssetId!, $"{project.Name} · Cảnh {s.SequenceOrder}", (string?)s.Id)));
        }
        if (candidates.Count == 0) return [];

        var content = await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == "project.video_content." + project.Id, ct);
        var items = content == null ? [] : JsonSerializer.Deserialize<List<VideoContentItemDto>>(content.Value, JsonOptions) ?? [];
        var videos = new List<PublishVideoDto>();
        foreach (var (assetId, name, sceneId) in candidates)
        {
            var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == assetId &&
                (a.ProjectId == project.Id || project.CurrentVideoRevisionId != null) && a.AssetType == "Video" && a.Status == "Linked", ct);
            if (asset == null) continue;
            // Clip của cảnh chỉ lấy caption của cảnh đó; video cuối đơn lẻ mới dùng caption chung đầu tiên.
            var caption = sceneId != null
                ? items.FirstOrDefault(i => i.SceneId == sceneId || i.VideoAssetId == asset.Id)?.TikTokContent
                : items.FirstOrDefault(i => i.VideoAssetId == asset.Id)?.TikTokContent ?? items.FirstOrDefault()?.TikTokContent;
            videos.Add(new(project.Id, name, asset.Id, Path.GetFileName(asset.RelativePath), asset.ByteSize, caption ?? "", asset.CreatedAtUtc));
        }
        return videos;
    }

    // Cấu hình cũ không có videoStrategy được coi là nhiều video (VIDEO_TWO_FLOW_SPLIT).
    private static async Task<bool> IsMultiVideoAsync(FrayloDbContext db, string projectId, CancellationToken ct)
    {
        var config = await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == "project.workflow.config." + projectId, ct);
        if (config == null) return true;
        using var document = JsonDocument.Parse(config.Value);
        var strategy = document.RootElement.EnumerateObject()
            .FirstOrDefault(p => p.Name.Equals("videoStrategy", StringComparison.OrdinalIgnoreCase));
        return strategy.Value.ValueKind != JsonValueKind.String || strategy.Value.GetString() != VideoStrategies.SingleVideo;
    }

    private async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        await using var db = new FrayloDbContext(dbOptions);
        // Chỉ đọc các lần đăng chưa ở trạng thái cuối (Published/Failed): lịch sử cũ không phải quét lại mỗi lần mở trang.
        var rows = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith(AttemptPrefix) &&
                (s.Value.Contains("\"Preparing\"") || s.Value.Contains("\"Submitted\"") || s.Value.Contains("\"Uncertain\"") ||
                 s.Value.Contains("\"UserConfirmedPublished\"") || s.Value.Contains("\"UserConfirmedNotPublished\"")))
            .Select(s => s.Value).ToListAsync(ct);
        foreach (var json in rows)
        {
            var item = Read(json);
            if (Active.Contains(item.Attempt.AttemptId) || HasOtherLiveOwner(item)) continue;
            if (item.Attempt.State is "Preparing" or "Submitted" or "Uncertain")
                await SaveAsync(WithState(item, "Failed", "Lần đăng trước bị gián đoạn; đã dừng, không tự gửi lại."), true);
            else if (item.Attempt.State is "UserConfirmedPublished" or "UserConfirmedNotPublished")
                await SaveAsync(WithState(item, item.Attempt.State == "UserConfirmedPublished" ? "Published" : "Failed",
                    item.Attempt.Message), item.Attempt.State == "UserConfirmedNotPublished");
        }
    }

    private async Task SaveAsync(StoredAttempt stored, bool releaseGuard)
    {
        await using var db = new FrayloDbContext(dbOptions);
        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.AppSettings.SingleAsync(s => s.Key == AttemptPrefix + stored.Attempt.AttemptId);
        row.Value = JsonSerializer.Serialize(stored, JsonOptions);
        row.UpdatedAtUtc = stored.Attempt.UpdatedAtUtc;
        if (releaseGuard)
        {
            var guard = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == GuardKey(stored) && s.Value == stored.Attempt.AttemptId);
            if (guard != null) db.AppSettings.Remove(guard);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static string GuardKey(StoredAttempt stored) => $"publishing.guard.{stored.ProfileId}.{stored.Sha256Hash.ToLowerInvariant()}";
    private static bool HasOtherLiveOwner(StoredAttempt stored)
    {
        if (stored.OwnerProcessId == Environment.ProcessId || stored.Attempt.State is not ("Preparing" or "Submitted")) return false;
        try
        {
            using var owner = Process.GetProcessById(stored.OwnerProcessId);
            return owner.StartTime.ToUniversalTime().Ticks == stored.OwnerStartedAtTicks && !owner.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
    private static StoredAttempt Read(string json) => JsonSerializer.Deserialize<StoredAttempt>(json, JsonOptions)
        ?? throw new InvalidOperationException("Lịch sử đăng video không hợp lệ; cần kiểm tra dữ liệu trước khi tiếp tục.");
    private static StoredAttempt WithState(StoredAttempt stored, string state, string message) => stored with
    { Attempt = stored.Attempt with { State = state, Message = message, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") } };

    private static void ValidateRequest(PublishVideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParse(request.ClientRequestId, out _) || !Guid.TryParse(request.ProjectId, out _) ||
            !Guid.TryParse(request.AssetId, out _) || string.IsNullOrWhiteSpace(request.AccountId))
            throw new ArgumentException("Mã yêu cầu/dự án/video/tài khoản không hợp lệ.");
        if (string.IsNullOrWhiteSpace(request.Caption) || request.Caption.EnumerateRunes().Count() > 2200)
            throw new ArgumentException("Caption cần từ 1 đến 2.200 ký tự.");
        if (request.FavoriteSoundKey is { } key && (key.Length != 64 || !key.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Mã nhạc yêu thích không hợp lệ.");
        if (!string.IsNullOrWhiteSpace(request.FavoriteSoundKey) && !string.IsNullOrWhiteSpace(request.SoundName))
            throw new ArgumentException("Chỉ chọn nhạc yêu thích hoặc nhập tên sound.");
        if (request.SoundName is { } sound && (sound.Length > 100 || sound.Any(char.IsControl)))
            throw new ArgumentException("Tên sound tối đa 100 ký tự và không chứa ký tự điều khiển.");
    }
}
