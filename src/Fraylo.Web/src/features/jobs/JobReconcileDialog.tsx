import { useState, useEffect, type FormEvent } from "react";
import {
  SearchCheck,
  CheckCircle2,
  XCircle,
  Ban,
  Loader2,
  ShieldAlert,
  Download,
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
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { Label } from "@/components/ui/label";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Checkbox } from "@/components/ui/checkbox";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { cn } from "@/lib/utils";
import {
  type JobSummaryDto,
  type JobDetailDto,
  type JobReconcileResponseData,
  getTaskTypeInfo,
} from "./jobTypes";
import { jobApi } from "./jobApi";

export interface JobReconcileDialogProps {
  job: (JobSummaryDto | JobDetailDto) | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onReconciled?: (result: JobReconcileResponseData) => void;
}

type ReconcileActionChoice = "mark_succeeded" | "mark_failed" | "cancel" | "recover_output";

export function JobReconcileDialog({
  job,
  open,
  onOpenChange,
  onReconciled,
}: JobReconcileDialogProps) {
  const isCanceledInitial = job ? job.state === "Canceled" || ("cancelRequested" in job && Boolean(job.cancelRequested)) : false;
  const [actionChoice, setActionChoice] =
    useState<ReconcileActionChoice>(isCanceledInitial ? "cancel" : "mark_succeeded");
  const [resultReference, setResultReference] = useState("");
  const [resultText, setResultText] = useState("");
  const [confirmedNotReceived, setConfirmedNotReceived] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  // Reset form khi mở dialog với job mới
  useEffect(() => {
    if (open && job) {
      const isCanceledJob = job.state === "Canceled" || ("cancelRequested" in job && Boolean(job.cancelRequested));
      setActionChoice(isCanceledJob ? "cancel" : "mark_succeeded");
      setResultReference("");
      setResultText("");
      setConfirmedNotReceived(false);
      setErrorMessage(null);
    }
  }, [open, job]);

  if (!job) return null;

  const taskTypeInfo = getTaskTypeInfo(job.taskType);
  const TaskIcon = taskTypeInfo.icon;
  // Video render chỉ chạy trên MuseAI; khôi phục cần tác vụ đang chờ đối soát.
  const canRecoverOutput = job.taskType === "VideoRender" && job.state === "AwaitingUser";

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!job) return;

    setErrorMessage(null);

    // Validation
    if (actionChoice === "mark_succeeded") {
      if (!resultReference.trim() && !resultText.trim()) {
        setErrorMessage(
          "Vui lòng cung cấp ít nhất một đường dẫn/mã kết quả (Result Reference) hoặc nội dung văn bản (Result Text)."
        );
        return;
      }
    } else if (actionChoice !== "recover_output") {
      if (!confirmedNotReceived) {
        setErrorMessage(
          "Vui lòng tích chọn xác nhận để đảm bảo an toàn trạng thái hàng đợi và tài nguyên."
        );
        return;
      }
    }

    setIsSubmitting(true);
    try {
      if (actionChoice === "recover_output") {
        onReconciled?.(await jobApi.recoverOutput(job.id));
        onOpenChange(false);
        return;
      }

      const res = await jobApi.reconcile({
        jobId: job.id,
        action: actionChoice,
        confirmedNotReceived:
          actionChoice !== "mark_succeeded" ? confirmedNotReceived : undefined,
        resultReference:
          actionChoice === "mark_succeeded" && resultReference.trim()
            ? resultReference.trim()
            : undefined,
        resultText:
          actionChoice === "mark_succeeded" && resultText.trim()
            ? resultText.trim()
            : undefined,
      });

      onReconciled?.(res);
      onOpenChange(false);
    } catch (err) {
      setErrorMessage(
        err instanceof Error
          ? err.message
          : "Đã xảy ra lỗi trong quá trình đối soát tác vụ."
      );
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-xl max-h-[90vh] flex flex-col p-0 gap-0 overflow-hidden sm:rounded-xl border-border bg-background">
        <form onSubmit={handleSubmit} className="flex flex-col h-full min-h-0">
          {/* Header */}
          <DialogHeader className="p-5 pb-4 border-b border-border bg-muted/20">
            <div className="flex items-start gap-3 pr-6">
              <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg border border-warning/30 bg-warning/10 text-warning dark:text-warning">
                <SearchCheck className="h-5 w-5" />
              </div>
              <div className="space-y-1">
                <DialogTitle className="text-base font-semibold text-foreground flex items-center gap-1.5">
                  <TaskIcon className="h-4 w-4 text-primary" />
                  <span>Đối soát tác vụ an toàn</span>
                </DialogTitle>
                <DialogDescription className="text-xs text-muted-foreground">
                  Tác vụ: <span className="font-semibold text-foreground">{taskTypeInfo.label}</span> (#{job.id.slice(0, 8)})
                </DialogDescription>
              </div>
            </div>
          </DialogHeader>

          {/* Body nội dung có thể cuộn */}
          <div className="flex-1 overflow-y-auto p-5 space-y-4">
            {/* Cảnh báo giải thích rõ ràng */}
            <Alert
              variant="warning"
              className="border-warning/40 bg-warning/10 text-warning dark:text-warning text-xs"
            >
              <ShieldAlert className="h-4 w-4 text-warning dark:text-warning" />
              <AlertTitle className="text-xs font-semibold">
                {job.state === "Canceled" ? "Đối soát giải phóng tài nguyên tác vụ đã hủy" : "Tại sao cần đối soát?"}
              </AlertTitle>
              <AlertDescription className="text-xs leading-relaxed mt-1">
                {job.state === "Canceled"
                  ? "Tác vụ này đã bị hủy bỏ bởi người dùng nhưng vẫn đang bảo lưu slot occupancy do đã phát sinh tác dụng phụ (side-effect) ra ngoài. Vui lòng đối soát để giải phóng tài nguyên trình duyệt hoặc thu hồi kết quả muộn."
                  : "Tác vụ này có thể đã được AI provider tiếp nhận trước khi kết nối bị gián đoạn. Để tránh lãng phí hạn ngạch (quota) hoặc bị bỏ sót kết quả đã tạo trên trang web AI, vui lòng kiểm tra lịch sử tạo nội dung của tài khoản AI và chọn phương án bên dưới."}
              </AlertDescription>
            </Alert>

            {errorMessage && (
              <Alert variant="destructive" className="text-xs">
                <AlertTitle>Lỗi đối soát</AlertTitle>
                <AlertDescription>{errorMessage}</AlertDescription>
              </Alert>
            )}

            {/* Lựa chọn các phương án đối soát */}
            <div className="space-y-3">
              <Label className="text-xs font-semibold text-foreground">
                Chọn kết quả kiểm tra thực tế:
              </Label>

              <RadioGroup
                value={actionChoice}
                onValueChange={(val) => {
                  setActionChoice(val as ReconcileActionChoice);
                  setErrorMessage(null);
                }}
                className="gap-2.5"
              >
                {/* Lựa chọn 1: Đã có kết quả */}
                <div
                  className={cn(
                    "flex flex-col gap-2 rounded-lg border p-3.5 transition-colors cursor-pointer",
                    actionChoice === "mark_succeeded"
                      ? "border-primary bg-primary/5 dark:bg-primary/10"
                      : "border-border hover:bg-muted/40"
                  )}
                  onClick={() => setActionChoice("mark_succeeded")}
                >
                  <div className="flex items-center gap-2.5">
                    <RadioGroupItem value="mark_succeeded" id="opt-succeeded" />
                    <Label
                      htmlFor="opt-succeeded"
                      className="font-medium text-xs text-foreground cursor-pointer flex items-center gap-1.5"
                    >
                      <CheckCircle2 className="h-3.5 w-3.5 text-success" />
                      <span>Đã có kết quả từ AI (Đánh dấu Thành công)</span>
                    </Label>
                  </div>
                  <p className="pl-6 text-[11px] text-muted-foreground leading-normal">
                    AI đã tạo nội dung thành công trên trang web. Nhập đường dẫn file hoặc nội dung bên dưới để lưu vào hệ thống.
                  </p>

                  {/* Form nhập khi chọn mark_succeeded */}
                  {actionChoice === "mark_succeeded" && (
                    <div className="mt-2 pl-6 space-y-2.5 border-t border-border/60 pt-2.5">
                      <div className="space-y-1">
                        <Label
                          htmlFor="res-ref"
                          className="text-[11px] text-muted-foreground"
                        >
                          Đường dẫn tệp / Mã tham chiếu (Result Reference):
                        </Label>
                        <Input
                          id="res-ref"
                          placeholder="Ví dụ: https://... hoặc mã bài viết/kết quả"
                          value={resultReference}
                          onChange={(e) => setResultReference(e.target.value)}
                          className="h-8 text-xs bg-background"
                        />
                      </div>

                      <div className="space-y-1">
                        <Label
                          htmlFor="res-text"
                          className="text-[11px] text-muted-foreground"
                        >
                          Nội dung văn bản kết quả (nếu có):
                        </Label>
                        <Textarea
                          id="res-text"
                          placeholder="Dán nội dung kết quả AI tạo ra..."
                          value={resultText}
                          onChange={(e) => setResultText(e.target.value)}
                          rows={2}
                          className="text-xs bg-background min-h-[50px]"
                        />
                      </div>
                    </div>
                  )}
                </div>

                {/* Lựa chọn khôi phục: video đã có trên MuseAI, chỉ cần tải lại */}
                {canRecoverOutput && (
                  <div
                    className={cn(
                      "flex flex-col gap-2 rounded-lg border p-3.5 transition-colors cursor-pointer",
                      actionChoice === "recover_output"
                        ? "border-primary bg-primary/5 dark:bg-primary/10"
                        : "border-border hover:bg-muted/40"
                    )}
                    onClick={() => setActionChoice("recover_output")}
                  >
                    <div className="flex items-center gap-2.5">
                      <RadioGroupItem value="recover_output" id="opt-recover" />
                      <Label
                        htmlFor="opt-recover"
                        className="font-medium text-xs text-foreground cursor-pointer flex items-center gap-1.5"
                      >
                        <Download className="h-3.5 w-3.5 text-primary" />
                        <span>Tải lại video từ phiên MuseAI đã lưu</span>
                      </Label>
                    </div>
                    <p className="pl-6 text-[11px] text-muted-foreground leading-normal">
                      Mở lại side chat MuseAI đã ghi nhận và tải video về. Không gửi lại prompt; tác vụ chỉ thành công khi video đã được lưu thành Asset.
                    </p>
                  </div>
                )}

                {/* Lựa chọn 2: AI chưa nhận - Đánh dấu thất bại để thử lại */}
                <div
                  className={cn(
                    "flex flex-col gap-2 rounded-lg border p-3.5 transition-colors cursor-pointer",
                    actionChoice === "mark_failed"
                      ? "border-primary bg-primary/5 dark:bg-primary/10"
                      : "border-border hover:bg-muted/40"
                  )}
                  onClick={() => setActionChoice("mark_failed")}
                >
                  <div className="flex items-center gap-2.5">
                    <RadioGroupItem value="mark_failed" id="opt-failed" />
                    <Label
                      htmlFor="opt-failed"
                      className="font-medium text-xs text-foreground cursor-pointer flex items-center gap-1.5"
                    >
                      <XCircle className="h-3.5 w-3.5 text-warning" />
                      <span>Xác nhận AI chưa nhận yêu cầu (Đánh dấu Thất bại để thử lại)</span>
                    </Label>
                  </div>
                  <p className="pl-6 text-[11px] text-muted-foreground leading-normal">
                    Đã kiểm tra lịch sử AI và thấy chưa có yêu cầu nào được xử lý hoặc trừ hạn ngạch. Cho phép thử lại tác vụ an toàn.
                  </p>

                  {/* Checkbox xác nhận */}
                  {actionChoice === "mark_failed" && (
                    <div className="mt-2 pl-6 pt-2 border-t border-border/60">
                      <div className="flex items-center gap-2">
                        <Checkbox
                          id="confirm-not-received-fail"
                          checked={confirmedNotReceived}
                          onCheckedChange={(checked) =>
                            setConfirmedNotReceived(Boolean(checked))
                          }
                        />
                        <Label
                          htmlFor="confirm-not-received-fail"
                          className="text-xs text-foreground cursor-pointer"
                        >
                          Tôi xác nhận đã kiểm tra và AI chưa tiếp nhận yêu cầu này
                        </Label>
                      </div>
                    </div>
                  )}
                </div>

                {/* Lựa chọn 3: Hủy an toàn */}
                <div
                  className={cn(
                    "flex flex-col gap-2 rounded-lg border p-3.5 transition-colors cursor-pointer",
                    actionChoice === "cancel"
                      ? "border-primary bg-primary/5 dark:bg-primary/10"
                      : "border-border hover:bg-muted/40"
                  )}
                  onClick={() => setActionChoice("cancel")}
                >
                  <div className="flex items-center gap-2.5">
                    <RadioGroupItem value="cancel" id="opt-cancel" />
                    <Label
                      htmlFor="opt-cancel"
                      className="font-medium text-xs text-foreground cursor-pointer flex items-center gap-1.5"
                    >
                      <Ban className="h-3.5 w-3.5 text-destructive" />
                      <span>Hủy tác vụ an toàn (Giải phóng tài nguyên)</span>
                    </Label>
                  </div>
                  <p className="pl-6 text-[11px] text-muted-foreground leading-normal">
                    Hủy bỏ tác vụ này vĩnh viễn và giải phóng chiếm dụng (occupancy) tài khoản trình duyệt ngay lập tức.
                  </p>

                  {/* Checkbox xác nhận */}
                  {actionChoice === "cancel" && (
                    <div className="mt-2 pl-6 pt-2 border-t border-border/60">
                      <div className="flex items-center gap-2">
                        <Checkbox
                          id="confirm-cancel"
                          checked={confirmedNotReceived}
                          onCheckedChange={(checked) =>
                            setConfirmedNotReceived(Boolean(checked))
                          }
                        />
                        <Label
                          htmlFor="confirm-cancel"
                          className="text-xs text-foreground cursor-pointer"
                        >
                          Xác nhận hủy tác vụ và giải phóng giữ chỗ trình duyệt
                        </Label>
                      </div>
                    </div>
                  )}
                </div>
              </RadioGroup>
            </div>
          </div>

          {/* Footer */}
          <DialogFooter className="p-4 border-t border-border bg-muted/20 flex flex-row items-center justify-end gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={isSubmitting}
              onClick={() => onOpenChange(false)}
              className="text-xs cursor-pointer"
            >
              Hủy bỏ
            </Button>
            <Button
              type="submit"
              size="sm"
              disabled={isSubmitting}
              className="gap-1.5 text-xs cursor-pointer shadow-xs"
            >
              {isSubmitting ? (
                <>
                  <Loader2 className="h-3.5 w-3.5 animate-spin" />
                  <span>Đang xử lý...</span>
                </>
              ) : (
                <>
                  <CheckCircle2 className="h-3.5 w-3.5" />
                  <span>Xác nhận đối soát</span>
                </>
              )}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
