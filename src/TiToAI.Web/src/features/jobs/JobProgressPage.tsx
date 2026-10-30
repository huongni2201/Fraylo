import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertCircle, Check, Copy, Inbox, Loader2, RefreshCw, SearchCheck } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { PageHeader } from "@/components/PageHeader";
import { Progress } from "@/components/ui/progress";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { cn } from "@/lib/utils";
import { AssetMediaPreview } from "../library/AssetMediaPreview";
import { projectApi } from "../projects/projectApi";
import { videoWorkflowApi } from "../projects/videoWorkflowApi";
import { JobDetailsDialog } from "./JobDetailsDialog";
import { JobReconcileDialog } from "./JobReconcileDialog";
import { jobApi } from "./jobApi";
import {
  indexVideoText,
  isPipelineRow,
  jobRowText,
  mergeJobs,
  outputPreview,
  videoProjectIds,
  type VideoJobText,
} from "./jobProgressModel";
import {
  formatReasonCode,
  getStateBadgeInfo,
  isTerminalState,
  type JobDetailDto,
  type JobSummaryDto,
  type QueueTabFilter,
} from "./jobTypes";
import { matchesTab } from "./useQueueState";

const PAGE_SIZE = 50;

const TAB_LABELS: Record<QueueTabFilter, string> = {
  all: "Tất cả",
  running: "Đang chạy",
  awaiting: "Chờ / Cần xử lý",
  failed: "Thất bại",
  completed: "Hoàn tất",
};

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

function formatDateTime(utc: string): string {
  const date = new Date(utc);
  return Number.isNaN(date.getTime())
    ? utc
    : date.toLocaleString("vi-VN", { dateStyle: "short", timeStyle: "medium" });
}

/** Nội dung dài được rút gọn một dòng, kèm nút sao chép bản đầy đủ. */
function CopyableText({ value }: { value: string | undefined }) {
  const [copied, setCopied] = useState(false);
  const preview = outputPreview(value);
  if (!preview) return <span className="text-muted-foreground">—</span>;

  const copy = () => {
    void navigator.clipboard.writeText(value ?? "").then(() => {
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    });
  };

  return (
    <div className="flex items-start gap-1.5">
      <p className="line-clamp-3 flex-1 text-foreground" title={value}>
        {preview}
      </p>
      <Button variant="ghost" size="icon" className="h-6 w-6 shrink-0" onClick={copy} aria-label="Sao chép">
        {copied ? <Check className="size-3.5 text-success" /> : <Copy className="size-3.5 text-muted-foreground" />}
      </Button>
    </div>
  );
}

export interface JobProgressPageProps {
  active: boolean;
}

export function JobProgressPage({ active }: JobProgressPageProps) {
  const [activeJobs, setActiveJobs] = useState<JobSummaryDto[]>([]);
  const [historyJobs, setHistoryJobs] = useState<JobSummaryDto[]>([]);
  // undefined: chưa tải trang đầu; null: đã hết lịch sử
  const [nextCursor, setNextCursor] = useState<string | null | undefined>(undefined);
  const [projectNames, setProjectNames] = useState<Record<string, string>>({});
  const [videoText, setVideoText] = useState<Record<string, VideoJobText>>({});
  // Tiến độ đến từ sự kiện job.progress_updated; server không lưu trong danh sách job
  const [progress, setProgress] = useState<Record<string, { percent: number; step: string }>>({});
  const [cancelingIds, setCancelingIds] = useState<Set<string>>(new Set());
  const [tab, setTab] = useState<QueueTabFilter>("all");
  const [selectedJob, setSelectedJob] = useState<JobSummaryDto | null>(null);
  const [reconcileJob, setReconcileJob] = useState<JobSummaryDto | JobDetailDto | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [isLoadingMore, setIsLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Trang đầu lịch sử được nạp lại cùng danh sách đang chạy; con trỏ các trang sau vẫn hợp lệ (keyset).
  const loadFirstPage = useCallback(async () => {
    setIsLoading(true);
    try {
      const [activeList, page] = await Promise.all([
        jobApi.listActive(),
        jobApi.listHistory({ pageSize: PAGE_SIZE }),
      ]);
      setActiveJobs(activeList);
      setHistoryJobs((prev) => mergeJobs(prev, page.items));
      setNextCursor((prev) => (prev === undefined ? page.nextCursor : prev));
      setError(null);

      const contentLists = await Promise.all(
        videoProjectIds([...activeList, ...page.items]).map((projectId) =>
          videoWorkflowApi.listContentItems(projectId).catch(() => []),
        ),
      );
      setVideoText((prev) => ({ ...prev, ...indexVideoText(contentLists.flat()) }));
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setIsLoading(false);
    }
  }, []);

  const loadMore = async () => {
    if (!nextCursor) return;
    setIsLoadingMore(true);
    try {
      const page = await jobApi.listHistory({ pageSize: PAGE_SIZE, cursor: nextCursor });
      setHistoryJobs((prev) => mergeJobs(prev, page.items));
      setNextCursor(page.nextCursor);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setIsLoadingMore(false);
    }
  };

  const cancelJob = async (jobId: string) => {
    setCancelingIds((prev) => new Set(prev).add(jobId));
    try {
      await jobApi.cancel(jobId);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setCancelingIds((prev) => {
        const next = new Set(prev);
        next.delete(jobId);
        return next;
      });
    }
  };

  // Chỉ tải và lắng nghe khi trang đang hiển thị
  useEffect(() => {
    if (!active) return;
    void loadFirstPage();
    return jobApi.onStateChanged(() => {
      void loadFirstPage();
    });
  }, [active, loadFirstPage]);

  // Trang luôn được mount, nên tiến độ vẫn được ghi nhận khi đang ở trang khác
  useEffect(() => {
    return jobApi.onProgressUpdated((envelope) => {
      const payload = envelope.payload;
      if (!payload?.jobId) return;
      setProgress((prev) => ({
        ...prev,
        [payload.jobId]: { percent: payload.progressPercent, step: payload.currentStep },
      }));
    });
  }, []);

  useEffect(() => {
    if (!active) return;
    projectApi
      .listAll()
      .then((projects) =>
        setProjectNames(Object.fromEntries(projects.map((p) => [p.projectId, p.name]))),
      )
      .catch(() => {
        // Thiếu tên dự án chỉ ảnh hưởng nhãn hiển thị; bỏ qua
      });
  }, [active]);

  // Mỗi job render video là một dòng; job Kịch bản/Ảnh/Prompt chỉ hiện khi chưa thành công
  const allJobs = useMemo(() => mergeJobs(historyJobs, activeJobs), [historyJobs, activeJobs]);
  const rows = useMemo(() => allJobs.filter(isPipelineRow), [allJobs]);
  const visibleJobs = useMemo(
    () => rows.filter((job) => matchesTab(job.state, tab)),
    [rows, tab],
  );

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title="Tiến trình"
        description="Tiến trình tạo video theo từng dòng: kịch bản, prompt, trạng thái và kết quả đầu ra."
        actions={
          <Button
            variant="outline"
            size="sm"
            onClick={() => void loadFirstPage()}
            disabled={isLoading}
            aria-label="Làm mới tiến trình"
          >
            {isLoading ? <Loader2 className="size-3.5 animate-spin" /> : <RefreshCw className="size-3.5" />}
            Làm mới
          </Button>
        }
      />

      {error && (
        <Alert variant="destructive">
          <AlertCircle className="size-4" />
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      )}

      <Tabs value={tab} onValueChange={(value) => setTab(value as QueueTabFilter)}>
        <TabsList className="grid w-full grid-cols-5 h-9 p-1 bg-muted/60">
          {(Object.keys(TAB_LABELS) as QueueTabFilter[]).map((value) => (
            <TabsTrigger key={value} value={value} className="text-xs gap-1">
              <span>{TAB_LABELS[value]}</span>
              <span className="tabular-nums text-muted-foreground">
                {rows.filter((job) => matchesTab(job.state, value)).length}
              </span>
            </TabsTrigger>
          ))}
        </TabsList>
      </Tabs>

      <div className="rounded-lg border border-border bg-card overflow-x-auto">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead className="w-12 text-right">STT</TableHead>
              <TableHead className="max-w-[140px]">Dự án</TableHead>
              <TableHead className="min-w-[240px]">Kịch bản (AI)</TableHead>
              <TableHead className="min-w-[240px]">Prompt video</TableHead>
              <TableHead className="text-right">Lần thử</TableHead>
              <TableHead className="whitespace-nowrap">Thời gian tạo</TableHead>
              <TableHead className="min-w-[220px]">Output</TableHead>
              <TableHead>
                <span className="sr-only">Thao tác</span>
              </TableHead>
              <TableHead className="min-w-[160px]">Trạng thái</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {visibleJobs.map((job, index) => {
              const text = jobRowText(job, videoText);
              const stateInfo = getStateBadgeInfo(job.state, job.cancelRequested);
              const live = job.state === "Running" ? progress[job.id] : undefined;
              const canCancel = !isTerminalState(job.state) && !job.cancelRequested;
              const canceling = cancelingIds.has(job.id);
              const fallback = job.state === "Succeeded" ? "" : formatReasonCode(job.reasonCode);
              return (
                <TableRow key={job.id}>
                  <TableCell className="text-right tabular-nums text-xs text-muted-foreground">
                    {index + 1}
                  </TableCell>
                  <TableCell className="max-w-[140px] truncate text-xs" title={job.projectId}>
                    {projectNames[job.projectId] ?? job.projectId.slice(0, 8)}
                  </TableCell>
                  <TableCell className="max-w-[300px] text-xs">
                    <CopyableText value={text.script} />
                  </TableCell>
                  <TableCell className="max-w-[300px] text-xs">
                    <CopyableText value={text.prompt} />
                  </TableCell>
                  <TableCell className="text-right tabular-nums text-xs">
                    {job.attempts}/{job.maxAttempts ?? "—"}
                  </TableCell>
                  <TableCell className="whitespace-nowrap text-xs">
                    {formatDateTime(job.createdAtUtc)}
                  </TableCell>
                  <TableCell className="max-w-[260px] text-xs">
                    {job.taskType === "VideoRender" && job.resultReference ? (
                      <AssetMediaPreview
                        assetId={job.resultReference}
                        assetType="Video"
                        fileName={`Video ${index + 1}`}
                      />
                    ) : (
                      <span className="text-muted-foreground">{fallback || "—"}</span>
                    )}
                  </TableCell>
                  <TableCell>
                    <Button variant="ghost" size="sm" onClick={() => setSelectedJob(job)}>
                      Chi tiết
                    </Button>
                  </TableCell>
                  <TableCell>
                    <div className="flex flex-col gap-1.5">
                      <div className="flex items-center gap-2">
                        <Badge
                          variant={stateInfo.variant}
                          className={cn("flex w-fit items-center gap-1 px-2 py-0.5", stateInfo.badgeClass)}
                        >
                          {stateInfo.label}
                        </Badge>
                        {job.state === "AwaitingUser" && (
                          <Button
                            variant="outline"
                            size="sm"
                            className="h-6 px-2 text-xs text-warning dark:text-warning"
                            onClick={() => setReconcileJob(job)}
                          >
                            <SearchCheck className="size-3" />
                            Đối soát
                          </Button>
                        )}
                        {canCancel && (
                          <Button
                            variant="outline"
                            size="sm"
                            className="h-6 px-2 text-xs text-destructive"
                            disabled={canceling}
                            onClick={() => void cancelJob(job.id)}
                          >
                            {canceling && <Loader2 className="size-3 animate-spin" />}
                            Hủy
                          </Button>
                        )}
                      </div>
                      {live && (
                        <>
                          <Progress value={live.percent} className="h-1.5" />
                          <p className="truncate text-[11px] text-muted-foreground tabular-nums">
                            {Math.round(live.percent)}% · {live.step}
                          </p>
                        </>
                      )}
                    </div>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>

        {!isLoading && visibleJobs.length === 0 && (
          <div className="flex flex-col items-center gap-2 py-12 text-muted-foreground">
            <Inbox className="size-8 opacity-50" />
            <p className="text-sm">Chưa có tác vụ nào trong bộ lọc này.</p>
          </div>
        )}
      </div>

      <div className="flex items-center justify-between text-xs text-muted-foreground">
        <span>
          Hiển thị {visibleJobs.length} / {rows.length} tác vụ
        </span>
        {nextCursor && (
          <Button variant="outline" size="sm" onClick={() => void loadMore()} disabled={isLoadingMore}>
            {isLoadingMore && <Loader2 className="size-3.5 animate-spin" />}
            Tải thêm
          </Button>
        )}
      </div>

      <JobDetailsDialog
        job={selectedJob}
        open={selectedJob !== null}
        onOpenChange={(open) => {
          if (!open) setSelectedJob(null);
        }}
        onOpenReconcile={(job) => setReconcileJob(job)}
      />

      <JobReconcileDialog
        job={reconcileJob}
        open={reconcileJob !== null}
        onOpenChange={(open) => {
          if (!open) setReconcileJob(null);
        }}
        onReconciled={() => void loadFirstPage()}
      />
    </div>
  );
}
