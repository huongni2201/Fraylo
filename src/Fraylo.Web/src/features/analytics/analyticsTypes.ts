/**
 * Fraylo — TikTok Analytics & Affiliate Dashboard Type Definitions
 * Phù hợp với UI_BROWSER_PLAN §3.6 và Bridge RPC contracts
 */

export interface AnalyticsSummary {
  periodDays: number | null;
  startDate: string | null;
  endDate: string | null;
  totalVideos: number | null;
  totalViews: number | null;
  totalProfileViews?: number | null;
  totalLikes: number | null;
  totalComments: number | null;
  totalShares: number | null;
  averageEngagementRate: number | null;
  totalGmvAmount: number | null;
  currency: string | null;
  totalAffiliateClicks: number | null;
  lastCapturedAtUtc: string | null;
  source: string | null;
}

export interface AnalyticsTimeseriesPoint {
  date: string; // YYYY-MM-DD
  metric: string;
  value: number | null;
}

export interface VideoMetricRecord {
  id: string;
  snapshotId?: string | null;
  accountId?: string | null;
  videoKey: string;
  title: string;
  views: number | null;
  likes: number | null;
  comments: number | null;
  shares: number | null;
  watchTimeAvgSec: number | null;
  gmvAmount: number | null;
  affiliateClicks: number | null;
  publishedAtUtc: string | null;
  capturedAtUtc: string | null;
}

export interface AnalyticsImportResult {
  snapshotId: string;
  recordCount: number;
  success: boolean;
  message: string;
  periodStart: string | null;
  periodEnd: string | null;
  capturedAtUtc: string;
}

export interface AnalyticsExportResult {
  format: string;
  content: string;
  fileName: string;
}

export type AnalyticsPeriod = 7 | 30 | 90 | 0; // 0 tương ứng với "Tất cả"

export type AnalyticsDataType = "overview" | "content" | "viewers";
export type AnalyticsMetricType =
  | "views" | "video_views" | "profile_views" | "likes" | "comments" | "shares"
  | "total_viewers" | "new_viewers" | "returning_viewers";
export type VideoSortField = "views" | "likes" | "comments" | "shares" | "gmvAmount";

export interface TikTokCheckSessionResult {
  isLoggedIn: boolean;
  profileId: string | null;
  accountIdentity: string | null;
  sessionStatus: string;
  errorMessage: string | null;
}

export interface TikTokOpenLoginResult {
  success: boolean;
  profileId: string;
  visibilityState: string;
  message: string;
}

export interface TikTokSyncChannelResult {
  success: boolean;
  snapshotId: string | null;
  recordCount: number;
  channelHandle: string | null;
  message: string;
  requiresLogin: boolean;
  profileId: string | null;
  overviewRecordCount?: number;
  contentRecordCount?: number;
  viewersRecordCount?: number;
}

