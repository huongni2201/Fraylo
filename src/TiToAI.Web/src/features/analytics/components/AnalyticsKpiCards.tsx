import React from "react";
import {
  Eye,
  Heart,
  MessageSquare,
  Share2,
  Video,
  Percent,
  ShoppingBag,
  MousePointerClick,
} from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import type { AnalyticsSummary } from "../analyticsTypes";

interface AnalyticsKpiCardsProps {
  summary: AnalyticsSummary | null;
  loading?: boolean;
}

export function formatNumber(val: number | null | undefined): string {
  if (val === null || val === undefined) return "—";
  return new Intl.NumberFormat("vi-VN").format(val);
}

export function formatPercentage(val: number | null | undefined): string {
  if (val === null || val === undefined) return "—";
  return `${new Intl.NumberFormat("vi-VN", { maximumFractionDigits: 2 }).format(val)}%`;
}

export function formatCurrency(
  val: number | null | undefined,
  currency?: string | null
): string {
  if (val === null || val === undefined) return "—";
  const curr = currency || "₫";
  return `${new Intl.NumberFormat("vi-VN").format(val)} ${curr}`;
}

interface KpiItem {
  id: string;
  label: string;
  value: string;
  subtext?: string;
  icon: React.ComponentType<{ className?: string }>;
  accentColor: string;
}

export function AnalyticsKpiCards({ summary, loading = false }: AnalyticsKpiCardsProps) {
  if (loading) {
    return (
      <div className="grid grid-cols-2 sm:grid-cols-2 md:grid-cols-4 gap-4" data-testid="kpi-skeleton">
        {Array.from({ length: 8 }).map((_, i) => (
          <Card key={i} className="animate-pulse bg-card border-border">
            <CardContent className="p-4 space-y-2">
              <div className="h-4 bg-muted rounded w-1/2" />
              <div className="h-7 bg-muted rounded w-3/4" />
            </CardContent>
          </Card>
        ))}
      </div>
    );
  }

  const items: KpiItem[] = [
    {
      id: "views",
      label: "Lượt xem",
      value: formatNumber(summary?.totalViews),
      subtext: summary?.periodDays ? `Trong ${summary.periodDays} ngày` : undefined,
      icon: Eye,
      accentColor: "text-info",
    },
    {
      id: "likes",
      label: "Lượt thích",
      value: formatNumber(summary?.totalLikes),
      icon: Heart,
      accentColor: "text-rose-500",
    },
    {
      id: "comments",
      label: "Bình luận",
      value: formatNumber(summary?.totalComments),
      icon: MessageSquare,
      accentColor: "text-warning",
    },
    {
      id: "shares",
      label: "Chia sẻ",
      value: formatNumber(summary?.totalShares),
      icon: Share2,
      accentColor: "text-success",
    },
    {
      id: "engagement",
      label: "Tỷ lệ tương tác",
      value: formatPercentage(summary?.averageEngagementRate),
      subtext: "Likes + Comments / Views",
      icon: Percent,
      accentColor: "text-purple-500",
    },
    {
      id: "videos",
      label: "Tổng video",
      value: formatNumber(summary?.totalVideos),
      icon: Video,
      accentColor: "text-indigo-500",
    },
    {
      id: "gmv",
      label: "Doanh thu GMV",
      value: formatCurrency(summary?.totalGmvAmount, summary?.currency),
      subtext: summary?.totalGmvAmount !== null ? "Ước tính từ TikTok Shop" : undefined,
      icon: ShoppingBag,
      accentColor: "text-teal-500",
    },
    {
      id: "affiliate_clicks",
      label: "Click Affiliate",
      value: formatNumber(summary?.totalAffiliateClicks),
      subtext: summary?.totalAffiliateClicks !== null ? "Lượt bấm vào liên kết" : undefined,
      icon: MousePointerClick,
      accentColor: "text-warning",
    },
  ];

  return (
    <div className="grid grid-cols-2 sm:grid-cols-2 md:grid-cols-4 gap-4" data-testid="kpi-cards">
      {items.map((item) => {
        const IconComponent = item.icon;
        return (
          <Card
            key={item.id}
            className="bg-card border-border hover:border-border/80 transition-[color,background-color,border-color,box-shadow,opacity,transform] shadow-xs"
            data-testid={`kpi-card-${item.id}`}
          >
            <CardContent className="p-4 flex flex-col justify-between h-full">
              <div className="flex items-center justify-between">
                <span className="text-xs font-medium text-muted-foreground line-clamp-1">
                  {item.label}
                </span>
                <span className={`p-1.5 rounded-md bg-muted/40 ${item.accentColor}`}>
                  <IconComponent className="w-4 h-4" />
                </span>
              </div>
              <div className="mt-3">
                <div
                  className="text-xl sm:text-2xl font-bold tracking-tight text-foreground truncate"
                  data-testid={`kpi-value-${item.id}`}
                >
                  {item.value}
                </div>
                {item.subtext ? (
                  <p className="text-[11px] text-muted-foreground mt-0.5 truncate">
                    {item.subtext}
                  </p>
                ) : (
                  <div className="h-4" />
                )}
              </div>
            </CardContent>
          </Card>
        );
      })}
    </div>
  );
}
