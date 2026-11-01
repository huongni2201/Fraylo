using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fraylo.Application.Analytics.Abstractions;
using Fraylo.Infrastructure.Persistence.Entities;

namespace Fraylo.Application.Analytics;

/// <summary>
/// DTO chứa kết quả phân tích nội dung import thô (CSV/JSON).
/// </summary>
public sealed class ParsedAnalyticsBatch
{
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public List<ParsedVideoMetricRecord> Records { get; set; } = new();
}

/// <summary>
/// DTO chứa thông số video đã bóc tách từ file import.
/// </summary>
public sealed class ParsedVideoMetricRecord
{
    public string VideoKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public long? Views { get; set; }
    public long? Likes { get; set; }
    public long? Comments { get; set; }
    public long? Shares { get; set; }
    public double? WatchTimeAvgSec { get; set; }
    public decimal? GmvAmount { get; set; }
    public long? AffiliateClicks { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime? CapturedAtUtc { get; set; }
}

public sealed class ParsedTikTokStudioExports
{
    public List<ParsedTikTokDailyRecord> OverviewRecords { get; } = new();
    public List<ParsedVideoMetricRecord> ContentRecords { get; } = new();
    public List<ParsedTikTokDailyRecord> ViewersRecords { get; } = new();
}

public sealed class ParsedTikTokDailyRecord
{
    public string DataType { get; set; } = string.Empty;
    public DateTime PeriodDateUtc { get; set; }
    public long? VideoViews { get; set; }
    public long? ProfileViews { get; set; }
    public long? Likes { get; set; }
    public long? Comments { get; set; }
    public long? Shares { get; set; }
    public long? TotalViewers { get; set; }
    public long? NewViewers { get; set; }
    public long? ReturningViewers { get; set; }
}

/// <summary>
/// Động cơ nhập/xuất dữ liệu phân tích chỉ số TikTok (CSV/JSON).
/// Đảm bảo tính trung thực: Dữ liệu thiếu được giữ null, không tự gán 0.
/// Hỗ trợ CSV RFC 4180, tiếng Việt UTF-8 BOM, linh hoạt header và deduplication trong batch.
/// </summary>
public sealed class AnalyticsImportExportService : IAnalyticsDataParser
{
    /// <summary>
    /// Định danh nền tảng phân tích số liệu mạng xã hội.
    /// </summary>
    public string PlatformKey => "tiktok";

    public ParsedTikTokStudioExports ParseTikTokStudioExports(
        string overviewCsv,
        string contentCsv,
        string viewersCsv,
        DateTime capturedAtUtc)
    {
        var result = new ParsedTikTokStudioExports();
        result.OverviewRecords.AddRange(ParseDailyCsv(overviewCsv, "overview", capturedAtUtc));
        ValidateContentCsv(contentCsv);
        result.ContentRecords.AddRange(ParseRawContent(contentCsv, "csv").Records);
        result.ViewersRecords.AddRange(ParseDailyCsv(viewersCsv, "viewers", capturedAtUtc));
        return result;
    }

    private static void ValidateContentCsv(string csv)
    {
        var rows = TokenizeCsv(csv);
        if (rows.Count == 0)
        {
            throw new ArgumentException("Tệp TikTok Content đang trống.", nameof(csv));
        }

        var headers = rows[0].Select(NormalizeHeaderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!headers.Contains("videotitle") || !headers.Contains("videolink") ||
            !headers.Contains("totallikes") || !headers.Contains("totalviews"))
        {
            throw new ArgumentException("Tệp TikTok Content không đúng cấu trúc CSV xuất từ TikTok Studio.", nameof(csv));
        }
    }

    private static List<ParsedTikTokDailyRecord> ParseDailyCsv(string csv, string dataType, DateTime capturedAtUtc)
    {
        var rows = TokenizeCsv(csv);
        if (rows.Count == 0)
        {
            throw new ArgumentException($"Tệp TikTok {dataType} đang trống.", nameof(csv));
        }

        var headers = BuildHeaderIndexMap(rows[0]);
        bool hasDate = FindColumn(rows[0], "date") >= 0;
        bool hasExpectedMetrics = dataType == "overview"
            ? headers.ContainsKey("views") || headers.ContainsKey("profile_views") || headers.ContainsKey("likes") || headers.ContainsKey("comments") || headers.ContainsKey("shares")
            : headers.ContainsKey("total_viewers") || headers.ContainsKey("new_viewers") || headers.ContainsKey("returning_viewers");

        if (!hasDate || !hasExpectedMetrics)
        {
            throw new ArgumentException($"Tệp TikTok {dataType} không đúng cấu trúc CSV xuất từ TikTok Studio.", nameof(csv));
        }

        int dateIndex = FindColumn(rows[0], "date");
        var records = new List<ParsedTikTokDailyRecord>();
        foreach (var row in rows.Skip(1))
        {
            if (dateIndex >= row.Count || !TryParseTikTokDate(row[dateIndex], capturedAtUtc, out var date))
            {
                continue;
            }

            string value(string key) => headers.TryGetValue(key, out var index) && index < row.Count ? row[index] : string.Empty;
            records.Add(new ParsedTikTokDailyRecord
            {
                DataType = dataType,
                PeriodDateUtc = date,
                VideoViews = dataType == "overview" ? ParseLongString(value("views")) : null,
                ProfileViews = dataType == "overview" ? ParseLongString(value("profile_views")) : null,
                Likes = dataType == "overview" ? ParseLongString(value("likes")) : null,
                Comments = dataType == "overview" ? ParseLongString(value("comments")) : null,
                Shares = dataType == "overview" ? ParseLongString(value("shares")) : null,
                TotalViewers = dataType == "viewers" ? ParseLongString(value("total_viewers")) : null,
                NewViewers = dataType == "viewers" ? ParseLongString(value("new_viewers")) : null,
                ReturningViewers = dataType == "viewers" ? ParseLongString(value("returning_viewers")) : null
            });
        }

        return records;
    }

    private static int FindColumn(List<string> headers, string normalizedName)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            if (NormalizeHeaderName(headers[i]) == normalizedName)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryParseTikTokDate(string? raw, DateTime capturedAtUtc, out DateTime dateUtc)
    {
        dateUtc = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var value = raw.Trim();
        var formats = new[] { "MMMM d", "MMM d", "M/d", "MM/dd" };
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var monthDay))
            {
                var year = capturedAtUtc.Year;
                var candidate = new DateTime(year, monthDay.Month, monthDay.Day, 0, 0, 0, DateTimeKind.Utc);
                if (candidate > capturedAtUtc.Date.AddDays(2)) candidate = candidate.AddYears(-1);
                dateUtc = candidate;
                return true;
            }
        }

        if (DateTime.TryParse(value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            dateUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        return false;
    }
    /// <summary>
    /// Phân tích cú pháp nội dung raw (CSV hoặc JSON) thành batch các bản ghi video metrics.
    /// </summary>
    public ParsedAnalyticsBatch ParseRawContent(string rawContent, string format)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return new ParsedAnalyticsBatch();
        }

        var normalizedFormat = format?.Trim().ToLowerInvariant() ?? string.Empty;

        ParsedAnalyticsBatch batch;
        if (normalizedFormat == "json" || (string.IsNullOrEmpty(normalizedFormat) && (rawContent.TrimStart().StartsWith("{") || rawContent.TrimStart().StartsWith("["))))
        {
            batch = ParseJson(rawContent);
        }
        else
        {
            batch = ParseCsv(rawContent);
        }

        // Deduplicate records trong cùng batch theo video_key:
        // Giữ lại record xuất hiện sau cùng hoặc có thông tin captured mới hơn
        var deduped = new Dictionary<string, ParsedVideoMetricRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in batch.Records)
        {
            if (string.IsNullOrWhiteSpace(record.VideoKey))
            {
                continue;
            }

            var trimmedKey = record.VideoKey.Trim();
            record.VideoKey = trimmedKey;
            if (string.IsNullOrWhiteSpace(record.Title))
            {
                record.Title = $"Video {trimmedKey}";
            }

            deduped[trimmedKey] = record;
        }

        batch.Records = deduped.Values.ToList();

        // Tự động suy ra PeriodStart và PeriodEnd nếu chưa có
        if (!batch.PeriodStart.HasValue || !batch.PeriodEnd.HasValue)
        {
            var dates = batch.Records
                .Select(r => r.PublishedAtUtc ?? r.CapturedAtUtc)
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .ToList();

            if (dates.Count > 0)
            {
                batch.PeriodStart ??= dates.Min();
                batch.PeriodEnd ??= dates.Max();
            }
        }

        return batch;
    }

    /// <summary>
    /// Phân tích nội dung JSON.
    /// </summary>
    public ParsedAnalyticsBatch ParseJson(string jsonContent)
    {
        var batch = new ParsedAnalyticsBatch();

        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        JsonElement recordsElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            recordsElement = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyCaseInsensitive(root, new[] { "periodStart", "period_start", "startDate", "start_date" }, out var startElem))
            {
                batch.PeriodStart = ParseDateTime(startElem.GetString());
            }

            if (TryGetPropertyCaseInsensitive(root, new[] { "periodEnd", "period_end", "endDate", "end_date" }, out var endElem))
            {
                batch.PeriodEnd = ParseDateTime(endElem.GetString());
            }

            if (TryGetPropertyCaseInsensitive(root, new[] { "records", "data", "videos", "items", "rows" }, out var listElem) && listElem.ValueKind == JsonValueKind.Array)
            {
                recordsElement = listElem;
            }
            else
            {
                recordsElement = root;
            }
        }
        else
        {
            return batch;
        }

        if (recordsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in recordsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var record = ParseJsonObjectToRecord(item);
                    if (record != null && !string.IsNullOrWhiteSpace(record.VideoKey))
                    {
                        batch.Records.Add(record);
                    }
                }
            }
        }
        else if (recordsElement.ValueKind == JsonValueKind.Object)
        {
            var record = ParseJsonObjectToRecord(recordsElement);
            if (record != null && !string.IsNullOrWhiteSpace(record.VideoKey))
            {
                batch.Records.Add(record);
            }
        }

        return batch;
    }

    private ParsedVideoMetricRecord? ParseJsonObjectToRecord(JsonElement element)
    {
        var record = new ParsedVideoMetricRecord();

        if (TryGetPropertyCaseInsensitive(element, new[] { "video_key", "videoKey", "videoId", "video_id", "id", "video" }, out var keyProp))
        {
            record.VideoKey = GetStringOrRaw(keyProp) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(record.VideoKey))
        {
            return null;
        }

        if (TryGetPropertyCaseInsensitive(element, new[] { "title", "video_title", "videoTitle", "name", "caption" }, out var titleProp))
        {
            record.Title = GetStringOrRaw(titleProp) ?? string.Empty;
        }

        if (TryGetPropertyCaseInsensitive(element, new[] { "views", "view", "play_count", "playCount", "plays", "view_count" }, out var viewsProp))
        {
            record.Views = ParseLong(viewsProp);
        }

        if (TryGetPropertyCaseInsensitive(element, new[] { "likes", "like", "digg_count", "diggCount", "diggs", "like_count" }, out var likesProp))
        {
            record.Likes = ParseLong(likesProp);
        }

        if (TryGetPropertyCaseInsensitive(element, new[] { "comments", "comment", "comment_count", "commentCount" }, out var commProp))
        {
            record.Comments = ParseLong(commProp);
        }

        if (TryGetPropertyCaseInsensitive(element, new[] { "shares", "share", "share_count", "shareCount" }, out var shareProp))
        {
            record.Shares = ParseLong(shareProp);
        }

        if (TryGetPropertyCaseInsensitive(element, new[] { "watch_time_avg_sec", "watchTimeAvgSec", "average_play_time", "averagePlayTime", "avg_play_time", "avg_watch_time", "watch_time" }, out var wtProp))
        {
            record.WatchTimeAvgSec = ParseDouble(wtProp);
        }

        if (TryGetPropertyCaseInsensitive(element, new[] { "gmv_amount", "gmvAmount", "gmv", "revenue", "sales", "total_gmv" }, out var gmvProp))
        {
            record.GmvAmount = ParseDecimal(gmvProp);
        }

        if (TryGetPropertyCaseInsensitive(element, new[] { "affiliate_clicks", "affiliateClicks", "clicks", "click_count", "clickCount", "affiliate_click" }, out var clickProp))
        {
            record.AffiliateClicks = ParseLong(clickProp);
        }

        if (TryGetPropertyCaseInsensitive(element, new[] { "published_at_utc", "publishedAtUtc", "publish_time", "publishTime", "published_at", "create_time", "created_time", "upload_time", "date" }, out var pubProp))
        {
            record.PublishedAtUtc = ParseDateTime(GetStringOrRaw(pubProp));
        }

        if (TryGetPropertyCaseInsensitive(element, new[] { "captured_at_utc", "capturedAtUtc", "captured_at", "crawl_time", "snapshot_time" }, out var capProp))
        {
            record.CapturedAtUtc = ParseDateTime(GetStringOrRaw(capProp));
        }

        return record;
    }

    /// <summary>
    /// Phân tích nội dung CSV tuân thủ RFC 4180 và linh hoạt header.
    /// </summary>
    public ParsedAnalyticsBatch ParseCsv(string csvContent)
    {
        var batch = new ParsedAnalyticsBatch();
        var rows = TokenizeCsv(csvContent);
        if (rows.Count < 2)
        {
            return batch;
        }

        var headerRow = rows[0];
        var headerMap = BuildHeaderIndexMap(headerRow);

        if (!headerMap.ContainsKey("video_key"))
        {
            if (headerRow.Count > 0 && !string.IsNullOrWhiteSpace(headerRow[0]))
            {
                headerMap["video_key"] = 0;
            }
        }

        for (int i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count == 0 || row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var record = new ParsedVideoMetricRecord();

            if (headerMap.TryGetValue("video_key", out var keyIdx) && keyIdx < row.Count)
            {
                record.VideoKey = row[keyIdx]?.Trim() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(record.VideoKey))
            {
                continue;
            }

            if (headerMap.TryGetValue("title", out var titleIdx) && titleIdx < row.Count)
            {
                record.Title = row[titleIdx]?.Trim() ?? string.Empty;
            }

            if (headerMap.TryGetValue("views", out var viewsIdx) && viewsIdx < row.Count)
            {
                record.Views = ParseLongString(row[viewsIdx]);
            }

            if (headerMap.TryGetValue("likes", out var likesIdx) && likesIdx < row.Count)
            {
                record.Likes = ParseLongString(row[likesIdx]);
            }

            if (headerMap.TryGetValue("comments", out var commIdx) && commIdx < row.Count)
            {
                record.Comments = ParseLongString(row[commIdx]);
            }

            if (headerMap.TryGetValue("shares", out var sharesIdx) && sharesIdx < row.Count)
            {
                record.Shares = ParseLongString(row[sharesIdx]);
            }

            if (headerMap.TryGetValue("watch_time_avg_sec", out var wtIdx) && wtIdx < row.Count)
            {
                record.WatchTimeAvgSec = ParseDoubleString(row[wtIdx]);
            }

            if (headerMap.TryGetValue("gmv_amount", out var gmvIdx) && gmvIdx < row.Count)
            {
                record.GmvAmount = ParseDecimalString(row[gmvIdx]);
            }

            if (headerMap.TryGetValue("affiliate_clicks", out var clicksIdx) && clicksIdx < row.Count)
            {
                record.AffiliateClicks = ParseLongString(row[clicksIdx]);
            }

            if (headerMap.TryGetValue("published_at_utc", out var pubIdx) && pubIdx < row.Count)
            {
                record.PublishedAtUtc = ParseDateTime(row[pubIdx]);
            }

            if (headerMap.TryGetValue("captured_at_utc", out var capIdx) && capIdx < row.Count)
            {
                record.CapturedAtUtc = ParseDateTime(row[capIdx]);
            }

            batch.Records.Add(record);
        }

        return batch;
    }

    /// <summary>
    /// Xuất danh sách bản ghi phân tích ra định dạng CSV UTF-8 có BOM (triển khai IAnalyticsDataParser).
    /// </summary>
    public string ExportCsv(IReadOnlyList<VideoAnalyticsRecord> records)
    {
        return ExportToCsv(records);
    }

    /// <summary>
    /// Xuất danh sách bản ghi phân tích ra định dạng CSV UTF-8 có BOM.
    /// </summary>
    public string ExportToCsv(IEnumerable<VideoAnalyticsRecord> records)
    {
        var sb = new StringBuilder();
        // Gắn tiền tố BOM \uFEFF để Excel mở UTF-8 hiển thị chuẩn tiếng Việt
        sb.Append('\uFEFF');

        // Header chuẩn
        sb.AppendLine("video_key,title,views,likes,comments,shares,watch_time_avg_sec,gmv_amount,affiliate_clicks,published_at_utc,captured_at_utc");

        foreach (var r in records)
        {
            var line = string.Join(",", new[]
            {
                EscapeCsvCell(r.VideoKey),
                EscapeCsvCell(r.Title),
                r.Views.HasValue ? r.Views.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                r.Likes.HasValue ? r.Likes.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                r.Comments.HasValue ? r.Comments.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                r.Shares.HasValue ? r.Shares.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                r.WatchTimeAvgSec.HasValue ? r.WatchTimeAvgSec.Value.ToString("F2", CultureInfo.InvariantCulture) : string.Empty,
                r.GmvAmount.HasValue ? r.GmvAmount.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                r.AffiliateClicks.HasValue ? r.AffiliateClicks.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
                r.PublishedAtUtc.HasValue ? r.PublishedAtUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") : string.Empty,
                r.CapturedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
            });
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static string EscapeCsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n');
        if (!mustQuote)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    /// <summary>
    /// Tách dòng CSV theo chuẩn RFC 4180 (xử lý ngoặc kép và dấu phẩy bên trong).
    /// </summary>
    private static List<List<string>> TokenizeCsv(string csv)
    {
        var result = new List<List<string>>();
        if (string.IsNullOrEmpty(csv))
        {
            return result;
        }

        // Bỏ qua BOM nếu có
        if (csv[0] == '\uFEFF')
        {
            csv = csv.Substring(1);
        }

        var currentRow = new List<string>();
        var currentCell = new StringBuilder();
        bool inQuotes = false;
        int i = 0;
        int length = csv.Length;

        while (i < length)
        {
            char c = csv[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Check xem có escaped quote "" không
                    if (i + 1 < length && csv[i + 1] == '"')
                    {
                        currentCell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentCell.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();
                }
                else if (c == '\r')
                {
                    if (i + 1 < length && csv[i + 1] == '\n')
                    {
                        i++;
                    }
                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();
                    result.Add(currentRow);
                    currentRow = new List<string>();
                }
                else if (c == '\n')
                {
                    currentRow.Add(currentCell.ToString());
                    currentCell.Clear();
                    result.Add(currentRow);
                    currentRow = new List<string>();
                }
                else
                {
                    currentCell.Append(c);
                }
            }
            i++;
        }

        if (currentCell.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentCell.ToString());
            result.Add(currentRow);
        }

        return result;
    }

    private static Dictionary<string, int> BuildHeaderIndexMap(List<string> headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < headerRow.Count; i++)
        {
            var rawHeader = headerRow[i]?.Trim() ?? string.Empty;
            var norm = NormalizeHeaderName(rawHeader);

            if (IsVideoKeyHeader(norm)) AddIfNotExists(map, "video_key", i);
            else if (IsTitleHeader(norm)) AddIfNotExists(map, "title", i);
            else if (IsViewsHeader(norm)) AddIfNotExists(map, "views", i);
            else if (IsProfileViewsHeader(norm)) AddIfNotExists(map, "profile_views", i);
            else if (IsLikesHeader(norm)) AddIfNotExists(map, "likes", i);
            else if (IsCommentsHeader(norm)) AddIfNotExists(map, "comments", i);
            else if (IsSharesHeader(norm)) AddIfNotExists(map, "shares", i);
            else if (IsTotalViewersHeader(norm)) AddIfNotExists(map, "total_viewers", i);
            else if (IsNewViewersHeader(norm)) AddIfNotExists(map, "new_viewers", i);
            else if (IsReturningViewersHeader(norm)) AddIfNotExists(map, "returning_viewers", i);
            else if (IsWatchTimeHeader(norm)) AddIfNotExists(map, "watch_time_avg_sec", i);
            else if (IsGmvHeader(norm)) AddIfNotExists(map, "gmv_amount", i);
            else if (IsClicksHeader(norm)) AddIfNotExists(map, "affiliate_clicks", i);
            else if (IsPublishedHeader(norm)) AddIfNotExists(map, "published_at_utc", i);
            else if (IsCapturedHeader(norm)) AddIfNotExists(map, "captured_at_utc", i);
        }

        return map;
    }

    private static void AddIfNotExists(Dictionary<string, int> map, string key, int index)
    {
        if (!map.ContainsKey(key))
        {
            map[key] = index;
        }
    }

    private static string NormalizeHeaderName(string header)
    {
        return header.ToLowerInvariant()
            .Replace(" ", "")
            .Replace("_", "")
            .Replace("-", "");
    }

    private static bool IsVideoKeyHeader(string n) =>
        n is "videokey" or "videoid" or "id" or "video" or "key" or "itemid" or "videolink" or "link" or "url";

    private static bool IsTitleHeader(string n) =>
        n is "title" or "videotitle" or "name" or "caption" or "desc" or "description";

    private static bool IsViewsHeader(string n) =>
        n is "views" or "view" or "playcount" or "plays" or "totalviews" or "viewcount" or "videoviews";

    private static bool IsProfileViewsHeader(string n) =>
        n is "profileviews" or "profileview";

    private static bool IsLikesHeader(string n) =>
        n is "likes" or "like" or "diggcount" or "diggs" or "likecount" or "totallikes";

    private static bool IsCommentsHeader(string n) =>
        n is "comments" or "comment" or "commentcount" or "totalcomments";

    private static bool IsSharesHeader(string n) =>
        n is "shares" or "share" or "sharecount" or "totalshares";

    private static bool IsTotalViewersHeader(string n) =>
        n is "totalviewers" or "viewers";

    private static bool IsNewViewersHeader(string n) =>
        n is "newviewers";

    private static bool IsReturningViewersHeader(string n) =>
        n is "returningviewers";

    private static bool IsWatchTimeHeader(string n) =>
        n is "watchtimeavgsec" or "watchtimeavg" or "averageplaytime" or "avgplaytime" or "avgwatchtime" or "watchtime" or "averagewatchtime";

    private static bool IsGmvHeader(string n) =>
        n is "gmvamount" or "gmv" or "revenue" or "sales" or "totalgmv" or "amount";

    private static bool IsClicksHeader(string n) =>
        n is "affiliateclicks" or "clicks" or "clickcount" or "affiliateclick";

    private static bool IsPublishedHeader(string n) =>
        n is "publishedatutc" or "publishedat" or "publishtime" or "posttime" or "createtime" or "createdtime" or "uploadtime" or "date" or "publisheddate";

    private static bool IsCapturedHeader(string n) =>
        n is "capturedatutc" or "capturedat" or "crawltime" or "snapshottime";

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string[] candidates, out JsonElement matched)
    {
        foreach (var prop in element.EnumerateObject())
        {
            foreach (var cand in candidates)
            {
                if (string.Equals(prop.Name, cand, StringComparison.OrdinalIgnoreCase))
                {
                    matched = prop.Value;
                    return true;
                }

                if (string.Equals(NormalizeHeaderName(prop.Name), NormalizeHeaderName(cand), StringComparison.OrdinalIgnoreCase))
                {
                    matched = prop.Value;
                    return true;
                }
            }
        }

        matched = default;
        return false;
    }

    private static string? GetStringOrRaw(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static long? ParseLong(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var val))
        {
            return val;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return ParseLongString(element.GetString());
        }

        return null;
    }

    private static long? ParseLongString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var clean = raw.Trim().ToLowerInvariant();

        // Xử lý hậu tố K, M, B
        double multiplier = 1;
        if (clean.EndsWith("k"))
        {
            multiplier = 1_000;
            clean = clean[..^1].Trim();
        }
        else if (clean.EndsWith("m"))
        {
            multiplier = 1_000_000;
            clean = clean[..^1].Trim();
        }
        else if (clean.EndsWith("b"))
        {
            multiplier = 1_000_000_000;
            clean = clean[..^1].Trim();
        }

        clean = CleanNumericString(clean);

        if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return (long)Math.Round(d * multiplier);
        }

        return null;
    }

    private static double? ParseDouble(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var val))
        {
            return val;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return ParseDoubleString(element.GetString());
        }

        return null;
    }

    private static double? ParseDoubleString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var clean = raw.Trim().ToLowerInvariant()
            .Replace("s", "")
            .Replace("sec", "")
            .Replace("giây", "")
            .Trim();

        clean = CleanNumericString(clean);

        if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        return null;
    }

    private static decimal? ParseDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var val))
        {
            return val;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return ParseDecimalString(element.GetString());
        }

        return null;
    }

    private static decimal? ParseDecimalString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var clean = raw.Trim().ToLowerInvariant();

        decimal multiplier = 1m;
        if (clean.EndsWith("k"))
        {
            multiplier = 1_000m;
            clean = clean[..^1].Trim();
        }
        else if (clean.EndsWith("m") || clean.EndsWith("tr"))
        {
            multiplier = 1_000_000m;
            clean = clean.EndsWith("tr") ? clean[..^2].Trim() : clean[..^1].Trim();
        }
        else if (clean.EndsWith("b") || clean.EndsWith("tỷ") || clean.EndsWith("ty"))
        {
            multiplier = 1_000_000_000m;
            clean = clean.EndsWith("tỷ") || clean.EndsWith("ty") ? clean[..^2].Trim() : clean[..^1].Trim();
        }

        clean = CleanNumericString(clean);

        if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
        {
            return result * multiplier;
        }

        return null;
    }

    private static string CleanNumericString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var sb = new StringBuilder();
        foreach (char c in raw)
        {
            if (char.IsDigit(c) || c == '.' || c == ',' || c == '-')
            {
                sb.Append(c);
            }
        }

        var s = sb.ToString();
        if (string.IsNullOrEmpty(s)) return string.Empty;

        int commaCount = s.Count(c => c == ',');
        int dotCount = s.Count(c => c == '.');

        if (commaCount > 0 && dotCount > 0)
        {
            int firstComma = s.IndexOf(',');
            int firstDot = s.IndexOf('.');
            if (firstComma < firstDot)
            {
                // 1,234,567.89 -> phẩy là ngàn, chấm là thập phân
                s = s.Replace(",", "");
            }
            else
            {
                // 1.234.567,89 -> chấm là ngàn, phẩy là thập phân
                s = s.Replace(".", "").Replace(",", ".");
            }
        }
        else if (commaCount > 1)
        {
            // Nhiều dấu phẩy: 1,500,000 -> chắc chắn là phân cách ngàn
            s = s.Replace(",", "");
        }
        else if (dotCount > 1)
        {
            // Nhiều dấu chấm: 1.500.000 -> chắc chắn là phân cách ngàn
            s = s.Replace(".", "");
        }
        else if (commaCount == 1)
        {
            // Đúng 1 dấu phẩy: ví dụ 1,500 hoặc 1,5
            int commaIdx = s.IndexOf(',');
            int decimals = s.Length - commaIdx - 1;
            if (decimals == 3 && commaIdx <= 3)
            {
                // 1,500 -> ngàn
                s = s.Replace(",", "");
            }
            else
            {
                // 1,5 -> thập phân
                s = s.Replace(",", ".");
            }
        }

        return s;
    }

    private static DateTime? ParseDateTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim();

        // Kiểm tra Unix timestamp bằng số
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixTime))
        {
            if (unixTime > 10_000_000_000_000)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixTime / 1000).UtcDateTime;
            }
            if (unixTime > 10_000_000_000)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixTime).UtcDateTime;
            }
            if (unixTime > 100_000_000)
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime;
            }
        }

        // Parse ISO hoặc date string
        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        return null;
    }
}
