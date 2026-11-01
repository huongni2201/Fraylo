import { useEffect, useState } from "react";
import { projectGroupApi, type ProjectGroupDto } from "./projectApi";
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
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { AlertCircle, Loader2 } from "lucide-react";

export interface ProjectGroupCreateDialogProps {
  isOpen: boolean;
  onClose: () => void;
  /** Nhóm vừa tạo; cấu hình prompt và tệp dùng chung được đặt sau trong trang nhóm. */
  onCreated: (group: ProjectGroupDto) => Promise<void>;
}

export function ProjectGroupCreateDialog({ isOpen, onClose, onCreated }: ProjectGroupCreateDialogProps) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [isSaving, setIsSaving] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  useEffect(() => {
    if (isOpen) {
      setName("");
      setDescription("");
      setErrorMessage(null);
    }
  }, [isOpen]);

  const handleCreate = async () => {
    const trimmedName = name.trim();
    if (!trimmedName) {
      setErrorMessage("Vui lòng nhập tên nhóm.");
      return;
    }

    setIsSaving(true);
    setErrorMessage(null);
    try {
      const group = await projectGroupApi.saveGroup({ name: trimmedName, description: description.trim() });
      await onCreated(group);
    } catch (err: unknown) {
      setErrorMessage((err as ErrorDto)?.message || "Không tạo được nhóm. Vui lòng thử lại.");
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <Dialog
      open={isOpen}
      onOpenChange={(open) => {
        if (!open && !isSaving) onClose();
      }}
    >
      <DialogContent className="sm:max-w-[440px]">
        <form
          className="space-y-5"
          onSubmit={(e) => {
            e.preventDefault();
            void handleCreate();
          }}
        >
          <DialogHeader>
            <DialogTitle>Tạo nhóm dự án</DialogTitle>
            <DialogDescription>
              Nhóm gom các video cùng ngành hàng. Hướng dẫn prompt và tệp dùng chung được cấu hình trong trang nhóm sau khi tạo.
            </DialogDescription>
          </DialogHeader>

          {errorMessage && (
            <Alert variant="destructive">
              <AlertCircle className="h-4 w-4" />
              <AlertDescription>{errorMessage}</AlertDescription>
            </Alert>
          )}

          <div className="space-y-4">
            <div className="space-y-1.5">
              <Label htmlFor="grp-create-name" className="text-sm font-medium">
                Tên nhóm <span className="text-destructive">*</span>
              </Label>
              <Input
                id="grp-create-name"
                autoFocus
                value={name}
                maxLength={100}
                disabled={isSaving}
                placeholder="Ví dụ: Quần áo nữ"
                onChange={(e) => setName(e.target.value)}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="grp-create-desc" className="text-sm font-medium">Mô tả</Label>
              <Input
                id="grp-create-desc"
                value={description}
                maxLength={500}
                disabled={isSaving}
                onChange={(e) => setDescription(e.target.value)}
              />
            </div>
          </div>

          <DialogFooter>
            <Button type="button" variant="outline" disabled={isSaving} onClick={onClose}>
              Hủy
            </Button>
            <Button type="submit" disabled={isSaving}>
              {isSaving ? (
                <>
                  <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                  Đang tạo…
                </>
              ) : (
                "Tạo nhóm"
              )}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
