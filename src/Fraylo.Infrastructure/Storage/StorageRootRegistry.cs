using System.IO;
using Microsoft.EntityFrameworkCore;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Infrastructure.Storage;

public sealed record StorageRootsSnapshot(
    string DocumentsRootId,
    string DocumentsPath,
    string ImagesRootId,
    string ImagesPath,
    string VideosRootId,
    string VideosPath,
    string AudioRootId,
    string AudioPath
);

/// <summary>
/// Quản lý phân giải và ánh xạ bền vững các thư mục lưu trữ độc lập (Storage Roots)
/// trong cơ sở dữ liệu AppSettings theo docs/plans/SETTINGS_PLAN.md §2–3, §5.
/// </summary>
public sealed class StorageRootRegistry
{
    public const string RootDefaultDocsId = "root-default-docs";
    public const string RootDefaultImagesId = "root-default-images";
    public const string RootDefaultVideosId = "root-default-videos";
    public const string RootInternalAudioId = "root-internal-audio";

    public const string KeyDocumentsRootId = "storage.documentsRootId";
    public const string KeyImagesRootId = "storage.imagesRootId";
    public const string KeyVideosRootId = "storage.videosRootId";
    public const string KeyRootsPrefix = "storage.roots.";

    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly string? _customBaseDir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public StorageRootRegistry(DbContextOptions<FrayloDbContext> dbOptions, string? customBaseDir = null)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _customBaseDir = customBaseDir;
    }

    /// <summary>
    /// Lấy đường dẫn gốc mặc định theo chuẩn Fraylo trong %LOCALAPPDATA%/Fraylo hoặc customBaseDir.
    /// </summary>
    public static string GetDefaultRootPath(string rootId, string? customBaseDir = null)
    {
        var baseDir = customBaseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fraylo");

        return rootId switch
        {
            RootDefaultDocsId => Path.Combine(baseDir, "documents"),
            RootDefaultImagesId => Path.Combine(baseDir, "images"),
            RootDefaultVideosId => Path.Combine(baseDir, "videos"),
            RootInternalAudioId => Path.Combine(baseDir, "audio"),
            _ => throw new ArgumentException($"Root ID mặc định không xác định: '{rootId}'.", nameof(rootId))
        };
    }

    /// <summary>
    /// Khởi tạo các root mặc định nếu hệ thống chưa từng được cấu hình.
    /// </summary>
    public async Task EnsureDefaultRootsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var dbContext = new FrayloDbContext(_dbOptions);
            await EnsureDefaultRootsInternalAsync(dbContext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureDefaultRootsInternalAsync(FrayloDbContext dbContext, CancellationToken cancellationToken)
    {
        var existingDocRootSetting = await dbContext.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == KeyDocumentsRootId, cancellationToken)
            .ConfigureAwait(false);

        if (existingDocRootSetting is not null)
        {
            return; // Đã được cấu hình trước đó, không ghi đè
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var defaultRoots = new[]
        {
            (RootDefaultDocsId, GetDefaultRootPath(RootDefaultDocsId, _customBaseDir)),
            (RootDefaultImagesId, GetDefaultRootPath(RootDefaultImagesId, _customBaseDir)),
            (RootDefaultVideosId, GetDefaultRootPath(RootDefaultVideosId, _customBaseDir)),
            (RootInternalAudioId, GetDefaultRootPath(RootInternalAudioId, _customBaseDir))
        };

        foreach (var (id, path) in defaultRoots)
        {
            var normalized = StoragePathGuard.NormalizePath(path);
            var key = KeyRootsPrefix + id;

            var existing = await dbContext.AppSettings.FindAsync(new object[] { key }, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                dbContext.AppSettings.Add(new AppSetting
                {
                    Key = key,
                    Value = normalized,
                    UpdatedAtUtc = now
                });
            }
        }

        dbContext.AppSettings.Add(new AppSetting
        {
            Key = KeyDocumentsRootId,
            Value = RootDefaultDocsId,
            UpdatedAtUtc = now
        });

        dbContext.AppSettings.Add(new AppSetting
        {
            Key = KeyImagesRootId,
            Value = RootDefaultImagesId,
            UpdatedAtUtc = now
        });

        dbContext.AppSettings.Add(new AppSetting
        {
            Key = KeyVideosRootId,
            Value = RootDefaultVideosId,
            UpdatedAtUtc = now
        });

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Phân giải root ID thành đường dẫn tuyệt đối đã được cấu hình trong AppSettings.
    /// Nếu ID là default root và chưa có trong AppSettings, fallback về đường dẫn mặc định.
    /// </summary>
    public async Task<string> ResolveRootPathAsync(string storageRootId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storageRootId))
        {
            throw new ArgumentException("Mã storageRootId không được để trống.", nameof(storageRootId));
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);
        var key = KeyRootsPrefix + storageRootId.Trim();

        var setting = await dbContext.AppSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (setting is not null && !string.IsNullOrWhiteSpace(setting.Value))
        {
            return StoragePathGuard.NormalizePath(setting.Value);
        }

        // Fallback cho default roots nếu chưa có record
        if (storageRootId is RootDefaultDocsId or RootDefaultImagesId or RootDefaultVideosId or RootInternalAudioId)
        {
            return StoragePathGuard.NormalizePath(GetDefaultRootPath(storageRootId, _customBaseDir));
        }

        throw new KeyNotFoundException($"Không tìm thấy ánh xạ đường dẫn lưu trữ cho Storage Root ID: '{storageRootId}'.");
    }

    /// <summary>
    /// Liệt kê mọi Root đã đăng ký (kể cả root chưa có Asset nào) để đối soát sau crash hoặc reset DB.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetRegisteredRootIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = new FrayloDbContext(_dbOptions);
        var keys = await dbContext.AppSettings
            .AsNoTracking()
            .Where(s => s.Key.StartsWith(KeyRootsPrefix))
            .Select(s => s.Key)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return keys
            .Select(k => k[KeyRootsPrefix.Length..])
            .Concat(new[] { RootDefaultDocsId, RootDefaultImagesId, RootDefaultVideosId, RootInternalAudioId })
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Lấy ảnh chụp trạng thái các Root hiện hành của hệ thống (Documents, Images, Videos, Audio).
    /// </summary>
    public async Task<StorageRootsSnapshot> GetCurrentRootsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultRootsAsync(cancellationToken).ConfigureAwait(false);

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var allSettings = await dbContext.AppSettings
            .AsNoTracking()
            .Where(s => s.Key.StartsWith("storage."))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken)
            .ConfigureAwait(false);

        string docRootId = allSettings.GetValueOrDefault(KeyDocumentsRootId, RootDefaultDocsId);
        string imgRootId = allSettings.GetValueOrDefault(KeyImagesRootId, RootDefaultImagesId);
        string vidRootId = allSettings.GetValueOrDefault(KeyVideosRootId, RootDefaultVideosId);
        string audioRootId = RootInternalAudioId;

        string docPath = allSettings.GetValueOrDefault(KeyRootsPrefix + docRootId) ?? GetDefaultRootPath(RootDefaultDocsId, _customBaseDir);
        string imgPath = allSettings.GetValueOrDefault(KeyRootsPrefix + imgRootId) ?? GetDefaultRootPath(RootDefaultImagesId, _customBaseDir);
        string vidPath = allSettings.GetValueOrDefault(KeyRootsPrefix + vidRootId) ?? GetDefaultRootPath(RootDefaultVideosId, _customBaseDir);
        string audioPath = allSettings.GetValueOrDefault(KeyRootsPrefix + audioRootId) ?? GetDefaultRootPath(RootInternalAudioId, _customBaseDir);

        return new StorageRootsSnapshot(
            DocumentsRootId: docRootId,
            DocumentsPath: StoragePathGuard.NormalizePath(docPath),
            ImagesRootId: imgRootId,
            ImagesPath: StoragePathGuard.NormalizePath(imgPath),
            VideosRootId: vidRootId,
            VideosPath: StoragePathGuard.NormalizePath(vidPath),
            AudioRootId: audioRootId,
            AudioPath: StoragePathGuard.NormalizePath(audioPath)
        );
    }

    /// <summary>
    /// Cập nhật ba đường dẫn lưu trữ. Tái sử dụng rootId nếu path đã từng được cấu hình trước đó;
    /// nếu path hoàn toàn mới, sinh rootId mới và LƯU BỔ SUNG, giữ nguyên tất cả mapping cũ.
    /// </summary>
    public async Task<StorageRootsSnapshot> UpdateRootsAsync(
        string documentsPath,
        string imagesPath,
        string videosPath,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var dbContext = new FrayloDbContext(_dbOptions);
            var snapshot = await UpdateRootsInternalAsync(dbContext, documentsPath, imagesPath, videosPath, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Cập nhật ba đường dẫn lưu trữ trên một DbContext được cung cấp để hỗ trợ transaction atomic cùng settings khác.
    /// </summary>
    public async Task<StorageRootsSnapshot> UpdateRootsInternalAsync(
        FrayloDbContext dbContext,
        string documentsPath,
        string imagesPath,
        string videosPath,
        CancellationToken cancellationToken = default)
    {
        var normDocPath = StoragePathGuard.NormalizePath(documentsPath);
        var normImgPath = StoragePathGuard.NormalizePath(imagesPath);
        var normVidPath = StoragePathGuard.NormalizePath(videosPath);

        await EnsureDefaultRootsInternalAsync(dbContext, cancellationToken).ConfigureAwait(false);

        var rootMappings = await dbContext.AppSettings
            .Where(s => s.Key.StartsWith(KeyRootsPrefix))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in rootMappings)
        {
            var id = item.Key[KeyRootsPrefix.Length..];
            var normalizedVal = StoragePathGuard.NormalizePath(item.Value);
            if (!pathMap.ContainsKey(normalizedVal))
            {
                pathMap[normalizedVal] = id;
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");

        string ResolveOrRegister(string targetPath, string defaultId)
        {
            if (pathMap.TryGetValue(targetPath, out var existingId))
            {
                return existingId;
            }

            // Nếu trùng path mặc định ban đầu
            var defPath = StoragePathGuard.NormalizePath(GetDefaultRootPath(defaultId, _customBaseDir));
            if (string.Equals(targetPath, defPath, StringComparison.OrdinalIgnoreCase))
            {
                return defaultId;
            }

            var newRootId = $"root-{Guid.NewGuid():N}";
            var newKey = KeyRootsPrefix + newRootId;
            dbContext.AppSettings.Add(new AppSetting
            {
                Key = newKey,
                Value = targetPath,
                UpdatedAtUtc = now
            });
            pathMap[targetPath] = newRootId;
            return newRootId;
        }

        var newDocRootId = ResolveOrRegister(normDocPath, RootDefaultDocsId);
        var newImgRootId = ResolveOrRegister(normImgPath, RootDefaultImagesId);
        var newVidRootId = ResolveOrRegister(normVidPath, RootDefaultVideosId);

        await SetSettingValueAsync(dbContext, KeyDocumentsRootId, newDocRootId, now, cancellationToken).ConfigureAwait(false);
        await SetSettingValueAsync(dbContext, KeyImagesRootId, newImgRootId, now, cancellationToken).ConfigureAwait(false);
        await SetSettingValueAsync(dbContext, KeyVideosRootId, newVidRootId, now, cancellationToken).ConfigureAwait(false);

        var audioPath = StoragePathGuard.NormalizePath(GetDefaultRootPath(RootInternalAudioId, _customBaseDir));

        return new StorageRootsSnapshot(
            DocumentsRootId: newDocRootId,
            DocumentsPath: normDocPath,
            ImagesRootId: newImgRootId,
            ImagesPath: normImgPath,
            VideosRootId: newVidRootId,
            VideosPath: normVidPath,
            AudioRootId: RootInternalAudioId,
            AudioPath: audioPath
        );
    }

    private static async Task SetSettingValueAsync(
        FrayloDbContext dbContext,
        string key,
        string value,
        string nowUtc,
        CancellationToken cancellationToken)
    {
        var item = await dbContext.AppSettings
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            dbContext.AppSettings.Add(new AppSetting
            {
                Key = key,
                Value = value,
                UpdatedAtUtc = nowUtc
            });
        }
        else
        {
            item.Value = value;
            item.UpdatedAtUtc = nowUtc;
        }
    }
}
