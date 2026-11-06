import React, { useState, useEffect, useRef } from "react";
import {
  projectApi,
  type ProjectDetailDto,
  type ProjectSummaryDto,
} from "./projectApi";
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
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Loader2, AlertCircle } from "lucide-react";

export interface ProjectFormProps {
  isOpen: boolean;
  mode: "create" | "edit";
  project?: ProjectDetailDto | ProjectSummaryDto | null;
  onClose: () => void;
  onSuccess: (projectId: string) => void;
}

export function ProjectForm({
  isOpen,
  mode,
  project,
  onClose,
  onSuccess,
}: ProjectFormProps) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [status, setStatus] = useState("Draft");
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});

  const nameInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (isOpen) {
      if (mode === "edit" && project) {
        setName(project.name || "");
        setDescription(project.description || "");
        setStatus(project.status || "Draft");
      } else {
        setName("");
        setDescription("");
        setStatus("Draft");
      }
      setErrorMessage(null);
      setFieldErrors({});
      setIsSubmitting(false);

      setTimeout(() => {
        nameInputRef.current?.focus();
      }, 50);
    }
  }, [isOpen, mode, project]);

  const validate = (): boolean => {
    const errors: Record<string, string> = {};
    const trimmed = name.trim();
    if (!trimmed) {
      errors.name = "Vui lòng nhập tên dự án video.";
    } else if (trimmed.length > 150) {
      errors.name = "Tên dự án không được vượt quá 150 ký tự.";
    }

    setFieldErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (isSubmitting) return;

    if (!validate()) return;

    setIsSubmitting(true);
    setErrorMessage(null);

    const trimmedName = name.trim();
    const trimmedDesc = description.trim() || null;

    try {
      if (mode === "create") {
        const newProjectId = await projectApi.createProject({
          name: trimmedName,
          description: trimmedDesc,
        });
        onSuccess(newProjectId);
      } else if (mode === "edit" && project) {
        const updated = await projectApi.updateProject({
          projectId: project.projectId,
          name: trimmedName,
          description: trimmedDesc,
          status,
        });
        onSuccess(updated.projectId);
      }
    } catch (err: unknown) {
      const errorDto = err as ErrorDto;
      console.error("[ProjectForm] Lỗi lưu dự án:", errorDto);
      setErrorMessage(
        errorDto?.message || "Đã xảy ra lỗi khi lưu thông tin dự án. Vui lòng thử lại.",
      );
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <Dialog
      open={isOpen}
      onOpenChange={(open) => {
        if (!open && !isSubmitting) {
          onClose();
        }
      }}
    >
      <DialogContent
        className="sm:max-w-[540px]"
        onPointerDownOutside={(e) => {
          if (isSubmitting) e.preventDefault();
        }}
        onEscapeKeyDown={(e) => {
          if (isSubmitting) e.preventDefault();
        }}
      >
        <DialogHeader>
          <DialogTitle id="project-form-title">
            {mode === "create" ? "Tạo video mới" : "Chỉnh sửa dự án video"}
          </DialogTitle>
          <DialogDescription>
            {mode === "create"
              ? "Dự án chứa kịch bản phân cảnh và tài nguyên video hoàn chỉnh."
              : `Cập nhật thông tin dự án ${project?.name || ""}.`}
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={handleSubmit} className="space-y-4">
          {errorMessage && (
            <Alert variant="destructive">
              <AlertCircle className="h-4 w-4" />
              <AlertDescription>{errorMessage}</AlertDescription>
            </Alert>
          )}

          <div className="space-y-1.5">
            <Label htmlFor="prj-name" className="text-sm font-medium">
              Tiêu đề dự án <span className="text-destructive">*</span>
            </Label>
            <Input
              id="prj-name"
              ref={nameInputRef}
              type="text"
              placeholder="Ví dụ: Outfit Thu Đông - Áo Len Xinh Xắn"
              value={name}
              maxLength={150}
              disabled={isSubmitting}
              onChange={(e) => {
                setName(e.target.value);
                if (fieldErrors.name) setFieldErrors((prev) => ({ ...prev, name: "" }));
              }}
              aria-invalid={!!fieldErrors.name}
            />
            {fieldErrors.name ? (
              <p className="text-xs text-destructive">{fieldErrors.name}</p>
            ) : (
              <p className="text-xs text-muted-foreground">Tối đa 150 ký tự.</p>
            )}
          </div>

          <div className="space-y-1.5">
            <Label htmlFor="prj-desc" className="text-sm font-medium">
              Mô tả tóm tắt nội dung
            </Label>
            <Textarea
              id="prj-desc"
              rows={3}
              className="resize-y"
              placeholder="Tóm tắt ý tưởng, chủ đề hoặc thông điệp chính của video…"
              value={description}
              maxLength={500}
              disabled={isSubmitting}
              onChange={(e) => setDescription(e.target.value)}
            />
            <p className="text-xs text-muted-foreground">Tùy chọn, tối đa 500 ký tự.</p>
          </div>

          {mode === "edit" && (
            <div className="space-y-1.5">
              <Label htmlFor="prj-status" className="text-sm font-medium">
                Trạng thái sản xuất
              </Label>
              <select
                id="prj-status"
                className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50 text-foreground"
                value={status}
                onChange={(e) => setStatus(e.target.value)}
                disabled={isSubmitting}
              >
                <option value="Draft">Nháp (Draft)</option>
                <option value="InProgress">Đang sản xuất (InProgress)</option>
                <option value="Completed">Hoàn thành (Completed)</option>
              </select>
            </div>
          )}

          <DialogFooter className="flex flex-col-reverse sm:flex-row sm:items-center sm:justify-between gap-2 pt-2 border-t border-border">
            <span className="text-xs text-muted-foreground">
              {isSubmitting ? "Đang xử lý…" : "Lưu vào cơ sở dữ liệu SQLite cục bộ."}
            </span>
            <div className="flex items-center gap-2">
              <Button
                type="button"
                variant="outline"
                onClick={onClose}
                disabled={isSubmitting}
              >
                Hủy
              </Button>
              <Button type="submit" disabled={isSubmitting}>
                {isSubmitting ? (
                  <>
                    <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                    Đang lưu…
                  </>
                ) : mode === "create" ? (
                  "Tạo video"
                ) : (
                  "Lưu thay đổi"
                )}
              </Button>
            </div>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
