using Fraylo.Contracts.Facebook;

namespace Fraylo.Contracts.FacebookAnalytics;

public sealed record FacebookAnalyticsRequest(string AccountId, string? PageId = null, string? OperationId = null,
    string? ContentId = null, int Page = 1, string? Search = null, string? Kind = null, string Sort = "views", int Days = 0);
public sealed record FacebookMetric(double? Value, string Raw, bool Approximate = false, string? MissingReason = null);
public sealed record FacebookAnalyticsAccount(string AccountId, string Name, string SessionStatus, bool Enabled);
public sealed record FacebookAnalyticsPageDto(string Id, string AccountId, string FacebookPageId, string Name, string Url);
public sealed record FacebookContentDto(string ContentId, string Kind, string Title, string Url, string PublishedLabel,
    string? PublishedDate, IReadOnlyDictionary<string, FacebookMetric> Metrics);
public sealed record FacebookCaptureDto(string Id, string PageId, string Status, string StartedAtUtc, string? FinishedAtUtc,
    string Message, int RecordCount, string Source, string Scope, IReadOnlyList<FacebookActivityEntry>? ActivityLog = null);
public sealed record FacebookAnalyticsCatalog(IReadOnlyList<FacebookAnalyticsAccount> Accounts, IReadOnlyList<FacebookAnalyticsPageDto> Pages);
public sealed record FacebookAnalyticsDashboard(FacebookCaptureDto? Capture, FacebookCaptureDto? LatestAttempt,
    IReadOnlyList<FacebookContentDto> Content, int TotalCount, int Page,
    IReadOnlyDictionary<string, FacebookMetricTotal> Totals, IReadOnlyList<FacebookCaptureDto> Captures);
public sealed record FacebookMetricTotal(double? Value, int Available, int Total, bool Approximate);
public sealed record FacebookContentObservation(string CapturedAtUtc, string CaptureId, FacebookContentDto Content);
