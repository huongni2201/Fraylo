import { useState, useEffect } from "react";
import {
  Copy,
  Check,
  ExternalLink,
  Clock,
  RotateCcw,
  Ban,
  LogIn,
  SearchCheck,
  Play,
  Loader2,
  AlertTriangle,
  Server,
  FolderGit2,
  Hash,
  ShieldCheck,
  ShieldAlert,
  Settings,
} from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { ScrollArea } from "@/components/ui/scroll-area";
import { cn } from "@/lib/utils";
import {
  type JobSummaryDto,
  type JobDetailDto,
  getTaskTypeInfo,
  getStateBadgeInfo,
  formatReasonCode,
  getRecommendedAction,
} from "./jobTypes";
import { jobApi } from "./jobApi";

export interface JobDetailsDialogProps {
  job: (JobSummaryDto | JobDetailDto) | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onNavigateToJob?: (job: JobSummaryDto | JobDetailDto) => void;
  onAction?: (
    actionType: "login" | "navigate_settings_accounts" | "resume" | "retry" | "cancel",
    job: JobSummaryDto | JobDetailDto
  ) => void;
  onOpenReconcile?: (job: JobSummaryDto | JobDetailDto) => void;
  isActionLoading?: boolean;
}

function formatFullDateTime(utcString?: string | null): string {
  if (!utcString) return "Chưa cập nhật";
  try {
    const date = new Date(utcString);
    if (isNaN(date.getTime())) return utcString;
    return new Intl.DateTimeFormat("vi-VN", {
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      timeZoneName: "short",
    }).format(date);
  } catch {
    return utcString;
  }
}

export function JobDetailsDialog({
  job,
  open,
  onOpenChange,
  onNavigateToJob,
  onAction,
  onOpenReconcile,
  isActionLoading = false,
}: JobDetailsDialogProps) {
  const [detailData, setDetailData] = useState<JobDetailDto | null>(null);
  const [isLoadingDetail, setIsLoadingDetail] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [copiedKey, setCopiedKey] = useState<string | null>(null);

  // Fetch dữ liệu chi tiết khi modal mở
  useEffect(() => {
    if (!open || !job) {
      setDetailData(null);
      setLoadError(null);
      return;
    }

    let isMounted = true;
    setIsLoadingDetail(true);
    setLoadError(null);
    const refresh = () => jobApi.getStatus(job.id)
      .then((detail) => {
        if (isMounted) {
          setDetailData(detail);
        }
      })
      .catch((err) => {
        if (isMounted) {
          setLoadError(
            err instanceof Error ? err.message : "Không thể tải thông tin chi tiết tác vụ."
          );
        }
      })
      .finally(() => {
        if (isMounted) {
          setIsLoadingDetail(false);
        }
      });
    void refresh();
    const timer = window.setInterval(() => { void refresh(); }, 2000);

    return () => {
      isMounted = false;
      window.clearInterval(timer);
    };
  }, [open, job]);

  const copyToClipboard = (text: string, keyName: string) => {
    navigator.clipboard.writeText(text);
    setCopiedKey(keyName);
    setTimeout(() => {
      setCopiedKey(null);
    }, 2000);
  };

  if (!job) return null;

  // Hợp nhất dữ liệu hiển thị (ưu tiên detailData mới nhất)
  const currentJob: JobDetailDto = {
    id: job.id,
    projectId: job.projectId,
    sceneId: job.sceneId,
    taskType: job.taskType,
    state: detailData?.state ?? job.state,
    reasonCode: detailData?.reasonCode ?? job.reasonCode,
    providerKey: detailData?.providerKey ?? ("providerKey" in job ? (job as JobDetailDto).providerKey : "Đang kiểm tra..."),
    storageRootId: detailData?.storageRootId ?? ("storageRootId" in job ? (job as JobDetailDto).storageRootId : "Mặc định"),
    attempts: detailData?.attempts ?? job.attempts,
    maxAttempts: detailData?.maxAttempts ?? ("maxAttempts" in job ? (job as JobDetailDto).maxAttempts : 3),
    sideEffectSubmitted: detailData?.sideEffectSubmitted ?? ("sideEffectSubmitted" in job ? (job as JobDetailDto).sideEffectSubmitted : 0),
    resultReference: detailData?.resultReference ?? ("resultReference" in job ? (job as JobDetailDto).resultReference : null),
    resultText: detailData?.resultText ?? ("resultText" in job ? (job as JobDetailDto).resultText : null),
    cancelRequested: detailData?.cancelRequested ?? ("cancelRequested" in job ? Boolean((job as JobDetailDto).cancelRequested) : false),
    revision: detailData?.revision ?? job.revision ?? 1,
    createdAtUtc: detailData?.createdAtUtc ?? job.createdAtUtc,
    updatedAtUtc: detailData?.updatedAtUtc ?? ("updatedAtUtc" in job ? (job as JobDetailDto).updatedAtUtc : job.createdAtUtc),
    activity: detailData?.activity ?? ("activity" in job ? (job as JobDetailDto).activity : []),
  };

  const taskTypeInfo = getTaskTypeInfo(currentJob.taskType);
  const stateBadgeInfo = getStateBadgeInfo(currentJob.state, currentJob.cancelRequested);
  const recommendedAction = getRecommendedAction(currentJob);
  const formattedReason = formatReasonCode(currentJob.reasonCode);
  const TaskIcon = taskTypeInfo.icon;
  const StateIcon = stateBadgeInfo.icon;

  const isCancelable =
    (currentJob.state === "Queued" ||
      currentJob.state === "Reserved" ||
      currentJob.state === "Running" ||
      currentJob.state === "AwaitingUser") &&
    !currentJob.cancelRequested;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl max-h-[88vh] flex flex-col p-0 gap-0 overflow-hidden sm:rounded-xl border-border bg-background">
        {/* Header Modal */}
        <DialogHeader className="p-5 pb-4 border-b border-border bg-muted/20">
          <div className="flex items-start justify-between gap-3 pr-6">
            <div className="flex items-center gap-3">
              <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg border border-border bg-card text-primary shadow-xs">
                <TaskIcon className="h-5 w-5" />
              </div>
              <div className="space-y-1">
                <DialogTitle className="text-base font-semibold text-foreground flex items-center gap-2">
                  <span>Chi tiết tác vụ: {taskTypeInfo.label}</span>
                  {isLoadingDetail && (
                    <Loader2 className="h-3.5 w-3.5 animate-spin text-muted-foreground" />
                  )}
                </DialogTitle>
                <DialogDescription className="text-xs text-muted-foreground flex items-center gap-2">
                  <span>Mã tác vụ:</span>
                  <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-foreground font-semibold">
                    {currentJob.id}
                  </code>
                </DialogDescription>
              </div>
            </div>

            <Badge
              variant={stateBadgeInfo.variant}
              className={cn("flex items-center gap-1.5 px-2.5 py-1 text-xs shrink-0", stateBadgeInfo.badgeClass)}
            >
              <StateIcon
                className={cn("h-3.5 w-3.5", stateBadgeInfo.animate && "animate-spin")}
              />
              <span>{stateBadgeInfo.label}</span>
            </Badge>
          </div>
        </DialogHeader>

        <ScrollArea className="flex-1 p-5 overflow-y-auto">
          <div className="flex flex-col gap-5">
            {/* Cảnh báo trạng thái đặc biệt */}
            {currentJob.cancelRequested && currentJob.state !== "Canceled" && (
              <Alert variant="warning" className="border-warning/50 bg-warning/10 text-warning dark:text-warning">
                <AlertTriangle className="h-4 w-4 text-warning dark:text-warning" />
                <AlertTitle className="text-xs font-semibold">Đang xử lý hủy an toàn</AlertTitle>
                <AlertDescription className="text-xs">
                  Yêu cầu hủy đã được tiếp nhận. Hệ thống đang tiến hành đối soát và giải phóng tài nguyên với provider AI để tránh thất thoát chi phí.
                </AlertDescription>
              </Alert>
            )}

            {formattedReason && (
              <Alert
                variant={currentJob.state === "Failed" ? "destructive" : "warning"}
                className={cn(
                  "text-xs",
                  currentJob.state === "AwaitingUser" &&
                    "border-warning/50 bg-warning/10 text-warning dark:text-warning"
                )}
              >
                <AlertTriangle className="h-4 w-4" />
                <AlertTitle className="text-xs font-semibold">
                  {currentJob.state === "Failed" ? "Lý do thất bại" : "Lý do cần can thiệp"}
                </AlertTitle>
                <AlertDescription className="text-xs leading-relaxed">
                  {formattedReason}
                </AlertDescription>
              </Alert>
            )}

            {loadError && (
              <Alert variant="destructive" className="text-xs">
                <AlertTitle>Lỗi tải thông tin chi tiết</AlertTitle>
                <AlertDescription>{loadError}</AlertDescription>
              </Alert>
            )}

            <section className="rounded-lg border border-border bg-card p-4 text-xs" aria-label="Nhật ký hoạt động">
              <h4 className="font-semibold text-foreground mb-3">Nhật ký hoạt động</h4>
              {currentJob.activity?.length ? (
                <ol className="space-y-2">
                  {currentJob.activity.map((entry, index) => (
                    <li key={`${entry.atUtc}-${index}`} className="flex gap-2">
                      <time className="shrink-0 text-muted-foreground">{formatFullDateTime(entry.atUtc)}</time>
                      <span>{entry.message}</span>
                    </li>
                  ))}
                </ol>
              ) : <p className="text-muted-foreground">Chưa có nhật ký cho tác vụ này.</p>}
            </section>

            {/* Nhóm 1: Định danh & Ngữ cảnh dự án */}
            <div className="rounded-lg border border-border bg-card p-4 text-xs">
              <h4 className="font-semibold text-foreground mb-3 flex items-center gap-1.5">
                <Hash className="h-4 w-4 text-primary" />
                <span>Định danh & Không gian làm việc</span>
              </h4>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3.5">
                <div className="space-y-1">
                  <span className="text-muted-foreground">ID tác vụ (Job ID)</span>
                  <div className="flex items-center gap-1.5">
                    <span className="font-mono text-foreground select-all break-all">
                      {currentJob.id}
                    </span>
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      className="h-6 w-6 shrink-0 cursor-pointer"
                      onClick={() => copyToClipboard(currentJob.id, "jobId")}
                      title="Sao chép ID"
                    >
                      {copiedKey === "jobId" ? (
                        <Check className="h-3 w-3 text-success" />
                      ) : (
                        <Copy className="h-3 w-3 text-muted-foreground" />
                      )}
                    </Button>
                  </div>
                </div>

                <div className="space-y-1">
                  <span className="text-muted-foreground">Dự án (Project ID)</span>
                  <div className="flex items-center gap-2">
                    <span className="font-mono text-foreground">
                      {currentJob.projectId}
                    </span>
                    {onNavigateToJob && (
                      <Button
                        type="button"
                        variant="link"
                        size="sm"
                        className="h-auto p-0 text-primary text-xs cursor-pointer inline-flex items-center gap-1"
                        onClick={() => {
                          onNavigateToJob(currentJob);
                          onOpenChange(false);
                        }}
                      >
                        <span>Đến dự án</span>
                        <ExternalLink className="h-3 w-3" />
                      </Button>
                    )}
                  </div>
                </div>

                <div className="space-y-1">
                  <span className="text-muted-foreground">Phân cảnh (Scene ID)</span>
                  <div className="flex items-center gap-2">
                    <span className="font-mono text-foreground">
                      {currentJob.sceneId ?? "Tác vụ toàn dự án"}
                    </span>
                    {currentJob.sceneId && onNavigateToJob && (
                      <Button
                        type="button"
                        variant="link"
                        size="sm"
                        className="h-auto p-0 text-primary text-xs cursor-pointer inline-flex items-center gap-1"
                        onClick={() => {
                          onNavigateToJob(currentJob);
                          onOpenChange(false);
                        }}
                      >
                        <span>Đến phân cảnh</span>
                        <ExternalLink className="h-3 w-3" />
                      </Button>
                    )}
                  </div>
                </div>
              </div>
            </div>

            {/* Nhóm 2: Nhà cung cấp & Lưu trữ & Side Effect */}
            <div className="rounded-lg border border-border bg-card p-4 text-xs">
              <h4 className="font-semibold text-foreground mb-3 flex items-center gap-1.5">
                <Server className="h-4 w-4 text-primary" />
                <span>Nhà cung cấp & An toàn thực thi</span>
              </h4>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3.5">
                <div className="space-y-1">
                  <span className="text-muted-foreground">Nhà cung cấp AI (Provider)</span>
                  <div className="font-medium text-foreground">
                    {currentJob.providerKey}
                  </div>
                </div>

                <div className="space-y-1">
                  <span className="text-muted-foreground">Thư mục gốc lưu trữ (Storage Root)</span>
                  <div className="font-mono text-foreground break-all">
                    {currentJob.storageRootId}
                  </div>
                </div>

                <div className="space-y-1">
                  <span className="text-muted-foreground">Số lần thử lại (Attempts)</span>
                  <div className="font-medium text-foreground">
                    {currentJob.attempts} / {currentJob.maxAttempts} lần
                  </div>
                </div>

                <div className="space-y-1">
                  <span className="text-muted-foreground">Yêu cầu đã gửi tới AI (Side-Effect)</span>
                  <div className="flex items-center gap-1.5">
                    {currentJob.sideEffectSubmitted === 1 ? (
                      <Badge variant="warning" className="gap-1 border-warning/40 bg-warning/10 text-warning dark:text-warning">
                        <ShieldAlert className="h-3 w-3" />
                        <span>Đã gửi (Cần đối soát an toàn)</span>
                      </Badge>
                    ) : (
                      <Badge variant="secondary" className="gap-1 border-border text-muted-foreground">
                        <ShieldCheck className="h-3 w-3 text-success" />
                        <span>Chưa gửi (An toàn để hủy / thử lại)</span>
                      </Badge>
                    )}
                  </div>
                </div>
              </div>
            </div>

            {/* Nhóm 3: Kết quả thực thi (nếu có) */}
            {(currentJob.resultReference || currentJob.resultText) && (
              <div className="rounded-lg border border-border bg-card p-4 text-xs">
                <h4 className="font-semibold text-foreground mb-3 flex items-center gap-1.5">
                  <FolderGit2 className="h-4 w-4 text-primary" />
                  <span>Kết quả xử lý</span>
                </h4>
                <div className="space-y-3">
                  {currentJob.resultReference && (
                    <div className="space-y-1">
                      <span className="text-muted-foreground">Mã tham chiếu / URL kết quả:</span>
                      <div className="flex items-center gap-1.5 rounded-md border border-border bg-muted/40 p-2 font-mono text-foreground break-all">
                        <span className="flex-1 select-all">{currentJob.resultReference}</span>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="h-6 w-6 shrink-0 cursor-pointer"
                          onClick={() => copyToClipboard(currentJob.resultReference!, "resultRef")}
                          title="Sao chép kết quả"
                        >
                          {copiedKey === "resultRef" ? (
                            <Check className="h-3 w-3 text-success" />
                          ) : (
                            <Copy className="h-3 w-3 text-muted-foreground" />
                          )}
                        </Button>
                      </div>
                    </div>
                  )}

                  {currentJob.resultText && (
                    <div className="space-y-1">
                      <span className="text-muted-foreground">Nội dung văn bản kết quả:</span>
                      <pre className="max-h-40 overflow-y-auto whitespace-pre-wrap rounded-md border border-border bg-muted/30 p-2.5 font-mono text-xs text-foreground">
                        {currentJob.resultText}
                      </pre>
                    </div>
                  )}
                </div>
              </div>
            )}

            {/* Nhóm 4: Thời gian */}
            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 rounded-lg border border-border bg-muted/20 px-3.5 py-2.5 text-xs text-muted-foreground">
              <div className="flex items-center gap-1.5">
                <Clock className="h-3.5 w-3.5" />
                <span>Khởi tạo:</span>
                <span className="font-medium text-foreground">
                  {formatFullDateTime(currentJob.createdAtUtc)}
                </span>
              </div>
              <div className="flex items-center gap-1.5">
                <span>Cập nhật:</span>
                <span className="font-medium text-foreground">
                  {formatFullDateTime(currentJob.updatedAtUtc)}
                </span>
              </div>
            </div>
          </div>
        </ScrollArea>

        {/* Footer Modal: Các nút hành động */}
        <DialogFooter className="p-4 border-t border-border bg-muted/20 flex flex-row items-center justify-between sm:justify-between gap-2">
          <div>
            {isCancelable && (
              <Button
                type="button"
                variant="destructive"
                size="sm"
                disabled={isActionLoading}
                onClick={() => {
                  onAction?.("cancel", currentJob);
                  onOpenChange(false);
                }}
                className="gap-1.5 text-xs cursor-pointer"
              >
                <Ban className="h-3.5 w-3.5" />
                <span>Hủy tác vụ</span>
              </Button>
            )}
          </div>

          <div className="flex items-center gap-2">
            {recommendedAction.type === "login" && (
              <Button
                type="button"
                variant="default"
                size="sm"
                disabled={isActionLoading}
                onClick={() => {
                  onAction?.("login", currentJob);
                  onOpenChange(false);
                }}
                className="gap-1.5 text-xs cursor-pointer shadow-xs"
              >
                <LogIn className="h-3.5 w-3.5" />
                <span>Đăng nhập ngay</span>
              </Button>
            )}

            {recommendedAction.type === "navigate_settings_accounts" && (
              <Button
                type="button"
                variant="default"
                size="sm"
                disabled={isActionLoading}
                onClick={() => {
                  onAction?.("navigate_settings_accounts", currentJob);
                  onOpenChange(false);
                }}
                className="gap-1.5 text-xs cursor-pointer shadow-xs bg-primary"
              >
                <Settings className="h-3.5 w-3.5" />
                <span>{recommendedAction.label}</span>
              </Button>
            )}

            {recommendedAction.type === "reconcile" && (
              <Button
                type="button"
                variant="default"
                size="sm"
                disabled={isActionLoading}
                onClick={() => {
                  onOpenReconcile?.(currentJob);
                  onOpenChange(false);
                }}
                className="gap-1.5 text-xs bg-warning hover:bg-warning text-white cursor-pointer shadow-xs"
              >
                <SearchCheck className="h-3.5 w-3.5" />
                <span>Đối soát tác vụ</span>
              </Button>
            )}


            {recommendedAction.type === "resume" && (
              <Button
                type="button"
                variant="default"
                size="sm"
                disabled={isActionLoading}
                onClick={() => {
                  onAction?.("resume", currentJob);
                  onOpenChange(false);
                }}
                className="gap-1.5 text-xs cursor-pointer shadow-xs"
              >
                <Play className="h-3.5 w-3.5" />
                <span>Tiếp tục</span>
              </Button>
            )}

            {recommendedAction.type === "retry" && (
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={isActionLoading}
                onClick={() => {
                  onAction?.("retry", currentJob);
                  onOpenChange(false);
                }}
                className="gap-1.5 text-xs cursor-pointer"
              >
                <RotateCcw className="h-3.5 w-3.5" />
                <span>Thử lại</span>
              </Button>
            )}

            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => onOpenChange(false)}
              className="text-xs cursor-pointer"
            >
              Đóng
            </Button>
          </div>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
