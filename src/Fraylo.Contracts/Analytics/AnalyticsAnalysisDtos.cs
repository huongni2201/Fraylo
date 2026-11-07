namespace Fraylo.Contracts.Analytics;

public sealed record AnalyticsAnalysisRequest(string Platform, string AccountId, string? PageId = null,
    string? SourceId = null, string? ReportId = null, string? ClientRequestId = null, int Page = 1);

public sealed record AnalyticsAnalysisFinding(string Text, string[] Evidence);
public sealed record AnalyticsAnalysisResult(int SchemaVersion, string Summary, AnalyticsAnalysisFinding[] Highlights,
    AnalyticsAnalysisFinding[] Improvements, AnalyticsAnalysisFinding[] Recommendations, string[] Limitations);
