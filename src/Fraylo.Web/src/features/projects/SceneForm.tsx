import React, { useState, useEffect, useRef } from "react";
import { projectApi, type SceneDto } from "./projectApi";
import type { ErrorDto } from "../../contracts/bridge";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { Alert, AlertTitle, AlertDescription } from "@/components/ui/alert";
import { AlertTriangle, Loader2, RefreshCw } from "lucide-react";

export interface SceneFormProps {
  isOpen: boolean;
  mode: "create" | "edit";
  projectId: string;
  scene?: SceneDto | null;
  onClose: () => void;
  onSuccess: () => void;
}

export function SceneForm({
  isOpen,
  mode,
  projectId,
  scene,
  onClose,
  onSuccess,
}: SceneFormProps) {
  const [name, setName] = useState("");
  const [promptText, setPromptText] = useState("");
  const [targetDurationSeconds, setTargetDurationSeconds] = useState<number>(15);
  const [status, setStatus] = useState("Draft");

  // Lưu trữ revision phục vụ kiểm soát xung đột đồng thời (optimistic concurrency)
  const [initialRevision, setInitialRevision] = useState<string | null>(null);

  const [isSubmitting, setIsSubmitting] = useState(false);
  const [isReloading, setIsReloading] = useState(false);
  const [isConflict, setIsConflict] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});

  const nameInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (isOpen) {
      if (mode === "edit" && scene) {
        setName(scene.name || "");
        setPromptText(scene.promptText || "");
        setTargetDurationSeconds(scene.targetDurationSeconds || 15);
        setStatus(scene.status || "Draft");
        setInitialRevision(scene.revision);
      } else {
        setName("");
        setPromptText("");
        setTargetDurationSeconds(15);
        setStatus("Draft");
        setInitialRevision(null);
      }
      setIsConflict(false);
      setErrorMessage(null);
      setFieldErrors({});
      setIsSubmitting(false);

      setTimeout(() => {
        nameInputRef.current?.focus();
      }, 50);
    }
  }, [isOpen, mode, scene]);

  const validate = (): boolean => {
    const errors: Record<string, string> = {};
    const trimmed = name.trim();
    if (!trimmed) {
      errors.name = "Vui lòng nhập tên phân cảnh.";
    } else if (trimmed.length > 100) {
      errors.name = "Tên phân cảnh không được vượt quá 100 ký tự.";
    }

    if (!targetDurationSeconds || ![10, 15].includes(Number(targetDurationSeconds))) {
      errors.duration = "Vui lòng chọn thời lượng được nhà cung cấp hỗ trợ (10 giây hoặc 15 giây).";
    }

    setFieldErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (isSubmitting || isReloading) return;

    if (!validate()) return;

    setIsSubmitting(true);
    setErrorMessage(null);
    setIsConflict(false);

    const trimmedName = name.trim();
    const trimmedPrompt = promptText.trim() || null;

    try {
      if (mode === "create") {
        await projectApi.createScene({
          projectId,
          name: trimmedName,
          promptText: trimmedPrompt,
          targetDurationSeconds,
        });
        onSuccess();
      } else if (mode === "edit" && scene) {
        // GỬI KÈM expectedRevision: revision ban đầu
        await projectApi.updateScene({
          projectId,
          sceneId: scene.sceneId,
          expectedRevision: initialRevision,
          name: trimmedName,
          promptText: trimmedPrompt,
          targetDurationSeconds,
          status,
        });
        onSuccess();
      }
    } catch (err: unknown) {
      const errorDto = err as ErrorDto;
      console.error("[SceneForm] Lỗi lưu Cảnh:", errorDto);

      if (errorDto?.code === "CONCURRENCY_CONFLICT") {
        // QUY TẮC BẮT BUỘC: Hiển thị cảnh báo xung đột phiên, GIỮ NGUYÊN FORM INPUT
        setIsConflict(true);
        setErrorMessage(
          "Cảnh này đã bị chỉnh sửa bởi một phiên khác. Vui lòng tải lại dữ liệu mới nhất trước khi lưu.",
        );
      } else {
        setErrorMessage(
          errorDto?.message || "Đã xảy ra lỗi khi lưu thông tin phân cảnh. Vui lòng thử lại.",
        );
      }
    } finally {
      setIsSubmitting(false);
    }
  };

  // Nạp lại phiên bản mới nhất từ Host để giải quyết xung đột mà không mất nội dung đã nhập
  const handleReloadLatest = async () => {
    if (!scene) return;
    setIsReloading(true);
    setErrorMessage(null);

    try {
      const latestScene = await projectApi.getScene(projectId, scene.sceneId);
      // Cập nhật lại initialRevision thành revision mới nhất từ server
      setInitialRevision(latestScene.revision);
      setIsConflict(false);
      setErrorMessage(null);
    } catch (err: unknown) {
      const errorDto = err as ErrorDto;
      setErrorMessage(
        `Không thể nạp dữ liệu mới nhất: ${errorDto?.message || "Lỗi kết nối máy chủ"}`,
      );
    } finally {
      setIsReloading(false);
    }
  };

  return (
    <Dialog
      open={isOpen}
      onOpenChange={(open) => {
        if (!open && !isSubmitting && !isReloading) {
          onClose();
        }
      }}
    >
      <DialogContent
        className="sm:max-w-[580px]"
        onPointerDownOutside={(e) => {
          if (isSubmitting || isReloading) e.preventDefault();
        }}
        onEscapeKeyDown={(e) => {
          if (isSubmitting || isReloading) e.preventDefault();
        }}
      >
        <DialogHeader>
          <DialogTitle id="scene-form-title">
            {mode === "create" ? "Thêm Phân Cảnh Mới" : `Chỉnh Sửa Phân Cảnh #${scene?.sceneSequence || ""}`}
          </DialogTitle>
          <DialogDescription>
            {mode === "create"
              ? "Thiết lập kịch bản lời dẫn, thời lượng và prompt tạo video."
              : `Revision hiện thời: ${initialRevision || "—"}`}
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={handleSubmit} className="space-y-4">
          {/* Banner Cảnh Báo Xung Đột Đồng Thời (CONCURRENCY_CONFLICT) */}
          {isConflict && (
            <Alert variant="destructive" className="space-y-2">
              <div className="flex items-center gap-2">
                <AlertTriangle className="h-4 w-4" />
                <AlertTitle className="font-semibold">
                  Xung đột phiên chỉnh sửa đồng thời (Concurrency Conflict)!
                </AlertTitle>
              </div>
              <AlertDescription className="text-xs leading-relaxed">
                Cảnh này đã bị chỉnh sửa bởi một phiên khác. Vui lòng tải lại dữ liệu mới nhất trước khi lưu.
                (Nội dung bạn vừa nhập ở dưới vẫn được giữ nguyên đầy đủ).
              </AlertDescription>
              <div className="pt-1">
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="bg-background text-foreground hover:bg-accent"
                  onClick={handleReloadLatest}
                  disabled={isReloading}
                >
                  <RefreshCw className={`mr-1.5 h-3.5 w-3.5 ${isReloading ? "animate-spin" : ""}`} />
                  Tải lại dữ liệu mới nhất từ máy chủ
                </Button>
              </div>
            </Alert>
          )}

          {/* Thông báo lỗi thông thường */}
          {errorMessage && !isConflict && (
            <Alert variant="destructive">
              <AlertTriangle className="h-4 w-4" />
              <AlertDescription>{errorMessage}</AlertDescription>
            </Alert>
          )}

          <div className="grid grid-cols-1 sm:grid-cols-[1fr_140px] gap-3">
            <div className="space-y-1.5">
              <Label htmlFor="scene-name" className="text-sm font-medium">
                Tiêu đề phân cảnh <span className="text-destructive">*</span>
              </Label>
              <Input
                id="scene-name"
                ref={nameInputRef}
                type="text"
                placeholder="Ví dụ: Giới thiệu sản phẩm, Cận cảnh áo len…"
                value={name}
                maxLength={100}
                disabled={isSubmitting || isReloading}
                onChange={(e) => {
                  setName(e.target.value);
                  if (fieldErrors.name) setFieldErrors((prev) => ({ ...prev, name: "" }));
                }}
                aria-invalid={!!fieldErrors.name}
              />
              {fieldErrors.name && (
                <p className="text-xs text-destructive">{fieldErrors.name}</p>
              )}
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="scene-duration" className="text-sm font-medium">
                Thời lượng clip <span className="text-destructive">*</span>
              </Label>
              <select
                id="scene-duration"
                className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                value={targetDurationSeconds}
                disabled={isSubmitting || isReloading}
                onChange={(e) => {
                  setTargetDurationSeconds(Number(e.target.value));
                  if (fieldErrors.duration) setFieldErrors((prev) => ({ ...prev, duration: "" }));
                }}
                aria-invalid={!!fieldErrors.duration}
              >
                <option value={15}>15 giây (Ưu tiên)</option>
                <option value={10}>10 giây</option>
                {![10, 15].includes(targetDurationSeconds) && (
                  <option value={targetDurationSeconds}>
                    {targetDurationSeconds} giây (Cần chọn lại thời lượng được hỗ trợ)
                  </option>
                )}
              </select>
              {fieldErrors.duration && (
                <p className="text-xs text-destructive">{fieldErrors.duration}</p>
              )}
            </div>
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="scene-prompt" className="text-sm font-medium">
              Lời dẫn (Narration) / Prompt nháp cho cảnh
            </Label>
            <Textarea
              id="scene-prompt"
              rows={4}
              className="resize-y"
              placeholder="Nhập lời dẫn thu âm hoặc mô tả hành động nhân vật cho AI sinh prompt video…"
              value={promptText}
              maxLength={2000}
              disabled={isSubmitting || isReloading}
              onChange={(e) => setPromptText(e.target.value)}
            />
            <div className="flex items-center justify-between text-xs text-muted-foreground">
              <span>Mô tả càng chi tiết sẽ giúp sinh video chuyển động mượt mà hơn.</span>
              <span>{promptText.length} / 2000</span>
            </div>
          </div>

          {mode === "edit" && (
            <div className="space-y-1.5">
              <Label htmlFor="scene-status" className="text-sm font-medium">
                Trạng thái phân cảnh
              </Label>
              <select
                id="scene-status"
                className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50 text-foreground"
                value={status}
                onChange={(e) => setStatus(e.target.value)}
                disabled={isSubmitting || isReloading}
              >
                <option value="Draft">Chưa bắt đầu (Draft)</option>
                <option value="PromptReady">Đã có Prompt (PromptReady)</option>
                <option value="Generating">Đang tạo video (Generating)</option>
                <option value="Completed">Hoàn thành (Completed)</option>
              </select>
            </div>
          )}

          <DialogFooter className="flex flex-col-reverse sm:flex-row sm:items-center sm:justify-between gap-2 pt-2 border-t border-border">
            <span className="text-xs text-muted-foreground">
              {isSubmitting
                ? "Đang lưu cảnh…"
                : isReloading
                ? "Đang nạp dữ liệu mới…"
                : "Bảo vệ xung đột đồng thời an toàn."}
            </span>
            <div className="flex items-center gap-2">
              <Button
                type="button"
                variant="outline"
                onClick={onClose}
                disabled={isSubmitting || isReloading}
              >
                Hủy
              </Button>
              <Button
                type="submit"
                disabled={isSubmitting || isReloading}
              >
                {isSubmitting ? (
                  <>
                    <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                    Đang lưu…
                  </>
                ) : mode === "create" ? (
                  "Thêm Phân Cảnh"
                ) : (
                  "Lưu Phân Cảnh"
                )}
              </Button>
            </div>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
