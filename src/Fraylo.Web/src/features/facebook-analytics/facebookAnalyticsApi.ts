import type { FacebookActivityEntry } from "../facebook/facebookApi";
import { bridgeClient } from "../../services/bridgeClient";

export interface FacebookMetric { value: number | null; raw: string; approximate: boolean; missingReason: string | null }
export interface FacebookAnalyticsAccount { accountId: string; name: string; sessionStatus: string; enabled: boolean }
export interface FacebookAnalyticsPageItem { id: string; accountId: string; facebookPageId: string; name: string; url: string }
export interface FacebookContent { contentId: string; kind: string; title: string; url: string; publishedLabel: string; publishedDate: string | null; metrics: Record<string, FacebookMetric> }
export interface FacebookCapture { id: string; pageId: string; status: string; startedAtUtc: string; finishedAtUtc: string | null; message: string; recordCount: number; source: string; scope: string; activityLog?: FacebookActivityEntry[] }
export interface FacebookCatalog { accounts: FacebookAnalyticsAccount[]; pages: FacebookAnalyticsPageItem[] }
export interface FacebookDashboard {
  capture: FacebookCapture | null; latestAttempt: FacebookCapture | null; content: FacebookContent[]; totalCount: number; page: number;
  totals: Record<string, { value: number | null; available: number; total: number; approximate: boolean }>; captures: FacebookCapture[];
}
export interface FacebookObservation { capturedAtUtc: string; captureId: string; content: FacebookContent }
export interface FacebookQuery { accountId: string; pageId: string; page?: number; search?: string; kind?: string; sort?: string; days?: number }
export interface FacebookAnalyticsApi {
  catalog(): Promise<FacebookCatalog>;
  syncPages(accountId: string): Promise<FacebookCatalog>;
  dashboard(query: FacebookQuery): Promise<FacebookDashboard>;
  sync(query: FacebookQuery, operationId: string): Promise<FacebookCapture>;
  cancel(query: FacebookQuery, operationId: string): Promise<unknown>;
  history(query: FacebookQuery, contentId: string): Promise<FacebookObservation[]>;
}
export const facebookAnalyticsApi: FacebookAnalyticsApi = {
  catalog: () => bridgeClient.invoke("facebook.analytics.catalog", {}),
  syncPages: accountId => bridgeClient.invoke("facebook.analytics.pages.sync", { accountId }, { timeoutMs: 150000 }),
  dashboard: query => bridgeClient.invoke("facebook.analytics.dashboard", query),
  sync: (query, operationId) => bridgeClient.invoke("facebook.analytics.sync", { ...query, operationId }),
  cancel: (query, operationId) => bridgeClient.invoke("facebook.analytics.cancel", { ...query, operationId }),
  history: (query, contentId) => bridgeClient.invoke("facebook.analytics.history", { ...query, contentId }),
};
export const metricLabels: Record<string, string> = { views: "Lượt xem", reactions: "Cảm xúc", comments: "Bình luận", shares: "Chia sẻ", averageWatchSeconds: "Thời gian xem trung bình", totalWatchSeconds: "Tổng thời gian xem" };
export const formatMetric = (metric?: Pick<FacebookMetric, "value" | "approximate">) => metric?.value == null ? "—" : `${metric.approximate ? "≈ " : ""}${metric.value.toLocaleString("vi-VN")}`;
