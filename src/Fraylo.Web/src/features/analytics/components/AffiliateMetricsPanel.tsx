import {
  ShoppingBag,
  TrendingUp,
  MousePointerClick,
  Store,
  Upload,
  Info,
} from "lucide-react";
import { Card, CardHeader, CardTitle, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import type { AnalyticsSummary } from "../analyticsTypes";
import { formatNumber, formatCurrency } from "./AnalyticsKpiCards";

interface AffiliateMetricsPanelProps {
  summary: AnalyticsSummary | null;
  loading?: boolean;
  onImportClick?: () => void;
}

export function AffiliateMetricsPanel({
  summary,
  loading = false,
  onImportClick,
}: AffiliateMetricsPanelProps) {
  if (loading) {
    return (
      <Card className="bg-card border-border flex flex-col h-full animate-pulse shadow-xs" data-testid="affiliate-loading">
        <CardHeader className="p-4 border-b border-border/50">
          <div className="h-5 bg-muted rounded w-1/2" />
        </CardHeader>
        <CardContent className="p-4 space-y-4">
          <div className="h-16 bg-muted rounded" />
          <div className="h-16 bg-muted rounded" />
        </CardContent>
      </Card>
    );
  }

  // Check if both GMV and affiliate clicks are null/undefined (No affiliate data)
  const hasAffiliateData =
    summary &&
    (summary.totalGmvAmount !== null || summary.totalAffiliateClicks !== null);

  return (
    <Card className="bg-card border-border flex flex-col h-full shadow-xs" data-testid="affiliate-panel">
      <CardHeader className="p-4 pb-3 flex flex-row items-center justify-between border-b border-border/50">
        <div className="flex items-center gap-2">
          <div className="p-1.5 rounded-md bg-teal-500/10 text-teal-600 dark:text-teal-400">
            <ShoppingBag className="w-4 h-4" />
          </div>
          <div>
            <CardTitle className="text-base font-semibold text-foreground">
              Thương mại & Affiliate
            </CardTitle>
            <p className="text-xs text-muted-foreground">
              TikTok Shop & Tiếp thị liên kết
            </p>
          </div>
        </div>
      </CardHeader>

      <CardContent className="p-4 flex-1 flex flex-col justify-between">
        {!hasAffiliateData ? (
          <div
            className="flex-1 flex flex-col items-center justify-center text-center p-4 border border-dashed border-border/80 rounded-lg space-y-3"
            data-testid="affiliate-empty"
          >
            <div className="w-10 h-10 rounded-full bg-muted/70 flex items-center justify-center text-muted-foreground">
              <Store className="w-5 h-5" />
            </div>
            <div className="space-y-1 max-w-[240px]">
              <h4 className="text-sm font-semibold text-foreground">
                Chưa có liên kết Affiliate / TikTok Shop
              </h4>
              <p className="text-xs text-muted-foreground leading-relaxed">
                Các chỉ số doanh thu GMV và hoa hồng sẽ xuất hiện khi bạn liên kết TikTok Shop Seller Center hoặc nhập tệp báo cáo bán hàng.
              </p>
            </div>
            {onImportClick && (
              <Button
                variant="outline"
                size="sm"
                onClick={onImportClick}
                className="text-xs gap-1.5 mt-2"
                data-testid="affiliate-import-btn"
              >
                <Upload className="w-3.5 h-3.5" />
                <span>Nhập dữ liệu bán hàng</span>
              </Button>
            )}
          </div>
        ) : (
          <div className="space-y-4 flex-1 flex flex-col justify-center">
            {/* GMV Block */}
            <div className="p-3.5 rounded-lg bg-muted/40 border border-border/60">
              <div className="flex items-center justify-between">
                <span className="text-xs font-medium text-muted-foreground">
                  Tổng doanh thu GMV
                </span>
                <TrendingUp className="w-4 h-4 text-teal-500" />
              </div>
              <div
                className="text-2xl font-bold tracking-tight text-foreground mt-1"
                data-testid="affiliate-gmv-value"
              >
                {formatCurrency(summary.totalGmvAmount, summary.currency)}
              </div>
              <p className="text-[11px] text-muted-foreground mt-0.5">
                Doanh số phát sinh từ gắn thẻ sản phẩm
              </p>
            </div>

            {/* Affiliate Clicks Block */}
            <div className="p-3.5 rounded-lg bg-muted/40 border border-border/60">
              <div className="flex items-center justify-between">
                <span className="text-xs font-medium text-muted-foreground">
                  Lượt click sản phẩm / Affiliate
                </span>
                <MousePointerClick className="w-4 h-4 text-warning" />
              </div>
              <div
                className="text-2xl font-bold tracking-tight text-foreground mt-1"
                data-testid="affiliate-clicks-value"
              >
                {formatNumber(summary.totalAffiliateClicks)}
              </div>
              <p className="text-[11px] text-muted-foreground mt-0.5">
                Lượt click vào giỏ hàng hoặc link tiếp thị
              </p>
            </div>

            {/* Metadata Footer */}
            <div className="pt-2 border-t border-border/40 text-[11px] text-muted-foreground space-y-1">
              <div className="flex items-center justify-between">
                <span>Nguồn dữ liệu:</span>
                <span className="font-medium text-foreground">
                  {summary.source || "Báo cáo nhập (CSV/JSON)"}
                </span>
              </div>
              {summary.lastCapturedAtUtc && (
                <div className="flex items-center justify-between">
                  <span>Cập nhật lần cuối:</span>
                  <span className="font-mono">
                    {new Date(summary.lastCapturedAtUtc).toLocaleString("vi-VN")}
                  </span>
                </div>
              )}
            </div>
          </div>
        )}

        <div className="mt-4 flex items-start gap-1.5 p-2 rounded-md bg-muted/20 text-[11px] text-muted-foreground">
          <Info className="w-3.5 h-3.5 shrink-0 mt-0.5 text-primary" />
          <span>
            Chỉ số thương mại TikTok tuân thủ chính sách bảo mật Creator Marketplace & TikTok Shop.
          </span>
        </div>
      </CardContent>
    </Card>
  );
}
