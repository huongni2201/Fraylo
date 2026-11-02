import { useState, useEffect, useCallback, useRef } from "react";
import {
  Download,
  Upload,
  RefreshCw,
  AlertTriangle,
  FileSpreadsheet,
  UserRound,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import type {
  AnalyticsSummary,
  AnalyticsTimeseriesPoint,
  VideoMetricRecord,
  AnalyticsPeriod,
  AnalyticsDataType,
  AnalyticsMetricType,
  VideoSortField,
} from "./analyticsTypes";
import {
  getAnalyticsSummary,
  getAnalyticsTimeseries,
  listTopVideos,
  exportAnalyticsData,
  listTikTokAnalyticsAccounts,
  type TikTokAnalyticsAccount,
} from "./analyticsApi";
import { AnalyticsKpiCards } from "./components/AnalyticsKpiCards";
import { AnalyticsChart } from "./components/AnalyticsChart";
import { AffiliateMetricsPanel } from "./components/AffiliateMetricsPanel";
import { TopVideosTable } from "./components/TopVideosTable";
import { AnalyticsImportDialog } from "./components/AnalyticsImportDialog";
import { TikTokSyncDialog } from "./components/TikTokSyncDialog";

const periodOptions: { value: AnalyticsPeriod; label: string }[] = [
  { value: 7, label: "7 ngày qua" },
  { value: 30, label: "30 ngày qua" },
  { value: 90, label: "90 ngày qua" },
  { value: 0, label: "Tất cả" },
];

const datasetOptions: { value: AnalyticsDataType; label: string }[] = [
  { value: "overview", label: "Tổng quan" },
  { value: "content", label: "Nội dung" },
  { value: "viewers", label: "Người xem" },
];

const metricOptions: Record<AnalyticsDataType, AnalyticsMetricType[]> = {
  overview: ["views", "profile_views", "likes", "comments", "shares"],
  content: ["views", "likes", "comments", "shares"],
  viewers: ["total_viewers", "new_viewers", "returning_viewers"],
};

export function AnalyticsPage({ active }: { active: boolean }) {
  const [periodDays, setPeriodDays] = useState<AnalyticsPeriod>(30);
  const [selectedDataset, setSelectedDataset] = useState<AnalyticsDataType>("overview");
  const [selectedMetric, setSelectedMetric] = useState<AnalyticsMetricType>("views");
  const [sortBy, setSortBy] = useState<VideoSortField>("views");

  const [summary, setSummary] = useState<AnalyticsSummary | null>(null);
  const [timeseries, setTimeseries] = useState<AnalyticsTimeseriesPoint[]>([]);
  const [topVideos, setTopVideos] = useState<VideoMetricRecord[]>([]);

  const [loading, setLoading] = useState(false);
  const [timeseriesLoading, setTimeseriesLoading] = useState(false);
  const [videosLoading, setVideosLoading] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [importDialogOpen, setImportDialogOpen] = useState(false);
  const [syncDialogOpen, setSyncDialogOpen] = useState(false);
  const [accounts, setAccounts] = useState<TikTokAnalyticsAccount[]>([]);
  const [accountsLoading, setAccountsLoading] = useState(true);
  const [accountsError, setAccountsError] = useState<string | null>(null);
  const [selectedAccount, setSelectedAccount] = useState<TikTokAnalyticsAccount | null>(null);
  const accountId = selectedAccount?.accountId ?? null;
  // Số thứ tự yêu cầu mới nhất của từng loader: phản hồi của yêu cầu cũ (đổi tài khoản/kỳ) bị bỏ qua.
  const summarySeq = useRef(0);
  const timeseriesSeq = useRef(0);
  const videosSeq = useRef(0);

  const handleSelectAccount = (account: TikTokAnalyticsAccount) => {
    setSummary(null);
    setTimeseries([]);
    setTopVideos([]);
    setError(null);
    setSelectedAccount(account);
  };

  const loadAccounts = useCallback(async () => {
    setAccountsLoading(true);
    setAccountsError(null);
    try {
      setAccounts(await listTikTokAnalyticsAccounts());
    } catch (err: unknown) {
      setAccountsError(err instanceof Error ? err.message : "Không thể tải tài khoản TikTok.");
    } finally {
      setAccountsLoading(false);
    }
  }, []);

  useEffect(() => {
    if (!active) {
      setSelectedAccount(null);
      return;
    }
    void loadAccounts();
  }, [active, loadAccounts]);

  // Load Main Summary Data
  const loadSummaryData = useCallback(async () => {
    const seq = ++summarySeq.current;
    if (!accountId) return;
    setLoading(true);
    setError(null);
    try {
      const summaryRes = await getAnalyticsSummary({
        accountId,
        periodDays: periodDays > 0 ? periodDays : undefined,
      });
      if (seq !== summarySeq.current) return;
      setSummary(summaryRes);
    } catch (err: unknown) {
      if (seq !== summarySeq.current) return;
      const msg = err instanceof Error ? err.message : "Không thể tải báo cáo tổng hợp.";
      setError(msg);
    } finally {
      if (seq === summarySeq.current) setLoading(false);
    }
  }, [accountId, periodDays]);

  // Load Timeseries Data
  const loadTimeseriesData = useCallback(async () => {
    const seq = ++timeseriesSeq.current;
    if (!accountId) return;
    setTimeseriesLoading(true);
    try {
      const tsRes = await getAnalyticsTimeseries({
        accountId,
        metric: selectedMetric,
        dataType: selectedDataset,
        periodDays: periodDays > 0 ? periodDays : undefined,
      });
      if (seq !== timeseriesSeq.current) return;
      setTimeseries(tsRes || []);
    } catch {
      if (seq !== timeseriesSeq.current) return;
      setTimeseries([]);
    } finally {
      if (seq === timeseriesSeq.current) setTimeseriesLoading(false);
    }
  }, [accountId, periodDays, selectedMetric, selectedDataset]);

  // Load Top Videos Data
  const loadVideosData = useCallback(async () => {
    const seq = ++videosSeq.current;
    if (!accountId) return;
    setVideosLoading(true);
    try {
      const vidsRes = await listTopVideos({
        accountId,
        sortBy,
        limit: 15,
      });
      if (seq !== videosSeq.current) return;
      setTopVideos(vidsRes || []);
    } catch {
      if (seq !== videosSeq.current) return;
      setTopVideos([]);
    } finally {
      if (seq === videosSeq.current) setVideosLoading(false);
    }
  }, [accountId, sortBy]);

  // Load data only after the user selects an account.
  useEffect(() => {
    if (accountId) {
      loadSummaryData();
      loadTimeseriesData();
      loadVideosData();
    } else {
      setSummary(null);
      setTimeseries([]);
      setTopVideos([]);
    }
  }, [accountId, periodDays, loadSummaryData, loadTimeseriesData, loadVideosData]);

  // Reload timeseries when dataset or metric changes
  useEffect(() => {
    if (accountId) {
      loadTimeseriesData();
    }
  }, [accountId, selectedDataset, selectedMetric, loadTimeseriesData]);

  const handleDatasetChange = (dataset: AnalyticsDataType) => {
    setSelectedDataset(dataset);
    setSelectedMetric(metricOptions[dataset][0]);
  };

  // Reload videos when sort changes
  useEffect(() => {
    if (accountId) {
      loadVideosData();
    }
  }, [accountId, sortBy, loadVideosData]);

  const handleRefreshAll = () => {
    loadSummaryData();
    loadTimeseriesData();
    loadVideosData();
  };

  const handleExportCsv = async () => {
    if (!accountId) return;
    setExporting(true);
    try {
      const res = await exportAnalyticsData({
        accountId,
        format: "csv",
        periodDays: periodDays > 0 ? periodDays : undefined,
      });

      if (res && res.content) {
        const blob = new Blob([res.content], { type: "text/csv;charset=utf-8;" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = res.fileName || `analytics_${accountId}_${periodDays}d.csv`;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(url);
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Lỗi khi xuất tệp CSV.";
      alert(msg);
    } finally {
      setExporting(false);
    }
  };

  // Chọn TikTok identity trước khi tải Analytics theo account.
  if (!selectedAccount) {
    return (
      <div className="flex-1 flex flex-col items-center justify-center p-8 min-h-[60vh]" data-testid="analytics-account-picker">
        <div className="w-full max-w-2xl space-y-5">
          <div className="text-center space-y-2">
            <div className="mx-auto w-12 h-12 rounded-2xl bg-primary/10 flex items-center justify-center text-primary">
              <UserRound className="w-6 h-6" />
            </div>
            <h1 className="text-xl font-semibold text-foreground">Chọn tài khoản TikTok</h1>
            <p className="text-sm text-muted-foreground">Chọn tài khoản để mở báo cáo Analytics và dữ liệu của tài khoản đó.</p>
          </div>

          {accountsLoading ? (
            <p className="text-center text-sm text-muted-foreground">Đang tải tài khoản...</p>
          ) : accountsError ? (
            <div className="text-center space-y-3">
              <p className="text-sm text-destructive">{accountsError}</p>
              <Button variant="outline" onClick={() => void loadAccounts()}>Thử lại</Button>
            </div>
          ) : accounts.length === 0 ? (
            <div className="rounded-xl border border-dashed border-border p-6 text-center space-y-2">
              <p className="text-sm font-medium text-foreground">Chưa có tài khoản TikTok</p>
              <p className="text-xs text-muted-foreground">Thêm và đăng nhập tài khoản TikTok trong Cài đặt → Tài khoản, rồi quay lại đây.</p>
            </div>
          ) : (
            <div className="grid gap-3 sm:grid-cols-2">
              {accounts.map((account) => (
                <Button
                  key={account.accountId}
                  type="button"
                  variant="outline"
                  onClick={() => handleSelectAccount(account)}
                  className="h-auto min-h-16 justify-start gap-3 whitespace-normal px-4 py-3 text-left"
                  data-testid={`analytics-select-account-${account.accountId}`}
                >
                  <UserRound className="size-4 shrink-0 text-primary" />
                  <span className="min-w-0 flex-1">
                    <span className="block truncate font-semibold">{account.accountIdentity}</span>
                    <span className="block text-xs font-normal text-muted-foreground">Phiên: {account.sessionStatus}</span>
                  </span>
                </Button>
              ))}
            </div>
          )}
        </div>
      </div>
    );
  }

  const isAccountEmpty = Boolean(
    !loading &&
    summary &&
    (summary.totalViews === null && !summary.lastCapturedAtUtc) &&
    topVideos.length === 0
  );

  return (
    <div className="space-y-6" data-testid="analytics-page">
      {/* Page Header */}
      <div className="flex flex-col lg:flex-row lg:items-center justify-between gap-4">
        <div>
          <div className="flex items-center gap-2.5">
            <h1 className="text-2xl font-bold tracking-tight text-foreground">
              TikTok Analytics & Affiliate
            </h1>
          </div>
          <div className="mt-2 flex items-center gap-2 text-xs text-muted-foreground">
            <span>Đang xem:</span>
            <strong className="font-semibold text-foreground">{selectedAccount.accountIdentity}</strong>
            <Button type="button" variant="ghost" size="sm" className="h-6 px-2 text-xs" onClick={() => setSelectedAccount(null)}>
              Đổi tài khoản
            </Button>
          </div>
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1 mt-1 text-xs text-muted-foreground">
            {summary?.lastCapturedAtUtc && (
              <>
                <span>•</span>
                <span>
                  Cập nhật: {new Date(summary.lastCapturedAtUtc).toLocaleString("vi-VN")}
                </span>
              </>
            )}
            {summary?.source && (
              <>
                <span>•</span>
                <span className="capitalize">Nguồn: {summary.source}</span>
              </>
            )}
          </div>
        </div>

        {/* Action Controls */}
        <div className="flex flex-wrap items-center gap-2">
          {/* Period Filter */}
          <div className="flex items-center bg-muted/60 p-1 rounded-lg border border-border/60">
            {periodOptions.map((opt) => (
              <Button
                key={opt.value}
                variant={periodDays === opt.value ? "secondary" : "ghost"}
                size="sm"
                className={`h-7 px-2.5 text-xs font-medium rounded-md transition-[color,background-color,border-color,box-shadow,opacity,transform] ${
                  periodDays === opt.value
                    ? "bg-background text-foreground shadow-xs font-semibold"
                    : "text-muted-foreground hover:text-foreground"
                }`}
                onClick={() => setPeriodDays(opt.value)}
                data-testid={`period-filter-${opt.value}`}
              >
                {opt.label}
              </Button>
            ))}
          </div>

          {/* TikTok Sync Button */}
          <Button
            onClick={() => setSyncDialogOpen(true)}
            className="gap-2 bg-primary hover:bg-primary/90 text-primary-foreground shadow-xs text-xs h-9 font-semibold"
            data-testid="sync-tiktok-btn"
          >
            <RefreshCw className="w-3.5 h-3.5" />
            <span>Kéo dữ liệu từ kênh TikTok</span>
          </Button>

          {/* Import Button */}
          <Button
            variant="outline"
            size="sm"
            onClick={() => setImportDialogOpen(true)}
            className="text-xs gap-1.5 h-9"
            data-testid="open-import-dialog-btn"
          >
            <Upload className="w-3.5 h-3.5 text-primary" />
            <span>Nhập dữ liệu</span>
          </Button>

          {/* Export CSV Button */}
          <Button
            variant="outline"
            size="sm"
            onClick={handleExportCsv}
            disabled={exporting || isAccountEmpty}
            className="text-xs gap-1.5 h-9"
            data-testid="export-csv-btn"
          >
            <Download className="w-3.5 h-3.5" />
            <span>{exporting ? "Đang xuất..." : "Xuất CSV"}</span>
          </Button>

          {/* Refresh Button */}
          <Button
            variant="ghost"
            size="sm"
            onClick={handleRefreshAll}
            disabled={loading}
            className="h-9 w-9 p-0"
            title="Làm mới dữ liệu"
            data-testid="refresh-btn"
          >
            <RefreshCw className={`w-3.5 h-3.5 ${loading ? "animate-spin" : ""}`} />
          </Button>
        </div>
      </div>

      {/* Global Error Banner */}
      {error && (
        <div className="flex items-center justify-between p-3.5 rounded-lg bg-destructive/10 border border-destructive/20 text-xs text-destructive">
          <div className="flex items-center gap-2">
            <AlertTriangle className="w-4 h-4 shrink-0" />
            <span>{error}</span>
          </div>
          <Button
            variant="ghost"
            size="sm"
            onClick={handleRefreshAll}
            className="text-xs h-7 px-2 hover:bg-destructive/15 text-destructive"
          >
            Thử lại
          </Button>
        </div>
      )}

      {/* 2. Empty State: selected TikTok account has no data */}
      <div className="flex items-center gap-1 rounded-lg border border-border/60 bg-muted/40 p-1 w-fit" role="tablist" aria-label="Nhóm dữ liệu phân tích">
        {datasetOptions.map((dataset) => (
          <Button
            key={dataset.value}
            type="button"
            role="tab"
            aria-selected={selectedDataset === dataset.value}
            variant={selectedDataset === dataset.value ? "secondary" : "ghost"}
            size="sm"
            className="h-8 px-3 text-xs"
            onClick={() => handleDatasetChange(dataset.value)}
            data-testid={`analytics-dataset-${dataset.value}`}
          >
            {dataset.label}
          </Button>
        ))}
      </div>

      {isAccountEmpty ? (
        <div
          className="flex flex-col items-center justify-center p-12 text-center border-2 border-dashed border-border/80 rounded-2xl bg-card/40 my-8 space-y-4 shadow-xs"
          data-testid="analytics-account-empty-state"
        >
          <div className="w-16 h-16 rounded-2xl bg-primary/10 text-primary flex items-center justify-center">
            <FileSpreadsheet className="w-8 h-8" />
          </div>
          <div className="space-y-1.5 max-w-md">
            <h3 className="text-lg font-semibold text-foreground">
              Chưa có dữ liệu phân tích cho kênh này
            </h3>
            <p className="text-xs text-muted-foreground leading-relaxed">
              Bạn có thể nhập tệp xuất từ TikTok Analytics hoặc TikTok Studio (CSV/JSON) để theo dõi xu hướng lượt xem, tương tác và hiệu quả bán hàng affiliate.
            </p>
          </div>
          <div className="flex flex-wrap items-center justify-center gap-3">
            <Button
              onClick={() => setSyncDialogOpen(true)}
              className="gap-2 text-xs font-semibold bg-primary hover:bg-primary/90 text-primary-foreground shadow-xs"
              data-testid="empty-state-sync-cta"
            >
              <RefreshCw className="w-4 h-4" />
              <span>Kéo dữ liệu từ kênh TikTok</span>
            </Button>
            <Button
              variant="outline"
              onClick={() => setImportDialogOpen(true)}
              className="gap-2 text-xs font-semibold"
              data-testid="empty-state-import-cta"
            >
              <Upload className="w-4 h-4" />
              <span>Nhập dữ liệu phân tích ngay</span>
            </Button>
          </div>
        </div>
      ) : (
        /* Normal Dashboard Layout */
        <div className="space-y-6">
          {/* Top Row: KPI Cards */}
          {selectedDataset !== "viewers" && (
            <section aria-label="Chỉ số hiệu suất chính">
              <AnalyticsKpiCards summary={summary} loading={loading} />
            </section>
          )}

          {/* Middle Row: Trend Chart (2 cols) + Affiliate Panel (1 col) */}
          <section className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            <div className={selectedDataset === "content" ? "lg:col-span-2 min-h-[320px]" : "lg:col-span-3 min-h-[320px]"}>
              <AnalyticsChart
                data={timeseries}
                currentMetric={selectedMetric}
                onMetricChange={setSelectedMetric}
                metricOptions={metricOptions[selectedDataset]}
                loading={timeseriesLoading}
              />
            </div>
            {selectedDataset === "content" && (
              <div className="lg:col-span-1 min-h-[320px]">
                <AffiliateMetricsPanel
                  summary={summary}
                  loading={loading}
                  onImportClick={() => setImportDialogOpen(true)}
                />
              </div>
            )}
          </section>

          {/* Bottom Row: Top Videos Table */}
          {selectedDataset === "content" && (
            <section aria-label="Danh sách video hiệu quả">
              <TopVideosTable
                videos={topVideos}
                loading={videosLoading}
                sortBy={sortBy}
                onSortChange={setSortBy}
              />
            </section>
          )}
        </div>
      )}

      {/* Import Modal Dialog */}
      <AnalyticsImportDialog
        open={importDialogOpen}
        onOpenChange={setImportDialogOpen}
        accountId={selectedAccount.accountId}
        onSuccess={handleRefreshAll}
      />

      {/* TikTok Live Sync Dialog */}
      <TikTokSyncDialog
        open={syncDialogOpen}
        onOpenChange={setSyncDialogOpen}
        accountId={selectedAccount.accountId}
        onSyncSuccess={handleRefreshAll}
      />
    </div>
  );
}
