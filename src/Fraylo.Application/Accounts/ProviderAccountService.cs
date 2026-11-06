using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Fraylo.Contracts.Accounts;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Accounts;

public class ProviderAccountValidationException : Exception
{
    public ProviderAccountValidationException(string message) : base(message) { }
}

public class ProviderAccountConcurrencyException : Exception
{
    public ProviderAccountConcurrencyException(string message) : base(message) { }
}

/// <summary>
/// Dịch vụ quản lý tài khoản Provider AI toàn hệ thống (Cài đặt -> Tài khoản)
/// theo execution-contract-v1.1 và plan 1.2 §3.2.
/// </summary>
public sealed class ProviderAccountService
{
    private readonly DbContextOptions<FrayloDbContext> _dbOptions;
    private readonly IProfileIdentityResolver _identityResolver;
    private readonly Func<string, CancellationToken, Task>? _releaseProfileAsync;

    public ProviderAccountService(
        DbContextOptions<FrayloDbContext> dbOptions,
        IProfileIdentityResolver identityResolver,
        Func<string, CancellationToken, Task>? releaseProfileAsync = null)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _identityResolver = identityResolver ?? throw new ArgumentNullException(nameof(identityResolver));
        _releaseProfileAsync = releaseProfileAsync;
    }

    public async Task<IReadOnlyList<ProviderAccountDto>> ListAccountsAsync(
        ProviderAccountListRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = new FrayloDbContext(_dbOptions);

        var query = dbContext.ProviderAccounts
            .Include(a => a.Profile)
            .AsNoTracking();

        if (request != null)
        {
            if (!string.IsNullOrWhiteSpace(request.ProviderKey))
            {
                var pKey = request.ProviderKey.Trim().ToLowerInvariant();
                if (pKey == "museai-video") pKey = "museai";
                query = query.Where(a => a.ProviderKey == pKey);
            }

            if (request.Enabled.HasValue)
            {
                query = query.Where(a => a.Enabled == request.Enabled.Value);
            }

            if (!string.IsNullOrWhiteSpace(request.Lifecycle))
            {
                query = query.Where(a => a.Lifecycle == request.Lifecycle);
            }
        }

        var accounts = await query
            .OrderBy(a => a.ProviderKey)
            .ThenBy(a => a.CreatedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Lấy số slot đang bận cho từng account
        var activeOccupancies = await dbContext.ActiveSlotOccupancies
            .AsNoTracking()
            .GroupBy(o => o.ProviderAccountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.AccountId, g => g.Count, cancellationToken).ConfigureAwait(false);

        return accounts.Select(a =>
        {
            int activeCount = activeOccupancies.GetValueOrDefault(a.Id, 0);
            return new ProviderAccountDto(
                Id: a.Id,
                ProviderKey: a.ProviderKey,
                ProfileId: a.ProfileId,
                DisplayName: a.DisplayName,
                VerifiedIdentity: a.VerifiedIdentity,
                IdentityVerifiedAtUtc: a.IdentityVerifiedAtUtc,
                Enabled: a.Enabled,
                Lifecycle: a.Lifecycle,
                MaxConcurrentJobs: a.MaxConcurrentJobs,
                MaxTabs: a.MaxTabs,
                PerTaskLimitsJson: a.PerTaskLimitsJson,
                Revision: a.Revision,
                CreatedAtUtc: a.CreatedAtUtc,
                UpdatedAtUtc: a.UpdatedAtUtc,
                ActiveJobCount: activeCount,
                SessionStatus: a.Profile?.SessionStatus ?? (a.VerifiedIdentity != null ? "Connected" : "Unverified")
            );
        }).ToList();
    }

    public async Task<ProviderAccountDto> CreateAccountAsync(
        CreateProviderAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ProviderKey))
        {
            throw new ProviderAccountValidationException("Trường 'providerKey' không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ProviderAccountValidationException("Tên hiển thị 'displayName' không được để trống.");
        }

        var providerKey = request.ProviderKey.Trim().ToLowerInvariant();
        if (providerKey == "museai-video")
        {
            providerKey = "museai";
        }
        if (providerKey != "chatgpt-web" && providerKey != "gemini-web" && providerKey != "museai" && providerKey != "tiktok" && providerKey != "facebook")
        {
            throw new ProviderAccountValidationException($"Nhà cung cấp '{request.ProviderKey}' không được hỗ trợ. Chỉ chấp nhận chatgpt-web, gemini-web, museai, tiktok hoặc facebook.");
        }

        // Nguồn cấu hình N duy nhất là MaxTabs (Số tác vụ chạy đồng thời).
        // Đồng bộ MaxConcurrentJobs = MaxTabs để duy trì tính tương thích với hệ thống cũ.
        int n = request.MaxTabs ?? request.MaxConcurrentJobs ?? 3;
        if (n <= 0) n = 1;
        int maxTabs = n;
        int maxConcurrentJobs = n;

        string nowIso = DateTime.UtcNow.ToString("o");
        string accountId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        string profileId = !string.IsNullOrWhiteSpace(request.ProfileId)
            ? request.ProfileId.Trim()
            : $"profile_{providerKey.Replace("-", "_")}_{Guid.NewGuid():N}";

        await using var dbContext = new FrayloDbContext(_dbOptions);

        // Đảm bảo Profile được tạo trong BrowserProfiles nếu chưa có
        var existingProfile = await dbContext.BrowserProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken).ConfigureAwait(false);

        if (existingProfile == null)
        {
            var profileKind = providerKey switch
            {
                "museai" => "MuseAI",
                "tiktok" => "TikTok",
                "facebook" => "Facebook",
                _ => "SharedAI"
            };
            var newProfile = new BrowserProfile
            {
                Id = profileId,
                Kind = profileKind,
                AccountIdentity = null,
                VisibilityState = "Hidden",
                SessionStatus = "Unverified",
                OperationalState = "Stopped",
                ProviderKey = providerKey,
                DisplayName = $"{request.DisplayName.Trim()} Profile",
                Lifecycle = "Active",
                Revision = 1,
                CreatedAtUtc = nowIso,
                UpdatedAtUtc = nowIso
            };
            dbContext.BrowserProfiles.Add(newProfile);
        }

        var newAccount = new ProviderAccount
        {
            Id = accountId,
            ProviderKey = providerKey,
            ProfileId = profileId,
            DisplayName = request.DisplayName.Trim(),
            VerifiedIdentity = null,
            IdentityVerifiedAtUtc = null,
            Enabled = false, // Ban đầu tắt cho đến khi hoàn tất login và probe
            Lifecycle = "Active",
            MaxConcurrentJobs = maxConcurrentJobs,
            MaxTabs = maxTabs,
            PerTaskLimitsJson = request.PerTaskLimitsJson,
            Revision = 1,
            CreatedAtUtc = nowIso,
            UpdatedAtUtc = nowIso
        };

        dbContext.ProviderAccounts.Add(newAccount);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ProviderAccountDto(
            Id: newAccount.Id,
            ProviderKey: newAccount.ProviderKey,
            ProfileId: newAccount.ProfileId,
            DisplayName: newAccount.DisplayName,
            VerifiedIdentity: null,
            IdentityVerifiedAtUtc: null,
            Enabled: false,
            Lifecycle: "Active",
            MaxConcurrentJobs: newAccount.MaxConcurrentJobs,
            MaxTabs: newAccount.MaxTabs,
            PerTaskLimitsJson: newAccount.PerTaskLimitsJson,
            Revision: 1,
            CreatedAtUtc: nowIso,
            UpdatedAtUtc: nowIso,
            ActiveJobCount: 0,
            SessionStatus: "Unverified"
        );
    }

    public async Task<ProviderAccountDto> UpdateAccountAsync(
        UpdateProviderAccountRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var account = await dbContext.ProviderAccounts
            .Include(a => a.Profile)
            .FirstOrDefaultAsync(a => a.Id == request.Id, cancellationToken).ConfigureAwait(false);

        if (account == null)
        {
            throw new ProviderAccountValidationException($"Không tìm thấy tài khoản với mã '{request.Id}'.");
        }

        if (account.Revision != request.ExpectedRevision)
        {
            throw new ProviderAccountConcurrencyException($"Xung đột sửa đổi: Phiên bản tài khoản ({account.Revision}) không khớp với phiên bản dự kiến ({request.ExpectedRevision}).");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ProviderAccountValidationException("Tên hiển thị không được để trống.");
        }

        if (request.Enabled && string.IsNullOrWhiteSpace(account.VerifiedIdentity))
        {
            throw new ProviderAccountValidationException("Không thể kích hoạt tài khoản khi chưa xác minh danh tính đăng nhập thành công qua session probe.");
        }

        // Nguồn cấu hình N duy nhất là MaxTabs (Số tác vụ chạy đồng thời).
        // Đồng bộ MaxConcurrentJobs = MaxTabs để không bị Max Jobs cũ chặn ngầm.
        int n = request.MaxTabs > 0 ? request.MaxTabs : (request.MaxConcurrentJobs > 0 ? request.MaxConcurrentJobs : 1);
        int maxTabs = n;
        int maxConcurrentJobs = n;

        string nowIso = DateTime.UtcNow.ToString("o");
        account.DisplayName = request.DisplayName.Trim();
        account.Enabled = request.Enabled;
        account.MaxConcurrentJobs = maxConcurrentJobs;
        account.MaxTabs = maxTabs;
        account.PerTaskLimitsJson = request.PerTaskLimitsJson;
        account.Revision += 1;
        account.UpdatedAtUtc = nowIso;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        int activeCount = await dbContext.ActiveSlotOccupancies
            .CountAsync(o => o.ProviderAccountId == account.Id, cancellationToken).ConfigureAwait(false);

        return new ProviderAccountDto(
            Id: account.Id,
            ProviderKey: account.ProviderKey,
            ProfileId: account.ProfileId,
            DisplayName: account.DisplayName,
            VerifiedIdentity: account.VerifiedIdentity,
            IdentityVerifiedAtUtc: account.IdentityVerifiedAtUtc,
            Enabled: account.Enabled,
            Lifecycle: account.Lifecycle,
            MaxConcurrentJobs: account.MaxConcurrentJobs,
            MaxTabs: account.MaxTabs,
            PerTaskLimitsJson: account.PerTaskLimitsJson,
            Revision: account.Revision,
            CreatedAtUtc: account.CreatedAtUtc,
            UpdatedAtUtc: account.UpdatedAtUtc,
            ActiveJobCount: activeCount,
            SessionStatus: account.Profile?.SessionStatus ?? (account.VerifiedIdentity != null ? "Connected" : "Unverified")
        );
    }

    public async Task<ProbeProviderAccountSessionResponseData> ProbeAccountSessionAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ProviderAccountValidationException("Mã tài khoản 'accountId' không được để trống.");
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var account = await dbContext.ProviderAccounts
            .Include(a => a.Profile)
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken).ConfigureAwait(false);

        if (account == null)
        {
            throw new ProviderAccountValidationException($"Không tìm thấy tài khoản với mã '{accountId}'.");
        }

        var resolution = await _identityResolver.ResolveProfileIdentityAsync(
            account.ProfileId,
            account.ProviderKey,
            cancellationToken).ConfigureAwait(false);

        string sessionStatus = resolution.SessionStatus ?? "Unverified";
        string? verifiedIdentity = resolution.AccountIdentity;
        bool isVerified = resolution.IsSuccess && !string.IsNullOrWhiteSpace(verifiedIdentity);

        string nowIso = DateTime.UtcNow.ToString("o");
        if (isVerified)
        {
            account.VerifiedIdentity = verifiedIdentity;
            account.IdentityVerifiedAtUtc = nowIso;
            account.Revision += 1;
            account.UpdatedAtUtc = nowIso;

            if (account.Profile != null)
            {
                account.Profile.SessionStatus = sessionStatus;
                account.Profile.AccountIdentity = verifiedIdentity;
                account.Profile.UpdatedAtUtc = nowIso;
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (account.Profile != null && account.Profile.SessionStatus != sessionStatus)
        {
            account.Profile.SessionStatus = sessionStatus;
            account.Profile.UpdatedAtUtc = nowIso;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        int activeCount = await dbContext.ActiveSlotOccupancies
            .CountAsync(o => o.ProviderAccountId == account.Id, cancellationToken).ConfigureAwait(false);

        var dto = new ProviderAccountDto(
            Id: account.Id,
            ProviderKey: account.ProviderKey,
            ProfileId: account.ProfileId,
            DisplayName: account.DisplayName,
            VerifiedIdentity: account.VerifiedIdentity,
            IdentityVerifiedAtUtc: account.IdentityVerifiedAtUtc,
            Enabled: account.Enabled,
            Lifecycle: account.Lifecycle,
            MaxConcurrentJobs: account.MaxConcurrentJobs,
            MaxTabs: account.MaxTabs,
            PerTaskLimitsJson: account.PerTaskLimitsJson,
            Revision: account.Revision,
            CreatedAtUtc: account.CreatedAtUtc,
            UpdatedAtUtc: account.UpdatedAtUtc,
            ActiveJobCount: activeCount,
            SessionStatus: sessionStatus
        );

        return new ProbeProviderAccountSessionResponseData(
            Account: dto,
            IsVerified: isVerified,
            SessionStatus: sessionStatus,
            AccountIdentity: verifiedIdentity,
            ErrorMessage: resolution.ErrorMessage
        );
    }

    public async Task<ProviderAccountDto> EnableAccountAsync(
        string accountId,
        bool enabled,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ProviderAccountValidationException("Mã tài khoản 'accountId' không được để trống.");
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var account = await dbContext.ProviderAccounts
            .Include(a => a.Profile)
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken).ConfigureAwait(false);

        if (account == null)
        {
            throw new ProviderAccountValidationException($"Không tìm thấy tài khoản với mã '{accountId}'.");
        }

        if (expectedRevision.HasValue && account.Revision != expectedRevision.Value)
        {
            throw new ProviderAccountConcurrencyException($"Xung đột sửa đổi: Phiên bản tài khoản ({account.Revision}) không khớp với phiên bản dự kiến ({expectedRevision.Value}).");
        }

        if (enabled && string.IsNullOrWhiteSpace(account.VerifiedIdentity))
        {
            throw new ProviderAccountValidationException("Không thể kích hoạt tài khoản khi chưa xác minh danh tính đăng nhập thành công qua session probe. Vui lòng bấm 'Kiểm tra phiên' hoặc 'Đăng nhập trình duyệt' trước khi kích hoạt.");
        }

        if (enabled && account.Profile != null && string.Equals(account.Profile.SessionStatus, "SessionExpired", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderAccountValidationException("Phiên đăng nhập của tài khoản đã hết hạn. Vui lòng đăng nhập lại qua trình duyệt và kiểm tra phiên trước khi kích hoạt.");
        }

        string nowIso = DateTime.UtcNow.ToString("o");
        account.Enabled = enabled;
        account.Revision += 1;
        account.UpdatedAtUtc = nowIso;

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        int activeCount = await dbContext.ActiveSlotOccupancies
            .CountAsync(o => o.ProviderAccountId == account.Id, cancellationToken).ConfigureAwait(false);

        return new ProviderAccountDto(
            Id: account.Id,
            ProviderKey: account.ProviderKey,
            ProfileId: account.ProfileId,
            DisplayName: account.DisplayName,
            VerifiedIdentity: account.VerifiedIdentity,
            IdentityVerifiedAtUtc: account.IdentityVerifiedAtUtc,
            Enabled: account.Enabled,
            Lifecycle: account.Lifecycle,
            MaxConcurrentJobs: account.MaxConcurrentJobs,
            MaxTabs: account.MaxTabs,
            PerTaskLimitsJson: account.PerTaskLimitsJson,
            Revision: account.Revision,
            CreatedAtUtc: account.CreatedAtUtc,
            UpdatedAtUtc: account.UpdatedAtUtc,
            ActiveJobCount: activeCount,
            SessionStatus: account.Profile?.SessionStatus ?? (account.VerifiedIdentity != null ? "Connected" : "Unverified")
        );
    }

    public async Task<ProviderAccountDto> DeleteAccountAsync(
        string accountId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ProviderAccountValidationException("Mã tài khoản 'accountId' không được để trống.");
        }

        await using var dbContext = new FrayloDbContext(_dbOptions);

        var account = await dbContext.ProviderAccounts
            .Include(a => a.Profile)
            .FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken).ConfigureAwait(false);

        if (account == null)
        {
            throw new ProviderAccountValidationException($"Không tìm thấy tài khoản với mã '{accountId}'.");
        }

        if (expectedRevision.HasValue && account.Revision != expectedRevision.Value)
        {
            throw new ProviderAccountConcurrencyException($"Xung đột sửa đổi: Phiên bản tài khoản ({account.Revision}) không khớp với phiên bản dự kiến ({expectedRevision.Value}).");
        }

        int activeCount = await dbContext.ActiveSlotOccupancies
            .CountAsync(o => o.ProviderAccountId == account.Id, cancellationToken).ConfigureAwait(false);
        if (activeCount > 0)
        {
            throw new ProviderAccountValidationException($"Tài khoản \"{account.DisplayName}\" đang có {activeCount} tác vụ chạy. Hãy đợi tác vụ kết thúc rồi xoá lại.");
        }

        // Profile có thể được tài khoản khác dùng chung (dữ liệu cũ): chỉ xoá thư mục trình duyệt khi không còn ai dùng
        bool profileShared = await dbContext.ProviderAccounts
            .AnyAsync(a => a.ProfileId == account.ProfileId && a.Id != account.Id && a.Lifecycle == "Active", cancellationToken)
            .ConfigureAwait(false);

        // Đóng trình duyệt và xoá dữ liệu phiên trước: nếu thất bại thì tài khoản giữ nguyên để người dùng thử lại
        if (!profileShared && _releaseProfileAsync != null)
        {
            await _releaseProfileAsync(account.ProfileId, cancellationToken).ConfigureAwait(false);
        }

        string nowIso = DateTime.UtcNow.ToString("o");
        account.Lifecycle = "Deleted";
        account.Enabled = false;
        account.VerifiedIdentity = null;
        account.IdentityVerifiedAtUtc = null;
        account.Revision += 1;
        account.UpdatedAtUtc = nowIso;

        if (!profileShared && account.Profile != null)
        {
            account.Profile.Lifecycle = "Deleted";
            account.Profile.SessionStatus = "Unverified";
            account.Profile.AccountIdentity = null;
            account.Profile.UpdatedAtUtc = nowIso;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ProviderAccountDto(
            Id: account.Id,
            ProviderKey: account.ProviderKey,
            ProfileId: account.ProfileId,
            DisplayName: account.DisplayName,
            VerifiedIdentity: account.VerifiedIdentity,
            IdentityVerifiedAtUtc: account.IdentityVerifiedAtUtc,
            Enabled: account.Enabled,
            Lifecycle: account.Lifecycle,
            MaxConcurrentJobs: account.MaxConcurrentJobs,
            MaxTabs: account.MaxTabs,
            PerTaskLimitsJson: account.PerTaskLimitsJson,
            Revision: account.Revision,
            CreatedAtUtc: account.CreatedAtUtc,
            UpdatedAtUtc: account.UpdatedAtUtc,
            ActiveJobCount: 0,
            SessionStatus: "Unverified"
        );
    }
}
