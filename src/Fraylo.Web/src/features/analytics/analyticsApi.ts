import { bridgeClient } from "../../services/bridgeClient";
import type {
  AnalyticsSummary,
  AnalyticsTimeseriesPoint,
  VideoMetricRecord,
  AnalyticsImportResult,
  AnalyticsExportResult,
  TikTokCheckSessionResult,
  TikTokOpenLoginResult,
  TikTokSyncChannelResult,
} from "./analyticsTypes";

export interface TikTokAnalyticsAccount {
  accountId: string;
  accountIdentity: string;
  profileId: string;
  sessionStatus: string;
}

export async function listTikTokAnalyticsAccounts(): Promise<TikTokAnalyticsAccount[]> {
  const result = await bridgeClient.invoke<{ accounts: TikTokAnalyticsAccount[] }>(
    "analytics.list_tiktok_accounts",
    {},
  );
  return result.accounts ?? [];
}

export async function getAnalyticsSummary(params: {
  accountId: string;
  periodDays?: number;
  startDate?: string;
  endDate?: string;
}): Promise<AnalyticsSummary> {
  return await bridgeClient.invoke<AnalyticsSummary>(
    "analytics.get_summary",
    params,
  );
}

export async function getAnalyticsTimeseries(params: {
  accountId: string;
  metric: string;
  dataType?: "overview" | "content" | "viewers";
  periodDays?: number;
}): Promise<AnalyticsTimeseriesPoint[]> {
  const res = await bridgeClient.invoke<
    AnalyticsTimeseriesPoint[] | { points?: AnalyticsTimeseriesPoint[] }
  >(
    "analytics.get_timeseries",
    params,
  );
  if (Array.isArray(res)) return res;
  if (res && Array.isArray((res as { points?: AnalyticsTimeseriesPoint[] }).points)) {
    return (res as { points: AnalyticsTimeseriesPoint[] }).points;
  }
  return [];
}

export async function listTopVideos(params: {
  accountId: string;
  sortBy?: string;
  limit?: number;
}): Promise<VideoMetricRecord[]> {
  const res = await bridgeClient.invoke<
    VideoMetricRecord[] | { videos?: VideoMetricRecord[] }
  >(
    "analytics.list_top_videos",
    params,
  );
  if (Array.isArray(res)) return res;
  if (res && Array.isArray((res as { videos?: VideoMetricRecord[] }).videos)) {
    return (res as { videos: VideoMetricRecord[] }).videos;
  }
  return [];
}

export async function importAnalyticsData(params: {
  accountId: string;
  format: string;
  rawContent: string;
  source?: string;
}): Promise<AnalyticsImportResult> {
  return await bridgeClient.invoke<AnalyticsImportResult>(
    "analytics.import_data",
    params,
  );
}

export async function exportAnalyticsData(params: {
  accountId: string;
  format?: string;
  periodDays?: number;
}): Promise<AnalyticsExportResult> {
  return await bridgeClient.invoke<AnalyticsExportResult>(
    "analytics.export_data",
    params,
  );
}

export async function checkTikTokSession(
  accountId: string
): Promise<TikTokCheckSessionResult> {
  return await bridgeClient.invoke<TikTokCheckSessionResult>(
    "analytics.check_tiktok_session",
    { accountId },
    { timeoutMs: 45000 }
  );
}

export async function openTikTokLogin(
  accountId: string,
  targetUrl?: string
): Promise<TikTokOpenLoginResult> {
  return await bridgeClient.invoke<TikTokOpenLoginResult>(
    "analytics.open_tiktok_login",
    { accountId, targetUrl },
    { timeoutMs: 60000 }
  );
}

export async function syncTikTokChannel(
  accountId: string,
  forceOpenBrowser?: boolean
): Promise<TikTokSyncChannelResult> {
  return await bridgeClient.invoke<TikTokSyncChannelResult>(
    "analytics.sync_tiktok_channel",
    { accountId, forceOpenBrowser },
    { timeoutMs: 240000 }
  );
}

