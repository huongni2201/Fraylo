import {
  Film,
  Eye,
  Heart,
  MessageSquare,
  Share2,
  ShoppingBag,
  ArrowUpDown,
  Calendar,
} from "lucide-react";
import {
  Table,
  TableHeader,
  TableBody,
  TableHead,
  TableRow,
  TableCell,
} from "@/components/ui/table";
import { Card, CardHeader, CardTitle, CardContent } from "@/components/ui/card";
import type { VideoMetricRecord, VideoSortField } from "../analyticsTypes";
import { formatNumber, formatCurrency } from "./AnalyticsKpiCards";

interface TopVideosTableProps {
  videos: VideoMetricRecord[];
  loading?: boolean;
  sortBy: VideoSortField;
  onSortChange: (sortBy: VideoSortField) => void;
}

const sortOptions: { value: VideoSortField; label: string }[] = [
  { value: "views", label: "Lượt xem (Cao → Thấp)" },
  { value: "likes", label: "Lượt thích (Cao → Thấp)" },
  { value: "comments", label: "Bình luận (Cao → Thấp)" },
  { value: "shares", label: "Chia sẻ (Cao → Thấp)" },
  { value: "gmvAmount", label: "Doanh thu GMV (Cao → Thấp)" },
];

function formatDate(dateStr: string | null | undefined): string {
  if (!dateStr) return "—";
  try {
    const d = new Date(dateStr);
    if (isNaN(d.getTime())) return dateStr;
    return d.toLocaleDateString("vi-VN", {
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
    });
  } catch {
    return dateStr;
  }
}

export function TopVideosTable({
  videos,
  loading = false,
  sortBy,
  onSortChange,
}: TopVideosTableProps) {
  const videoList = Array.isArray(videos) ? videos : [];

  return (
    <Card className="bg-card border-border shadow-xs" data-testid="top-videos-table-card">
      <CardHeader className="p-4 pb-3 flex flex-col sm:flex-row sm:items-center justify-between gap-3 border-b border-border/50">
        <div className="flex items-center gap-2">
          <div className="p-1.5 rounded-md bg-primary/10 text-primary dark:text-primary-text">
            <Film className="w-4 h-4" />
          </div>
          <div>
            <CardTitle className="text-base font-semibold text-foreground">
              Danh sách video hiệu quả cao
            </CardTitle>
            <p className="text-xs text-muted-foreground">
              Bảng xếp hạng hiệu suất các video đã xuất bản
            </p>
          </div>
        </div>

        {/* Sort Select */}
        <div className="flex items-center gap-2 self-start sm:self-auto">
          <ArrowUpDown className="w-3.5 h-3.5 text-muted-foreground" />
          <label htmlFor="sort-select" className="text-xs text-muted-foreground font-medium sr-only sm:not-sr-only">
            Sắp xếp theo:
          </label>
          <select
            id="sort-select"
            value={sortBy}
            onChange={(e) => onSortChange(e.target.value as VideoSortField)}
            className="text-xs bg-muted/60 border border-border/70 rounded-md px-2.5 py-1.5 text-foreground focus:outline-hidden focus:ring-1 focus:ring-primary cursor-pointer"
            data-testid="top-videos-sort-select"
          >
            {sortOptions.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>
      </CardHeader>

      <CardContent className="p-0">
        {loading ? (
          <div className="p-8 text-center animate-pulse space-y-3" data-testid="top-videos-loading">
            <div className="h-6 bg-muted rounded w-1/3 mx-auto" />
            <div className="h-24 bg-muted rounded w-full" />
          </div>
        ) : videoList.length === 0 ? (
          <div className="p-8 text-center space-y-2" data-testid="top-videos-empty">
            <Film className="w-8 h-8 text-muted-foreground mx-auto stroke-1" />
            <p className="text-sm font-medium text-foreground">
              Không tìm thấy video nào trong kỳ này
            </p>
            <p className="text-xs text-muted-foreground max-w-sm mx-auto">
              Chưa có dữ liệu video cho tài khoản TikTok đã chọn. Bạn có thể nhập tệp CSV/JSON báo cáo nội dung TikTok.
            </p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <Table>
              <TableHeader className="bg-muted/30">
                <TableRow className="border-border/60 hover:bg-transparent">
                  <TableHead className="w-[320px] text-xs font-semibold">Video</TableHead>
                  <TableHead className="w-[120px] text-xs font-semibold">
                    <span className="flex items-center gap-1">
                      <Calendar className="w-3.5 h-3.5" />
                      Ngày đăng
                    </span>
                  </TableHead>
                  <TableHead className="text-right text-xs font-semibold">
                    <span className="flex items-center justify-end gap-1">
                      <Eye className="w-3.5 h-3.5" />
                      Lượt xem
                    </span>
                  </TableHead>
                  <TableHead className="text-right text-xs font-semibold">
                    <span className="flex items-center justify-end gap-1">
                      <Heart className="w-3.5 h-3.5" />
                      Thích
                    </span>
                  </TableHead>
                  <TableHead className="text-right text-xs font-semibold">
                    <span className="flex items-center justify-end gap-1">
                      <MessageSquare className="w-3.5 h-3.5" />
                      Bình luận
                    </span>
                  </TableHead>
                  <TableHead className="text-right text-xs font-semibold">
                    <span className="flex items-center justify-end gap-1">
                      <Share2 className="w-3.5 h-3.5" />
                      Chia sẻ
                    </span>
                  </TableHead>
                  <TableHead className="text-right text-xs font-semibold">
                    <span className="flex items-center justify-end gap-1">
                      <ShoppingBag className="w-3.5 h-3.5" />
                      GMV
                    </span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {videoList.map((video, index) => (
                  <TableRow
                    key={video.id || index}
                    className="border-border/50 hover:bg-muted/40 transition-colors"
                    data-testid={`top-video-row-${video.id || index}`}
                  >
                    {/* Video Column */}
                    <TableCell className="py-3">
                      <div className="flex items-center gap-3">
                        <div className="w-10 h-10 rounded-md bg-muted/80 flex items-center justify-center shrink-0 border border-border/60 text-muted-foreground">
                          <Film className="w-5 h-5 text-primary/80" />
                        </div>
                        <div className="min-w-0 flex-1">
                          <p className="text-xs font-medium text-foreground truncate" title={video.title}>
                            {video.title || "Video không tên"}
                          </p>
                          <p className="text-[11px] text-muted-foreground font-mono truncate">
                            ID: {video.videoKey || "—"}
                          </p>
                        </div>
                      </div>
                    </TableCell>

                    {/* Date */}
                    <TableCell className="text-xs text-muted-foreground font-mono whitespace-nowrap">
                      {formatDate(video.publishedAtUtc)}
                    </TableCell>

                    {/* Views */}
                    <TableCell className="text-right text-xs font-semibold text-foreground" data-testid="video-metric-views">
                      {formatNumber(video.views)}
                    </TableCell>

                    {/* Likes */}
                    <TableCell className="text-right text-xs text-muted-foreground" data-testid="video-metric-likes">
                      {formatNumber(video.likes)}
                    </TableCell>

                    {/* Comments */}
                    <TableCell className="text-right text-xs text-muted-foreground" data-testid="video-metric-comments">
                      {formatNumber(video.comments)}
                    </TableCell>

                    {/* Shares */}
                    <TableCell className="text-right text-xs text-muted-foreground" data-testid="video-metric-shares">
                      {formatNumber(video.shares)}
                    </TableCell>

                    {/* GMV */}
                    <TableCell className="text-right text-xs font-semibold text-teal-600 dark:text-teal-400" data-testid="video-metric-gmv">
                      {formatCurrency(video.gmvAmount)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
