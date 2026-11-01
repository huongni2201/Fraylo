using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Fraylo.Contracts.Analytics;
using Fraylo.Application.Analytics.Abstractions;
using Fraylo.Infrastructure.Persistence;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Analytics;

/// <summary>
/// Dịch vụ nghiệp vụ phân tích chỉ số TikTok & Dashboard Affiliate.
/// Triển khai theo nguyên tắc:
/// - Cô lập dữ liệu theo TikTok account.
/// - Không tự biên số liệu giả: dữ liệu khuyết trả về null.
/// - Snapshot bất biến (immutable snapshots) cho mỗi lần thu thập/nhập khẩu.
/// </summary>
public sealed class AnalyticsService : IAnalyticsService
{
    private readonly DbContextOptions<FrayloDbContext>? _dbOptions;
    private readonly Func<FrayloDbContext>? _contextFactory;
    private readonly IAnalyticsDataParser _importExportService;

    public AnalyticsService(DbContextOptions<FrayloDbContext> dbOptions, IAnalyticsDataParser? importExportService = null)
    {
        _dbOptions = dbOptions ?? throw new ArgumentNullException(nameof(dbOptions));
        _importExportService = importExportService ?? new AnalyticsImportExportService();
    }

    public AnalyticsService(Func<FrayloDbContext> contextFactory, IAnalyticsDataParser? importExportService = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _importExportService = importExportService ?? new AnalyticsImportExportService();
    }

    private FrayloDbContext CreateDbContext()
    {
        if (_contextFactory != null)
        {
            return _contextFactory();
        }

        if (_dbOptions != null)
        {
            return new FrayloDbContext(_dbOptions);
        }

        throw new InvalidOperationException("DbContext provider chưa được cấu hình cho AnalyticsService.");
    }

    private static async Task<string> RequireTikTokAccountAsync(
        FrayloDbContext dbContext,
        string accountId,
        CancellationToken cancellationToken)
    {
        var profileId = await dbContext.BrowserProfiles.AsNoTracking()
            .Where(p => p.Id == accountId && p.Kind == "TikTok" && p.Lifecycle == "Active")
            .Select(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return profileId
            ?? throw new KeyNotFoundException($"Không tìm thấy tài khoản TikTok đang liên kết '{accountId}'.");
    }

    public async Task<IReadOnlyList<TikTokAnalyticsAccountDto>> ListTikTokAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = CreateDbContext();
        var accounts = await dbContext.BrowserProfiles
            .AsNoTracking()
            .Where(p => p.Kind == "TikTok" && p.Lifecycle == "Active" && p.AccountIdentity != null)
            .OrderBy(p => p.AccountIdentity)
            .Select(a => new TikTokAnalyticsAccountDto(
                a.Id,
                a.AccountIdentity!,
                a.Id,
                a.SessionStatus))
            .ToListAsync(cancellationToken);

        return accounts;
    }

    /// <inheritdoc />
    public async Task<AnalyticsSummaryDto> GetSummaryAsync(GetAnalyticsSummaryRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            throw new ArgumentException("Cần chọn tài khoản TikTok.", nameof(request.AccountId));
        }

        string normalizedAccountId = request.AccountId.Trim();

        DateTime? fromUtc = null;
        DateTime? toUtc = null;

        if (request.PeriodDays.HasValue && request.PeriodDays.Value > 0)
        {
            fromUtc = DateTime.UtcNow.AddDays(-request.PeriodDays.Value);
        }

        if (request.StartDate.HasValue)
        {
            fromUtc = DateTime.SpecifyKind(request.StartDate.Value, DateTimeKind.Utc);
        }

        if (request.EndDate.HasValue)
        {
            toUtc = DateTime.SpecifyKind(request.EndDate.Value, DateTimeKind.Utc);
        }

        await using var dbContext = CreateDbContext();

        await RequireTikTokAccountAsync(dbContext, normalizedAccountId, cancellationToken);

        var query = dbContext.Set<VideoAnalyticsRecord>()
            .AsNoTracking()
            .Where(r => r.AccountId == normalizedAccountId);

        if (fromUtc.HasValue)
        {
            query = query.Where(r => r.CapturedAtUtc >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(r => r.CapturedAtUtc <= toUtc.Value);
        }

        var records = await query.ToListAsync(cancellationToken);
        var reportQuery = dbContext.Set<TikTokAnalyticsDailyRecord>()
            .AsNoTracking()
            .Where(r => r.AccountId == normalizedAccountId && r.DataType == "overview");
        if (fromUtc.HasValue) reportQuery = reportQuery.Where(r => r.PeriodDateUtc >= fromUtc.Value.Date);
        if (toUtc.HasValue) reportQuery = reportQuery.Where(r => r.PeriodDateUtc < toUtc.Value.Date.AddDays(1));
        var reportRecords = await reportQuery.ToListAsync(cancellationToken);
        var viewersQuery = dbContext.Set<TikTokAnalyticsDailyRecord>()
            .AsNoTracking()
            .Where(r => r.AccountId == normalizedAccountId && r.DataType == "viewers");
        if (fromUtc.HasValue) viewersQuery = viewersQuery.Where(r => r.PeriodDateUtc >= fromUtc.Value.Date);
        if (toUtc.HasValue) viewersQuery = viewersQuery.Where(r => r.PeriodDateUtc < toUtc.Value.Date.AddDays(1));
        var viewersRecords = await viewersQuery.ToListAsync(cancellationToken);

        if (records.Count == 0 && reportRecords.Count == 0 && viewersRecords.Count == 0)
        {
            return new AnalyticsSummaryDto(
                PeriodDays: request.PeriodDays,
                StartDate: fromUtc,
                EndDate: toUtc,
                TotalVideos: 0,
                TotalViews: null,
                TotalLikes: null,
                TotalComments: null,
                TotalShares: null,
                AverageEngagementRate: null,
                TotalGmvAmount: null,
                Currency: null,
                TotalAffiliateClicks: null,
                LastCapturedAtUtc: null,
                Source: null
            );
        }

        // Với mỗi video_key, lấy bản ghi có thời điểm captured mới nhất trong kỳ
        var latestPerVideo = records
            .GroupBy(r => r.VideoKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.CapturedAtUtc).First())
            .ToList();

        var latestReportPerDate = reportRecords
            .GroupBy(r => r.PeriodDateUtc.Date)
            .Select(g => g.OrderByDescending(r => r.CapturedAtUtc).First())
            .ToList();

        int totalVideos = latestPerVideo.Count;

        long? contentViews = latestPerVideo.Any(r => r.Views.HasValue)
            ? latestPerVideo.Sum(r => r.Views.GetValueOrDefault())
            : null;

        long? contentLikes = latestPerVideo.Any(r => r.Likes.HasValue)
            ? latestPerVideo.Sum(r => r.Likes.GetValueOrDefault())
            : null;

        long? contentComments = latestPerVideo.Any(r => r.Comments.HasValue)
            ? latestPerVideo.Sum(r => r.Comments.GetValueOrDefault())
            : null;

        long? contentShares = latestPerVideo.Any(r => r.Shares.HasValue)
            ? latestPerVideo.Sum(r => r.Shares.GetValueOrDefault())
            : null;

        long? totalViews = latestReportPerDate.Any(r => r.VideoViews.HasValue)
            ? latestReportPerDate.Sum(r => r.VideoViews.GetValueOrDefault())
            : contentViews;
        long? totalProfileViews = latestReportPerDate.Any(r => r.ProfileViews.HasValue)
            ? latestReportPerDate.Sum(r => r.ProfileViews.GetValueOrDefault())
            : null;
        long? totalLikes = latestReportPerDate.Any(r => r.Likes.HasValue)
            ? latestReportPerDate.Sum(r => r.Likes.GetValueOrDefault())
            : contentLikes;
        long? totalComments = latestReportPerDate.Any(r => r.Comments.HasValue)
            ? latestReportPerDate.Sum(r => r.Comments.GetValueOrDefault())
            : contentComments;
        long? totalShares = latestReportPerDate.Any(r => r.Shares.HasValue)
            ? latestReportPerDate.Sum(r => r.Shares.GetValueOrDefault())
            : contentShares;

        decimal? totalGmv = latestPerVideo.Any(r => r.GmvAmount.HasValue)
            ? latestPerVideo.Sum(r => r.GmvAmount.GetValueOrDefault())
            : null;

        long? totalClicks = latestPerVideo.Any(r => r.AffiliateClicks.HasValue)
            ? latestPerVideo.Sum(r => r.AffiliateClicks.GetValueOrDefault())
            : null;

        double? engagementRate = null;
        if (totalViews.HasValue && totalViews.Value > 0)
        {
            long engagementInteractions = (totalLikes ?? 0) + (totalComments ?? 0) + (totalShares ?? 0);
            if (totalLikes.HasValue || totalComments.HasValue || totalShares.HasValue)
            {
                engagementRate = Math.Round((double)engagementInteractions / totalViews.Value * 100.0, 2);
            }
        }

        var lastCapturedAtUtc = records.Select(r => (DateTime?)r.CapturedAtUtc)
            .Concat(reportRecords.Select(r => (DateTime?)r.CapturedAtUtc))
            .Concat(viewersRecords.Select(r => (DateTime?)r.CapturedAtUtc))
            .Max();

        var latestSnapshot = await dbContext.Set<AnalyticsSnapshot>()
            .AsNoTracking()
            .Where(s => s.AccountId == normalizedAccountId)
            .OrderByDescending(s => s.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return new AnalyticsSummaryDto(
            PeriodDays: request.PeriodDays,
            StartDate: fromUtc,
            EndDate: toUtc,
            TotalVideos: totalVideos,
            TotalViews: totalViews,
            TotalLikes: totalLikes,
            TotalComments: totalComments,
            TotalShares: totalShares,
            AverageEngagementRate: engagementRate,
            TotalGmvAmount: totalGmv,
            Currency: totalGmv.HasValue ? "VND" : null,
            TotalAffiliateClicks: totalClicks,
            LastCapturedAtUtc: lastCapturedAtUtc,
            Source: latestSnapshot?.Source,
            TotalProfileViews: totalProfileViews
        );
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalyticsTimeseriesPointDto>> GetTimeseriesAsync(GetAnalyticsTimeseriesRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            throw new ArgumentException("Cần chọn tài khoản TikTok.", nameof(request.AccountId));
        }

        var normalizedAccountId = request.AccountId.Trim();
        var metric = request.Metric?.Trim().ToLowerInvariant() ?? "views";

        await using var dbContext = CreateDbContext();
        await RequireTikTokAccountAsync(dbContext, normalizedAccountId, cancellationToken);

        if (string.Equals(request.DataType, "overview", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.DataType, "viewers", StringComparison.OrdinalIgnoreCase))
        {
            var reportType = request.DataType!.Trim().ToLowerInvariant();
            var reportQuery = dbContext.Set<TikTokAnalyticsDailyRecord>()
                .AsNoTracking()
                .Where(r => r.AccountId == normalizedAccountId && r.DataType == reportType);

            if (request.PeriodDays is > 0)
            {
                var sinceUtc = DateTime.UtcNow.Date.AddDays(-request.PeriodDays.Value + 1);
                reportQuery = reportQuery.Where(r => r.PeriodDateUtc >= sinceUtc);
            }

            var reportRecords = await reportQuery.ToListAsync(cancellationToken);
            var latestPerDate = reportRecords
                .GroupBy(r => r.PeriodDateUtc.Date)
                .Select(g => g.OrderByDescending(r => r.CapturedAtUtc).First())
                .OrderBy(r => r.PeriodDateUtc)
                .Select(r => new AnalyticsTimeseriesPointDto(
                    Date: r.PeriodDateUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Metric: metric,
                    Value: metric switch
                    {
                        "video_views" or "views" => r.VideoViews,
                        "profile_views" => r.ProfileViews,
                        "likes" => r.Likes,
                        "comments" => r.Comments,
                        "shares" => r.Shares,
                        "total_viewers" => r.TotalViewers,
                        "new_viewers" => r.NewViewers,
                        "returning_viewers" => r.ReturningViewers,
                        _ => null
                    }))
                .ToList();

            return latestPerDate;
        }

        var query = dbContext.Set<VideoAnalyticsRecord>()
            .AsNoTracking()
            .Where(r => r.AccountId == normalizedAccountId);

        if (request.PeriodDays.HasValue && request.PeriodDays.Value > 0)
        {
            var sinceUtc = DateTime.UtcNow.AddDays(-request.PeriodDays.Value);
            query = query.Where(r => r.CapturedAtUtc >= sinceUtc);
        }

        var records = await query.ToListAsync(cancellationToken);

        if (records.Count == 0)
        {
            return Array.Empty<AnalyticsTimeseriesPointDto>();
        }

        // Kiểm tra xem dữ liệu có phân bố theo nhiều ngày crawl/snapshot khác nhau không
        bool hasMultipleCaptureDays = records.Select(r => r.CapturedAtUtc.Date).Distinct().Count() > 1;

        // Nếu chỉ có 1 ngày captured nhưng các video có ngày đăng (PublishedAtUtc) trải dài, nhóm theo ngày đăng
        var groups = records
            .GroupBy(r =>
            {
                var dt = hasMultipleCaptureDays ? r.CapturedAtUtc : (r.PublishedAtUtc ?? r.CapturedAtUtc);
                return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            })
            .OrderBy(g => g.Key);

        var points = new List<AnalyticsTimeseriesPointDto>();

        foreach (var group in groups)
        {
            // Mỗi video chỉ lấy observation mới nhất trong nhóm; nhiều snapshot cùng ngày không được cộng dồn
            var latestPerVideo = group
                .GroupBy(r => r.VideoKey)
                .Select(g => g.OrderByDescending(r => r.CapturedAtUtc).First())
                .ToList();

            double? pointValue = metric switch
            {
                "views" => latestPerVideo.Any(r => r.Views.HasValue) ? latestPerVideo.Sum(r => (double)r.Views.GetValueOrDefault()) : null,
                "likes" => latestPerVideo.Any(r => r.Likes.HasValue) ? latestPerVideo.Sum(r => (double)r.Likes.GetValueOrDefault()) : null,
                "comments" => latestPerVideo.Any(r => r.Comments.HasValue) ? latestPerVideo.Sum(r => (double)r.Comments.GetValueOrDefault()) : null,
                "shares" => latestPerVideo.Any(r => r.Shares.HasValue) ? latestPerVideo.Sum(r => (double)r.Shares.GetValueOrDefault()) : null,
                "gmv" => latestPerVideo.Any(r => r.GmvAmount.HasValue) ? (double)latestPerVideo.Sum(r => r.GmvAmount.GetValueOrDefault()) : null,
                "clicks" => latestPerVideo.Any(r => r.AffiliateClicks.HasValue) ? latestPerVideo.Sum(r => (double)r.AffiliateClicks.GetValueOrDefault()) : null,
                _ => latestPerVideo.Any(r => r.Views.HasValue) ? latestPerVideo.Sum(r => (double)r.Views.GetValueOrDefault()) : null
            };

            points.Add(new AnalyticsTimeseriesPointDto(
                Date: group.Key,
                Metric: metric,
                Value: pointValue
            ));
        }

        return points;
    }

    public async Task<TikTokStudioImportResultDto> ImportTikTokStudioExportsAsync(
        ImportTikTokStudioExportsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            throw new ArgumentException("Cần chọn tài khoản TikTok.", nameof(request.AccountId));
        }

        var accountId = request.AccountId.Trim();
        await using (var scopeDb = CreateDbContext())
        {
            await RequireTikTokAccountAsync(scopeDb, accountId, cancellationToken);
        }
        var capturedAtUtc = DateTime.UtcNow;
        var batch = _importExportService.ParseTikTokStudioExports(
            request.OverviewCsv,
            request.ContentCsv,
            request.ViewersCsv,
            capturedAtUtc);

        var dates = batch.OverviewRecords.Select(r => r.PeriodDateUtc)
            .Concat(batch.ViewersRecords.Select(r => r.PeriodDateUtc))
            .Concat(batch.ContentRecords.Where(r => r.PublishedAtUtc.HasValue).Select(r => r.PublishedAtUtc!.Value))
            .ToList();
        var snapshotId = Guid.NewGuid().ToString("N");
        var dailyRecords = batch.OverviewRecords.Concat(batch.ViewersRecords).Select(r => new TikTokAnalyticsDailyRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            SnapshotId = snapshotId,
            AccountId = accountId,
            DataType = r.DataType,
            PeriodDateUtc = r.PeriodDateUtc,
            VideoViews = r.VideoViews,
            ProfileViews = r.ProfileViews,
            Likes = r.Likes,
            Comments = r.Comments,
            Shares = r.Shares,
            TotalViewers = r.TotalViewers,
            NewViewers = r.NewViewers,
            ReturningViewers = r.ReturningViewers,
            CapturedAtUtc = capturedAtUtc
        }).ToList();

        var videoRecords = batch.ContentRecords.Select(r => new VideoAnalyticsRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            SnapshotId = snapshotId,
            AccountId = accountId,
            VideoKey = r.VideoKey,
            Title = r.Title,
            Views = r.Views,
            Likes = r.Likes,
            Comments = r.Comments,
            Shares = r.Shares,
            WatchTimeAvgSec = r.WatchTimeAvgSec,
            GmvAmount = r.GmvAmount,
            AffiliateClicks = r.AffiliateClicks,
            PublishedAtUtc = r.PublishedAtUtc,
            CapturedAtUtc = capturedAtUtc
        }).ToList();

        await using var dbContext = CreateDbContext();
        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Set<AnalyticsSnapshot>().Add(new AnalyticsSnapshot
        {
            Id = snapshotId,
            AccountId = accountId,
            Source = "TikTok Studio CSV Export",
            PeriodStart = dates.Count == 0 ? null : dates.Min(),
            PeriodEnd = dates.Count == 0 ? null : dates.Max(),
            RecordCount = dailyRecords.Count + videoRecords.Count,
            RawManifest = System.Text.Json.JsonSerializer.Serialize(new
            {
                format = "TikTok Studio CSV",
                capturedAtUtc,
                overviewRecords = batch.OverviewRecords.Count,
                contentRecords = batch.ContentRecords.Count,
                viewersRecords = batch.ViewersRecords.Count
            }),
            CreatedAtUtc = capturedAtUtc
        });
        dbContext.Set<TikTokAnalyticsDailyRecord>().AddRange(dailyRecords);
        dbContext.Set<VideoAnalyticsRecord>().AddRange(videoRecords);
        await dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new TikTokStudioImportResultDto(
            SnapshotId: snapshotId,
            OverviewRecordCount: batch.OverviewRecords.Count,
            ContentRecordCount: batch.ContentRecords.Count,
            ViewersRecordCount: batch.ViewersRecords.Count,
            PeriodStart: dates.Count == 0 ? null : dates.Min(),
            PeriodEnd: dates.Count == 0 ? null : dates.Max(),
            CapturedAtUtc: capturedAtUtc);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VideoMetricRecordDto>> ListTopVideosAsync(ListTopVideosRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            throw new ArgumentException("Cần chọn tài khoản TikTok.", nameof(request.AccountId));
        }

        var normalizedAccountId = request.AccountId.Trim();
        var sortBy = request.SortBy?.Trim().ToLowerInvariant() ?? "views";
        int limit = request.Limit is > 0 ? request.Limit.Value : 10;

        await using var dbContext = CreateDbContext();
        await RequireTikTokAccountAsync(dbContext, normalizedAccountId, cancellationToken);

        var records = await dbContext.Set<VideoAnalyticsRecord>()
            .AsNoTracking()
            .Where(r => r.AccountId == normalizedAccountId)
            .ToListAsync(cancellationToken);

        if (records.Count == 0)
        {
            return Array.Empty<VideoMetricRecordDto>();
        }

        // Lấy bản ghi mới nhất của từng video
        var latestPerVideo = records
            .GroupBy(r => r.VideoKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.CapturedAtUtc).First());

        var ordered = sortBy switch
        {
            "likes" => latestPerVideo.OrderByDescending(r => r.Likes ?? -1),
            "comments" => latestPerVideo.OrderByDescending(r => r.Comments ?? -1),
            "shares" => latestPerVideo.OrderByDescending(r => r.Shares ?? -1),
            "gmv" => latestPerVideo.OrderByDescending(r => r.GmvAmount ?? -1),
            "clicks" => latestPerVideo.OrderByDescending(r => r.AffiliateClicks ?? -1),
            "published" or "date" => latestPerVideo.OrderByDescending(r => r.PublishedAtUtc ?? DateTime.MinValue),
            _ => latestPerVideo.OrderByDescending(r => r.Views ?? -1)
        };

        var top = ordered.Take(limit).Select(r => new VideoMetricRecordDto(
            Id: r.Id,
            SnapshotId: r.SnapshotId,
            AccountId: r.AccountId,
            VideoKey: r.VideoKey,
            Title: r.Title,
            Views: r.Views,
            Likes: r.Likes,
            Comments: r.Comments,
            Shares: r.Shares,
            WatchTimeAvgSec: r.WatchTimeAvgSec,
            GmvAmount: r.GmvAmount,
            AffiliateClicks: r.AffiliateClicks,
            PublishedAtUtc: r.PublishedAtUtc,
            CapturedAtUtc: r.CapturedAtUtc
        )).ToList();

        return top;
    }

    /// <inheritdoc />
    public async Task<AnalyticsImportResultDto> ImportDataAsync(ImportAnalyticsDataRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            throw new ArgumentException("Cần chọn tài khoản TikTok.", nameof(request.AccountId));
        }

        var normalizedAccountId = request.AccountId.Trim();
        await using (var scopeDb = CreateDbContext())
        {
            await RequireTikTokAccountAsync(scopeDb, normalizedAccountId, cancellationToken);
        }
        var batch = _importExportService.ParseRawContent(request.RawContent, request.Format);

        if (batch.Records.Count == 0)
        {
            return new AnalyticsImportResultDto(
                SnapshotId: string.Empty,
                RecordCount: 0,
                Success: false,
                Message: "Không tìm thấy bản ghi video hợp lệ nào trong nội dung đã nhập.",
                PeriodStart: null,
                PeriodEnd: null,
                CapturedAtUtc: DateTime.UtcNow
            );
        }

        var snapshotId = Guid.NewGuid().ToString("N");
        var nowUtc = DateTime.UtcNow;

        var source = string.IsNullOrWhiteSpace(request.Source)
            ? (request.Format?.ToUpperInvariant() ?? "IMPORT")
            : request.Source.Trim();

        var snapshot = new AnalyticsSnapshot
        {
            Id = snapshotId,
            AccountId = normalizedAccountId,
            Source = source,
            PeriodStart = batch.PeriodStart,
            PeriodEnd = batch.PeriodEnd,
            RecordCount = batch.Records.Count,
            RawManifest = $"{{\"format\":\"{request.Format}\",\"importedAt\":\"{nowUtc:O}\",\"source\":\"{source}\"}}",
            CreatedAtUtc = nowUtc
        };

        var entityRecords = batch.Records.Select(r => new VideoAnalyticsRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            SnapshotId = snapshotId,
            AccountId = normalizedAccountId,
            VideoKey = r.VideoKey,
            Title = r.Title,
            Views = r.Views,
            Likes = r.Likes,
            Comments = r.Comments,
            Shares = r.Shares,
            WatchTimeAvgSec = r.WatchTimeAvgSec,
            GmvAmount = r.GmvAmount,
            AffiliateClicks = r.AffiliateClicks,
            PublishedAtUtc = r.PublishedAtUtc,
            CapturedAtUtc = r.CapturedAtUtc ?? nowUtc
        }).ToList();

        await using var dbContext = CreateDbContext();
        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        dbContext.Set<AnalyticsSnapshot>().Add(snapshot);
        dbContext.Set<VideoAnalyticsRecord>().AddRange(entityRecords);

        await dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new AnalyticsImportResultDto(
            SnapshotId: snapshotId,
            RecordCount: entityRecords.Count,
            Success: true,
            Message: $"Nhập khẩu thành công {entityRecords.Count} bản ghi phân tích vào Snapshot '{snapshotId}'.",
            PeriodStart: batch.PeriodStart,
            PeriodEnd: batch.PeriodEnd,
            CapturedAtUtc: nowUtc
        );
    }

    /// <inheritdoc />
    public async Task<AnalyticsExportResultDto> ExportDataAsync(ExportAnalyticsDataRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.AccountId))
        {
            throw new ArgumentException("Cần chọn tài khoản TikTok.", nameof(request.AccountId));
        }

        var normalizedAccountId = request.AccountId.Trim();

        await using var dbContext = CreateDbContext();
        await RequireTikTokAccountAsync(dbContext, normalizedAccountId, cancellationToken);

        var query = dbContext.Set<VideoAnalyticsRecord>()
            .AsNoTracking()
            .Where(r => r.AccountId == normalizedAccountId);

        if (request.PeriodDays.HasValue && request.PeriodDays.Value > 0)
        {
            var sinceUtc = DateTime.UtcNow.AddDays(-request.PeriodDays.Value);
            query = query.Where(r => r.CapturedAtUtc >= sinceUtc);
        }

        var records = await query.ToListAsync(cancellationToken);

        var latestPerVideo = records
            .GroupBy(r => r.VideoKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.CapturedAtUtc).First())
            .OrderByDescending(r => r.Views ?? -1)
            .ToList();

        var csvContent = _importExportService.ExportCsv(latestPerVideo);
        var fileName = $"analytics_{normalizedAccountId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";

        return new AnalyticsExportResultDto(
            Format: "csv",
            Content: csvContent,
            FileName: fileName
        );
    }
}
