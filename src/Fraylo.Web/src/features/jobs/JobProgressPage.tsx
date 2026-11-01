import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertCircle, Inbox, Loader2, RefreshCw, SearchCheck } from "lucide-react";
import { PromptCell } from "@/components/PromptCell";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { PageHeader } from "@/components/PageHeader";
import { Progress } from "@/components/ui/progress";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
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

      <ul className="flex flex-col gap-3" aria-label="Danh sách tiến trình">
        {visibleJobs.map((job, index) => {
          const text = jobRowText(job, videoText);
          const stateInfo = getStateBadgeInfo(job.state, job.cancelRequested);
          const StateIcon = stateInfo.icon;
          const live = job.state === "Running" ? progress[job.id] : undefined;
          const canCancel = !isTerminalState(job.state) && !job.cancelRequested;
          const canceling = cancelingIds.has(job.id);
          const fallback = job.state === "Succeeded" ? "" : formatReasonCode(job.reasonCode);
          const hasVideo = job.taskType === "VideoRender" && !!job.resultReference;
          return (
            <li key={job.id} className="overflow-hidden rounded-xl border border-border bg-card">
              <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-border bg-muted/30 px-4 py-3">
                <span className="flex size-6 items-center justify-center rounded-md bg-primary/10 text-xs font-semibold tabular-nums text-primary">{index + 1}</span>
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium" title={job.projectId}>{projectNames[job.projectId] ?? job.projectId.slice(0, 8)}</p>
                  <p className="text-xs text-muted-foreground tabular-nums">
                    {formatDateTime(job.createdAtUtc)} · Lần thử {job.attempts}/{job.maxAttempts ?? "—"}
                  </p>
                </div>
                <Badge variant={stateInfo.variant} className={cn("flex w-fit items-center gap-1 px-2 py-0.5", stateInfo.badgeClass)}>
                  <StateIcon className={cn("size-3", stateInfo.animate && "animate-spin motion-reduce:animate-none")} aria-hidden="true" />
                  {stateInfo.label}
                </Badge>
                <div className="flex items-center gap-1.5">
                  {job.state === "AwaitingUser" && job.taskType === "VideoRender" && (
                    <Button variant="outline" size="sm" className="h-8 gap-1 px-2 text-xs text-warning dark:text-warning" onClick={() => setReconcileJob(job)}>
                      <SearchCheck className="size-3.5" aria-hidden="true" />Đối soát
                    </Button>
                  )}
                  {canCancel && (
                    <Button variant="outline" size="sm" className="h-8 gap-1 px-2 text-xs text-destructive" disabled={canceling} onClick={() => void cancelJob(job.id)}>
                      {canceling && <Loader2 className="size-3.5 animate-spin" aria-hidden="true" />}Hủy
                    </Button>
                  )}
                  <Button variant="ghost" size="sm" className="h-8 px-2 text-xs" onClick={() => setSelectedJob(job)}>Chi tiết</Button>
                </div>
              </div>
              {live && (
                <div className="px-4 pt-3">
                  <Progress value={live.percent} className="h-1.5" />
                  <p className="mt-1 truncate text-[11px] text-muted-foreground tabular-nums">{Math.round(live.percent)}% · {live.step}</p>
                </div>
              )}
              <div className="grid gap-4 p-4 lg:grid-cols-[220px_minmax(0,1fr)]">
                <div className="space-y-2">
                  {hasVideo ? (
                    <AssetMediaPreview assetId={job.resultReference!} assetType="Video" fileName={`Video ${index + 1}`} heightClass="h-48" />
                  ) : text.imageAssetId ? (
                    <AssetMediaPreview assetId={text.imageAssetId} assetType="Image" fileName={`Ảnh tham chiếu ${index + 1}`} heightClass="h-48" />
                  ) : null}
                  {fallback && (
                    <p className="flex items-start gap-1.5 text-xs leading-relaxed text-muted-foreground">
                      <AlertCircle className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />{fallback}
                    </p>
                  )}
                </div>
                <div className="grid gap-3 md:grid-cols-3">
                  {([["Kịch bản (AI)", text.script], ["Prompt video", text.prompt], ["Nội dung đăng TikTok", text.tiktok]] as const).map(([label, value]) => (
                    <div key={label} className="min-w-0 space-y-1.5">
                      <h3 className="text-xs font-medium text-muted-foreground">{label}</h3>
                      <PromptCell text={value} emptyText="—" title={label} />
                    </div>
                  ))}
                </div>
              </div>
            </li>
          );
        })}
      </ul>
      {!isLoading && visibleJobs.length === 0 && (
        <div className="flex flex-col items-center gap-2 rounded-xl border border-dashed border-border py-12 text-muted-foreground">
          <Inbox className="size-8 opacity-50" aria-hidden="true" />
          <p className="text-sm">Chưa có tác vụ nào trong bộ lọc này.</p>
        </div>
      )}

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
