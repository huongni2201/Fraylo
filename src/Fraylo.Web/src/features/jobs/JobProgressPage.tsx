import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AlertCircle, Inbox, Loader2, RefreshCw, ChevronRight } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { PageHeader } from "@/components/PageHeader";
import { Progress } from "@/components/ui/progress";
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { cn } from "@/lib/utils";
import { projectApi } from "../projects/projectApi";
import { JobDetailsDialog, type JobDetailsDialogProps } from "./JobDetailsDialog";
import { JobReconcileDialog } from "./JobReconcileDialog";
import { jobApi } from "./jobApi";
import { mergeJobs } from "./jobProgressModel";
import {
  getTaskTypeInfo,
  getStateBadgeInfo,
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
  onNavigateToSettingsAccounts: () => void;
}

export function JobProgressPage({ active, onNavigateToSettingsAccounts }: JobProgressPageProps) {
  const [activeJobs, setActiveJobs] = useState<JobSummaryDto[]>([]);
  const [historyJobs, setHistoryJobs] = useState<JobSummaryDto[]>([]);
  // undefined: chưa tải trang đầu; null: đã hết lịch sử
  const [nextCursor, setNextCursor] = useState<string | null | undefined>(undefined);
  const [projectNames, setProjectNames] = useState<Record<string, string>>({});
  // Tiến độ đến từ sự kiện job.progress_updated; server không lưu trong danh sách job
  const [progress, setProgress] = useState<Record<string, { percent: number; step: string }>>({});
  const [tab, setTab] = useState<QueueTabFilter>("all");
  const [selectedJob, setSelectedJob] = useState<JobSummaryDto | null>(null);
  const [reconcileJob, setReconcileJob] = useState<JobSummaryDto | JobDetailDto | null>(null);
  const [isLoading, setIsLoading] = useState(false);
  const [isLoadingMore, setIsLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [isActionLoading, setIsActionLoading] = useState(false);
  const actionBusy = useRef(false);

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

  const handleAction: NonNullable<JobDetailsDialogProps["onAction"]> = async (action, job) => {
    if (actionBusy.current) return;
    if (action === "navigate_settings_accounts") {
      setSelectedJob(null);
      onNavigateToSettingsAccounts();
      return;
    }
    actionBusy.current = true;
    setIsActionLoading(true);
    try {
      if (action === "cancel") await jobApi.cancel(job.id);
      if (action === "resume") await jobApi.resume(job.id);
      if (action === "retry") await jobApi.retry(job.id, job.revision ?? 1);
      if (action === "login") await jobApi.requestLogin(job.profileId ?? "");
      setSelectedJob(null);
      await loadFirstPage();
    } catch (err) {
      setSelectedJob(null);
      setError(errorMessage(err));
    } finally {
      actionBusy.current = false;
      setIsActionLoading(false);
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

  const allJobs = useMemo(() => mergeJobs(historyJobs, activeJobs), [historyJobs, activeJobs]);
  const visibleJobs = useMemo(
    () => allJobs.filter((job) => matchesTab(job.state, tab)),
    [allJobs, tab],
  );

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title="Tiến trình"
        description="Theo dõi mọi tác vụ. Bấm vào một dòng để xem chi tiết và xử lý."
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
        <TabsList className="flex h-auto w-full flex-wrap justify-start gap-1 p-1 bg-muted/60">
          {(Object.keys(TAB_LABELS) as QueueTabFilter[]).map((value) => (
            <TabsTrigger key={value} value={value} className="min-h-8 flex-1 text-xs gap-1">
              <span>{TAB_LABELS[value]}</span>
              <span className="tabular-nums text-muted-foreground">
                {allJobs.filter((job) => matchesTab(job.state, value)).length}
              </span>
            </TabsTrigger>
          ))}
        </TabsList>
      </Tabs>

      {isLoading && allJobs.length === 0 && <p role="status" className="text-sm text-muted-foreground">Đang tải tiến trình…</p>}
      <ul className="divide-y divide-border overflow-hidden rounded-xl border border-border bg-card" aria-label="Danh sách tiến trình">
        {visibleJobs.map((job) => {
          const taskInfo = getTaskTypeInfo(job.taskType);
          const TaskIcon = taskInfo.icon;
          const stateInfo = getStateBadgeInfo(job.state, job.cancelRequested);
          const StateIcon = stateInfo.icon;
          const live = job.state === "Running" ? progress[job.id] : undefined;
          const percent = live?.percent ?? job.progressPercent;
          const context = job.projectId ? projectNames[job.projectId] ?? job.projectId.slice(0, 8) : `#${job.id.slice(0, 8)}`;
          return (
            <li key={job.id}>
              <button
                type="button"
                onClick={() => setSelectedJob(job)}
                aria-label={`Xem chi tiết ${taskInfo.label} · ${context}`}
                className="grid w-full grid-cols-[minmax(0,1fr)_auto] items-center gap-x-3 gap-y-2 px-3 py-2.5 text-left transition-colors hover:bg-muted/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring lg:grid-cols-[minmax(0,1fr)_160px_140px_16px]"
              >
                <span className="flex min-w-0 items-center gap-2.5">
                  <TaskIcon className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <span className="min-w-0">
                    <span className="block truncate text-sm font-medium">{taskInfo.label}</span>
                    <span className="block truncate text-xs text-muted-foreground" title={context}>{context}</span>
                  </span>
                </span>
                <span className="flex min-w-0 flex-col items-end gap-1 lg:items-start">
                  <Badge variant={stateInfo.variant} className={cn("max-w-full gap-1 px-2 py-0.5 text-[11px]", stateInfo.badgeClass)}>
                    <StateIcon className={cn("size-3 shrink-0", stateInfo.animate && "animate-spin")} aria-hidden="true" />
                    <span className="truncate">{stateInfo.label}</span>
                  </Badge>
                  {job.state === "Running" && percent != null && Number.isFinite(percent) && (
                    <span className="flex w-28 items-center gap-1.5">
                      <Progress value={Math.max(0, Math.min(100, percent))} className="h-1 flex-1" />
                      <span className="text-[10px] tabular-nums text-muted-foreground">{Math.round(Math.max(0, Math.min(100, percent)))}%</span>
                    </span>
                  )}
                </span>
                <time dateTime={job.createdAtUtc} className="hidden text-xs tabular-nums text-muted-foreground lg:block">{formatDateTime(job.createdAtUtc)}</time>
                <ChevronRight className="hidden size-4 text-muted-foreground lg:block" aria-hidden="true" />
              </button>
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
          Hiển thị {visibleJobs.length} / {allJobs.length} tác vụ
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
        onAction={handleAction}
        isActionLoading={isActionLoading}
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
