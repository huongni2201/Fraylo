import {
  Clock,
  Loader2,
  AlertCircle,
  LogIn,
  SearchCheck,
  Play,
  RotateCcw,
  Ban,
  Info,
  ExternalLink,
  Settings,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Progress } from "@/components/ui/progress";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import {
  type JobSummaryDto,
  getTaskTypeInfo,
  getStateBadgeInfo,
  formatReasonCode,
  getRecommendedAction,
} from "./jobTypes";

export interface JobItemCardProps {
  job: JobSummaryDto & {
    cancelRequested?: boolean;
    sideEffectSubmitted?: number;
    providerKey?: string;
    hasUnresolvedSideEffect?: boolean;
    isHydrating?: boolean;
    profileId?: string | null;
  };
  onNavigateToJob?: (job: JobSummaryDto) => void;
  onOpenDetails?: (job: JobSummaryDto) => void;
  onOpenReconcile?: (job: JobSummaryDto) => void;
  onAction?: (
    actionType: "login" | "navigate_settings_accounts" | "resume" | "retry" | "cancel",
    job: JobSummaryDto
  ) => void;
  className?: string;
  isActionLoading?: boolean;
}

function formatJobTime(utcString?: string): string {
  if (!utcString) return "";
  try {
    const date = new Date(utcString);
    if (isNaN(date.getTime())) return utcString;
    return new Intl.DateTimeFormat("vi-VN", {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      day: "2-digit",
      month: "2-digit",
    }).format(date);
  } catch {
    return utcString;
  }
}

export function JobItemCard({
  job,
  onNavigateToJob,
  onOpenDetails,
  onOpenReconcile,
  onAction,
  className,
  isActionLoading = false,
}: JobItemCardProps) {
  const taskTypeInfo = getTaskTypeInfo(job.taskType);
  const stateBadgeInfo = getStateBadgeInfo(job.state, job.cancelRequested);
  const recommendedAction = getRecommendedAction(job);
  const formattedReason = formatReasonCode(job.reasonCode);
  const TaskIcon = taskTypeInfo.icon;
  const StateIcon = stateBadgeInfo.icon;

  const isCancelableState =
    (job.state === "Queued" ||
      job.state === "Reserved" ||
      job.state === "Running" ||
      job.state === "AwaitingUser") &&
    !job.cancelRequested;

  const isAwaitingAction = job.state === "AwaitingUser";
  const isFailedState = job.state === "Failed";
  const isRunningState = job.state === "Running";

  return (
    <div
      className={cn(
        "group relative flex flex-col gap-3 rounded-lg border border-border bg-card p-3.5 text-card-foreground shadow-xs transition-[color,background-color,border-color,box-shadow,opacity,transform] hover:border-border/80 hover:shadow-sm",
        isAwaitingAction && "border-warning/40 bg-warning/5 dark:border-warning/30 dark:bg-warning/10",
        isFailedState && "border-destructive/30 bg-destructive/5 dark:bg-destructive/10",
        className
      )}
    >
      {/* Hàng trên: Phân loại tác vụ & Badge trạng thái */}
      <div className="flex items-start justify-between gap-2">
        <div className="flex items-center gap-2.5 min-w-0">
          <div className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md border border-border bg-muted/60 text-foreground">
            <TaskIcon className="h-4 w-4" />
          </div>
          <div className="min-w-0">
            <div className="flex items-center gap-1.5">
              <span className="truncate text-sm font-semibold tracking-tight text-foreground">
                {taskTypeInfo.label}
              </span>
              <span className="text-xs text-muted-foreground">
                #{job.id.slice(0, 8)}
              </span>
            </div>

            {/* Thông tin ngữ cảnh dự án & phân cảnh */}
            <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
              {job.sceneId ? (
                <button
                  type="button"
                  onClick={() => onNavigateToJob?.(job)}
                  className={cn(
                    "inline-flex items-center gap-0.5 truncate transition-colors",
                    onNavigateToJob
                      ? "hover:text-primary cursor-pointer hover:underline"
                      : "cursor-default"
                  )}
                  title="Điều hướng tới phân cảnh này"
                >
                  <span className="truncate">Phân cảnh: {job.sceneId}</span>
                  {onNavigateToJob && <ExternalLink className="h-3 w-3 shrink-0" />}
                </button>
              ) : job.projectId ? (
                <button
                  type="button"
                  onClick={() => onNavigateToJob?.(job)}
                  className={cn(
                    "inline-flex items-center gap-0.5 truncate transition-colors",
                    onNavigateToJob
                      ? "hover:text-primary cursor-pointer hover:underline"
                      : "cursor-default"
                  )}
                  title="Điều hướng tới dự án này"
                >
                  <span className="truncate">Dự án: {job.projectId}</span>
                  {onNavigateToJob && <ExternalLink className="h-3 w-3 shrink-0" />}
                </button>
              ) : (
                <span>Tác vụ toàn hệ thống</span>
              )}
            </div>
          </div>
        </div>

        {/* Trạng thái tác vụ */}
        <div className="shrink-0">
          <Badge
            variant={stateBadgeInfo.variant}
            className={cn("flex items-center gap-1 px-2 py-0.5", stateBadgeInfo.badgeClass)}
          >
            <StateIcon
              className={cn("h-3 w-3", stateBadgeInfo.animate && "animate-spin")}
            />
            <span>{stateBadgeInfo.label}</span>
          </Badge>
        </div>
      </div>

      {/* Cảnh báo đang yêu cầu hủy an toàn */}
      {job.cancelRequested && job.state !== "Canceled" && (
        <Alert
          variant="warning"
          className="py-2 px-3 text-xs border-warning/40 bg-warning/10 text-warning dark:text-warning"
        >
          <Clock className="h-3.5 w-3.5 animate-spin" />
          <AlertDescription>
            Đang xử lý hủy an toàn với nhà cung cấp AI... Vui lòng không đóng ứng dụng.
          </AlertDescription>
        </Alert>
      )}

      {/* Lý do lỗi hoặc lý do cần người dùng tương tác */}
      {formattedReason && (
        <div
          className={cn(
            "rounded-md border p-2 text-xs leading-relaxed",
            isAwaitingAction
              ? "border-warning/30 bg-warning/10 text-warning dark:text-warning"
              : isFailedState
              ? "border-destructive/30 bg-destructive/10 text-destructive dark:text-destructive"
              : "border-border bg-muted/40 text-muted-foreground"
          )}
        >
          <div className="flex items-start gap-1.5">
            <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            <span>{formattedReason}</span>
          </div>
        </div>
      )}

      {/* Tiến độ thực thi */}
      {isRunningState && (
        <div className="flex flex-col gap-1.5">
          <div className="flex items-center justify-between text-xs text-muted-foreground">
            <span className="truncate">
              {job.currentStep ? (
                <span className="text-foreground font-medium">
                  Bước: {job.currentStep}
                </span>
              ) : (
                "Đang tiến hành xử lý qua AI..."
              )}
            </span>
            {job.progressPercent != null && (
              <span className="font-semibold text-primary">
                {Math.round(job.progressPercent)}%
              </span>
            )}
          </div>
          {job.progressPercent != null ? (
            <Progress
              value={job.progressPercent}
              className="h-1.5 bg-secondary"
            />
          ) : (
            <div className="h-1.5 w-full overflow-hidden rounded-full bg-secondary">
              <div className="h-full w-1/3 animate-pulse rounded-full bg-primary/60" />
            </div>
          )}
        </div>
      )}

      {/* Hàng dưới: Metadata thời gian, số lần thử & Các nút hành động */}
      <div className="flex flex-wrap items-center justify-between gap-2 border-t border-border/60 pt-2 text-xs text-muted-foreground">
        <div className="flex items-center gap-3">
          <span className="inline-flex items-center gap-1" title="Thời gian tạo">
            <Clock className="h-3 w-3" />
            <span>{formatJobTime(job.createdAtUtc)}</span>
          </span>
          {job.attempts > 1 && (
            <span className="rounded bg-muted px-1.5 py-0.5 text-[11px] font-medium text-foreground">
              Lần thử {job.attempts}
            </span>
          )}
        </div>

        <div className="flex items-center gap-1.5 ml-auto">
          {/* Nút xem chi tiết modal */}
          <TooltipProvider>
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  className="h-7 w-7 text-muted-foreground hover:text-foreground cursor-pointer"
                  onClick={() => onOpenDetails?.(job)}
                  aria-label="Xem chi tiết tác vụ"
                >
                  <Info className="h-3.5 w-3.5" />
                </Button>
              </TooltipTrigger>
              <TooltipContent side="top">
                <p>Xem thông tin chi tiết</p>
              </TooltipContent>
            </Tooltip>
          </TooltipProvider>

          {/* Nút hành động chuẩn theo trạng thái */}
          {recommendedAction.type === "login" && (
            <Button
              type="button"
              variant="default"
              size="sm"
              disabled={isActionLoading}
              onClick={() => onAction?.("login", job)}
              className="h-7 gap-1 px-2.5 text-xs cursor-pointer shadow-xs"
            >
              {isActionLoading ? (
                <Loader2 className="h-3 w-3 animate-spin" />
              ) : (
                <LogIn className="h-3 w-3" />
              )}
              <span>{recommendedAction.label}</span>
            </Button>
          )}

          {recommendedAction.type === "navigate_settings_accounts" && (
            <Button
              type="button"
              variant="default"
              size="sm"
              disabled={isActionLoading}
              onClick={() => onAction?.("navigate_settings_accounts", job)}
              className="h-7 gap-1 px-2.5 text-xs cursor-pointer shadow-xs bg-primary"
            >
              <Settings className="h-3 w-3" />
              <span>{recommendedAction.label}</span>
            </Button>
          )}

          {recommendedAction.type === "reconcile" && (
            <Button
              type="button"
              variant="default"
              size="sm"
              disabled={isActionLoading}
              onClick={() => onOpenReconcile?.(job)}
              className="h-7 gap-1 px-2.5 text-xs bg-warning hover:bg-warning text-white cursor-pointer shadow-xs"
            >
              <SearchCheck className="h-3 w-3" />
              <span>{recommendedAction.label}</span>
            </Button>
          )}
          {recommendedAction.type === "resume" && (
            <Button
              type="button"
              variant="default"
              size="sm"
              disabled={isActionLoading}
              onClick={() => onAction?.("resume", job)}
              className="h-7 gap-1 px-2.5 text-xs cursor-pointer shadow-xs"
            >
              {isActionLoading ? (
                <Loader2 className="h-3 w-3 animate-spin" />
              ) : (
                <Play className="h-3 w-3" />
              )}
              <span>{recommendedAction.label}</span>
            </Button>
          )}

          {recommendedAction.type === "retry" && (
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={isActionLoading}
              onClick={() => onAction?.("retry", job)}
              className="h-7 gap-1 px-2.5 text-xs cursor-pointer"
            >
              {isActionLoading ? (
                <Loader2 className="h-3 w-3 animate-spin" />
              ) : (
                <RotateCcw className="h-3 w-3" />
              )}
              <span>{recommendedAction.label}</span>
            </Button>
          )}

          {/* Nút hủy tác vụ */}
          {isCancelableState && (
            <Button
              type="button"
              variant="ghost"
              size="sm"
              disabled={isActionLoading}
              onClick={() => onAction?.("cancel", job)}
              className="h-7 gap-1 px-2 text-xs text-muted-foreground hover:bg-destructive/10 hover:text-destructive cursor-pointer"
              title="Hủy tác vụ an toàn"
            >
              <Ban className="h-3 w-3" />
              <span>Hủy</span>
            </Button>
          )}
        </div>
      </div>
    </div>
  );
}
