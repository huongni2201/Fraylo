import React, { useState, useId } from "react";
import { Eye, Heart, MessageSquare, Share2, BarChart2, Users, UserPlus, UserRoundCheck } from "lucide-react";
import { Card, CardHeader, CardTitle, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import type { AnalyticsTimeseriesPoint, AnalyticsMetricType } from "../analyticsTypes";
import { formatNumber } from "./AnalyticsKpiCards";

interface AnalyticsChartProps {
  data: AnalyticsTimeseriesPoint[];
  currentMetric: AnalyticsMetricType;
  onMetricChange: (metric: AnalyticsMetricType) => void;
  metricOptions?: AnalyticsMetricType[];
  loading?: boolean;
}

const metricConfigs: Record<
  AnalyticsMetricType,
  { label: string; icon: React.ComponentType<{ className?: string }>; color: string }
> = {
  views: { label: "Lượt xem", icon: Eye, color: "#6366f1" },
  video_views: { label: "Lượt xem video", icon: Eye, color: "#6366f1" },
  profile_views: { label: "Lượt xem hồ sơ", icon: Eye, color: "#8b5cf6" },
  likes: { label: "Lượt thích", icon: Heart, color: "#f43f5e" },
  comments: { label: "Bình luận", icon: MessageSquare, color: "#f59e0b" },
  shares: { label: "Chia sẻ", icon: Share2, color: "#10b981" },
  total_viewers: { label: "Tổng người xem", icon: Users, color: "#6366f1" },
  new_viewers: { label: "Người xem mới", icon: UserPlus, color: "#10b981" },
  returning_viewers: { label: "Người xem quay lại", icon: UserRoundCheck, color: "#f59e0b" },
};

function formatShortDate(dateStr: string): string {
  try {
    const parts = dateStr.split("-");
    if (parts.length === 3) {
      return `${parts[2]}/${parts[1]}`;
    }
    const d = new Date(dateStr);
    return `${d.getDate()}/${d.getMonth() + 1}`;
  } catch {
    return dateStr;
  }
}

export function AnalyticsChart({
  data,
  currentMetric,
  onMetricChange,
  metricOptions,
  loading = false,
}: AnalyticsChartProps) {
  const gradientId = useId();
  const [hoveredIndex, setHoveredIndex] = useState<number | null>(null);

  const activeConfig = metricConfigs[currentMetric] || metricConfigs.views;
  const visibleMetrics = metricOptions ?? (Object.keys(metricConfigs) as AnalyticsMetricType[]);

  // SVG dimensions
  const svgWidth = 600;
  const svgHeight = 240;
  const padding = { top: 25, right: 30, bottom: 45, left: 55 };
  const innerWidth = svgWidth - padding.left - padding.right;
  const innerHeight = svgHeight - padding.top - padding.bottom;

  // Filter valid numeric points
  const points = Array.isArray(data) ? data : [];
  const numericValues = points
    .map((p) => p.value)
    .filter((v): v is number => typeof v === "number" && !isNaN(v));

  const maxVal = numericValues.length > 0 ? Math.max(...numericValues) : 0;
  const yDomainMax = maxVal > 0 ? Math.ceil(maxVal * 1.15) : 10;

  // Compute coordinates
  const coords = points.map((p, i) => {
    const x =
      padding.left +
      (points.length > 1 ? (i / (points.length - 1)) * innerWidth : innerWidth / 2);
    const val = p.value ?? 0;
    const y = padding.top + innerHeight - (val / yDomainMax) * innerHeight;
    return { x, y, point: p, rawIndex: i };
  });

  const linePathD =
    coords.length > 0
      ? coords.reduce(
          (acc, c, i) => `${acc} ${i === 0 ? "M" : "L"} ${c.x.toFixed(1)} ${c.y.toFixed(1)}`,
          ""
        )
      : "";

  const areaPathD =
    coords.length > 0
      ? `${linePathD} L ${coords[coords.length - 1].x.toFixed(1)} ${(
          padding.top + innerHeight
        ).toFixed(1)} L ${coords[0].x.toFixed(1)} ${(
          padding.top + innerHeight
        ).toFixed(1)} Z`
      : "";

  // Y-axis ticks (0, 50%, 100%)
  const yTicks = [0, yDomainMax * 0.5, yDomainMax];

  // X-axis label sampling (at most ~6-7 labels)
  const step = Math.max(1, Math.floor(points.length / 6));
  const xLabelIndices = points
    .map((_, i) => i)
    .filter((i) => i % step === 0 || i === points.length - 1);

  return (
    <Card className="bg-card border-border flex flex-col h-full shadow-xs">
      <CardHeader className="p-4 pb-2 flex flex-col sm:flex-row sm:items-center justify-between gap-3 border-b border-border/50">
        <div className="flex items-center gap-2">
          <div className="p-1.5 rounded-md bg-primary/10 text-primary">
            <BarChart2 className="w-4 h-4" />
          </div>
          <div>
            <CardTitle className="text-base font-semibold text-foreground">
              Xu hướng tăng trưởng
            </CardTitle>
            <p className="text-xs text-muted-foreground">
              Theo dõi biến động chỉ số theo từng ngày
            </p>
          </div>
        </div>

        {/* Metric selection tabs */}
        <div className="flex items-center gap-1 bg-muted/60 p-1 rounded-lg self-start sm:self-auto">
          {visibleMetrics.map((metricKey) => {
            const config = metricConfigs[metricKey];
            const isSelected = currentMetric === metricKey;
            const Icon = config.icon;
            return (
              <Button
                key={metricKey}
                variant={isSelected ? "secondary" : "ghost"}
                size="sm"
                className={`h-7 px-2.5 text-xs font-medium rounded-md transition-[color,background-color,border-color,box-shadow,opacity,transform] gap-1.5 ${
                  isSelected
                    ? "bg-background text-foreground shadow-xs"
                    : "text-muted-foreground hover:text-foreground"
                }`}
                onClick={() => onMetricChange(metricKey)}
                data-testid={`chart-metric-${metricKey}`}
              >
                <Icon className="w-3.5 h-3.5" />
                <span>{config.label}</span>
              </Button>
            );
          })}
        </div>
      </CardHeader>

      <CardContent className="p-4 pt-4 flex-1 flex flex-col justify-center">
        {loading ? (
          <div className="h-60 flex items-center justify-center animate-pulse" data-testid="chart-loading">
            <div className="text-center space-y-2">
              <div className="w-8 h-8 rounded-full border-2 border-primary border-t-transparent animate-spin mx-auto" />
              <p className="text-xs text-muted-foreground">Đang tải biểu đồ...</p>
            </div>
          </div>
        ) : points.length === 0 ? (
          <div className="h-60 flex items-center justify-center text-center p-6 border border-dashed border-border/70 rounded-lg" data-testid="chart-empty">
            <div className="space-y-1">
              <p className="text-sm font-medium text-foreground">
                Chưa có dữ liệu chuỗi thời gian
              </p>
              <p className="text-xs text-muted-foreground">
                Nhập báo cáo từ TikTok Analytics để theo dõi biểu đồ xu hướng.
              </p>
            </div>
          </div>
        ) : (
          <div className="relative w-full overflow-hidden">
            <svg
              viewBox={`0 0 ${svgWidth} ${svgHeight}`}
              className="w-full h-auto overflow-visible select-none"
              role="img"
              aria-label={`Biểu đồ xu hướng ${activeConfig.label}`}
            >
              <defs>
                <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
                  <stop offset="0%" stopColor={activeConfig.color} stopOpacity="0.3" />
                  <stop offset="100%" stopColor={activeConfig.color} stopOpacity="0.0" />
                </linearGradient>
              </defs>

              {/* Horizontal grid lines & Y labels */}
              {yTicks.map((tickVal, idx) => {
                const y = padding.top + innerHeight - (tickVal / yDomainMax) * innerHeight;
                return (
                  <g key={idx} className="transition-[color,background-color,border-color,box-shadow,opacity,transform]">
                    <line
                      x1={padding.left}
                      y1={y}
                      x2={padding.left + innerWidth}
                      y2={y}
                      stroke="currentColor"
                      className="text-border/60"
                      strokeDasharray="4 4"
                      strokeWidth="1"
                    />
                    <text
                      x={padding.left - 8}
                      y={y + 4}
                      textAnchor="end"
                      className="text-[10px] fill-muted-foreground font-mono"
                    >
                      {formatNumber(Math.round(tickVal))}
                    </text>
                  </g>
                );
              })}

              {/* Area fill */}
              {areaPathD && (
                <path d={areaPathD} fill={`url(#${gradientId})`} className="transition-[color,background-color,border-color,box-shadow,opacity,transform] duration-300" />
              )}

              {/* Trend line */}
              {linePathD && (
                <path
                  d={linePathD}
                  fill="none"
                  stroke={activeConfig.color}
                  strokeWidth="2.5"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  className="transition-[color,background-color,border-color,box-shadow,opacity,transform] duration-300"
                />
              )}

              {/* X Axis Labels */}
              {xLabelIndices.map((idx) => {
                const c = coords[idx];
                if (!c) return null;
                return (
                  <text
                    key={idx}
                    x={c.x}
                    y={padding.top + innerHeight + 18}
                    textAnchor="middle"
                    className="text-[10px] fill-muted-foreground font-mono"
                  >
                    {formatShortDate(c.point.date)}
                  </text>
                );
              })}

              {/* Data circles & hover areas */}
              {coords.map((c, i) => {
                const isHovered = hoveredIndex === i;
                const val = c.point.value;
                return (
                  <g
                    key={i}
                    onMouseEnter={() => setHoveredIndex(i)}
                    onMouseLeave={() => setHoveredIndex(null)}
                    className="cursor-pointer"
                    tabIndex={0}
                    role="button"
                    aria-label={`${c.point.date}: ${formatNumber(val)}`}
                    onFocus={() => setHoveredIndex(i)}
                    onBlur={() => setHoveredIndex(null)}
                  >
                    {/* Invisible larger hit area for easy hover */}
                    <circle cx={c.x} cy={c.y} r="12" fill="transparent" />

                    {/* Data circle */}
                    <circle
                      cx={c.x}
                      cy={c.y}
                      r={isHovered ? "5.5" : "3.5"}
                      fill="var(--card, #fff)"
                      stroke={activeConfig.color}
                      strokeWidth={isHovered ? "3" : "2"}
                      className="transition-[color,background-color,border-color,box-shadow,opacity,transform] duration-150"
                    />

                    {/* Tooltip on hover */}
                    {isHovered && (
                      <g className="pointer-events-none transition-[color,background-color,border-color,box-shadow,opacity,transform]">
                        {/* Vertical indicator line */}
                        <line
                          x1={c.x}
                          y1={padding.top}
                          x2={c.x}
                          y2={padding.top + innerHeight}
                          stroke={activeConfig.color}
                          strokeWidth="1"
                          strokeDasharray="2 2"
                          opacity="0.6"
                        />

                        {/* Tooltip bubble */}
                        <g
                          transform={`translate(${Math.min(
                            Math.max(c.x, 60),
                            svgWidth - 60
                          )}, ${Math.max(c.y - 36, 12)})`}
                        >
                          <rect
                            x="-50"
                            y="-12"
                            width="100"
                            height="28"
                            rx="5"
                            className="fill-popover stroke-border"
                            strokeWidth="1"
                            filter="drop-shadow(0 2px 4px rgba(0,0,0,0.15))"
                          />
                          <text
                            x="0"
                            y="0"
                            textAnchor="middle"
                            className="text-[9px] fill-muted-foreground font-mono"
                          >
                            {c.point.date}
                          </text>
                          <text
                            x="0"
                            y="11"
                            textAnchor="middle"
                            className="text-[11px] font-bold fill-foreground"
                          >
                            {formatNumber(val)}
                          </text>
                        </g>
                      </g>
                    )}
                  </g>
                );
              })}
            </svg>

            {/* WAI-ARIA accessible table for screen readers */}
            <div className="sr-only">
              <table>
                <caption>
                  Bảng dữ liệu chi tiết cho xu hướng {activeConfig.label} theo thời gian
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Ngày</th>
                    <th scope="col">Chỉ số</th>
                    <th scope="col">Giá trị</th>
                  </tr>
                </thead>
                <tbody>
                  {points.map((p, idx) => (
                    <tr key={idx}>
                      <td>{p.date}</td>
                      <td>{activeConfig.label}</td>
                      <td>{formatNumber(p.value)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
