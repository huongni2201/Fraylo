import React, { useState, useEffect, useCallback } from "react";
import { Check, FolderOpen, Info, AlertCircle, Loader2 } from "lucide-react";
import {
  storageSettingsApi,
  type SettingsDto,
  type SettingsSaveRequest,
} from "./storageSettingsApi";
import { cn } from "../../lib/utils";

export interface StorageSettingsPanelProps {
  onDirtyChange?: (isDirty: boolean) => void;
  resetSignal?: number;
}

// shadcn UI-compatible primitives
function Button({
  className,
  variant = "default",
  size = "default",
  ...props
}: React.ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "default" | "outline" | "secondary" | "destructive" | "ghost";
  size?: "default" | "sm" | "lg" | "icon";
}) {
  const variantClasses = {
    default: "bg-primary text-primary-foreground shadow-sm hover:bg-primary/90",
    outline: "border border-input bg-background shadow-xs hover:bg-accent hover:text-accent-foreground",
    secondary: "bg-secondary text-secondary-foreground shadow-xs hover:bg-secondary/80",
    destructive: "bg-destructive text-destructive-foreground shadow-xs hover:bg-destructive/90",
    ghost: "hover:bg-accent hover:text-accent-foreground",
  };
  const sizeClasses = {
    default: "h-9 px-4 py-2",
    sm: "h-8 rounded-md px-3 text-xs",
    lg: "h-10 rounded-md px-8",
    icon: "h-9 w-9",
  };
  return (
    <button
      className={cn(
        "inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-md text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 ring-offset-background disabled:pointer-events-none disabled:opacity-50 [&_svg]:pointer-events-none [&_svg]:shrink-0",
        variantClasses[variant],
        sizeClasses[size],
        className
      )}
      {...props}
    />
  );
}

function Alert({
  className,
  variant = "default",
  ...props
}: React.HTMLAttributes<HTMLDivElement> & {
  variant?: "default" | "destructive" | "warning" | "info" | "success";
}) {
  const variantClasses = {
    default: "bg-background text-foreground border-border",
    destructive: "border-destructive/50 text-destructive dark:border-destructive [&>svg]:text-destructive",
    warning: "border-warning/50 text-warning dark:text-warning [&>svg]:text-warning",
    info: "border-info/50 text-info dark:text-info [&>svg]:text-info",
    success: "border-success/50 text-success dark:text-success [&>svg]:text-success",
  };
  return (
    <div
      role="alert"
      className={cn(
        "relative w-full rounded-lg border px-4 py-3 text-sm [&>svg+div]:translate-y-[-3px] [&>svg]:absolute [&>svg]:left-4 [&>svg]:top-4 [&>svg]:text-foreground [&>svg~*]:pl-7",
        variantClasses[variant],
        className
      )}
      {...props}
    />
  );
}

function Label({ className, ...props }: React.LabelHTMLAttributes<HTMLLabelElement>) {
  return (
    <label
      className={cn(
        "text-sm font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70",
        className
      )}
      {...props}
    />
  );
}

export function StorageSettingsPanel({ onDirtyChange, resetSignal }: StorageSettingsPanelProps) {
  const [savedSettings, setSavedSettings] = useState<SettingsDto | null>(null);
  const [documentsPath, setDocumentsPath] = useState<string>("");
  const [imagesPath, setImagesPath] = useState<string>("");
  const [videosPath, setVideosPath] = useState<string>("");

  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [generalError, setGeneralError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState<boolean>(true);
  const [isSaving, setIsSaving] = useState<boolean>(false);
  const [isBrowsing, setIsBrowsing] = useState<string | null>(null);

  // Nạp cấu hình cài đặt ban đầu từ Host
  const loadSettings = useCallback(async () => {
    setIsLoading(true);
    setGeneralError(null);
    try {
      const data = await storageSettingsApi.getSettings();
      setSavedSettings(data);
      setDocumentsPath(data.documentsPath);
      setImagesPath(data.imagesPath);
      setVideosPath(data.videosPath);
      setFieldErrors({});
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      setGeneralError(`Không thể nạp cấu hình cài đặt: ${message}`);
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadSettings();
  }, [loadSettings]);

  // Kiểm tra xem người dùng có thay đổi đường dẫn nào so với bản đã lưu hay không
  const isDirty =
    savedSettings !== null &&
    (documentsPath !== savedSettings.documentsPath ||
      imagesPath !== savedSettings.imagesPath ||
      videosPath !== savedSettings.videosPath);

  useEffect(() => {
    onDirtyChange?.(isDirty);
  }, [isDirty, onDirtyChange]);

  // Hủy bản nháp và hoàn tác về cấu hình đã lưu gần nhất
  const handleCancel = useCallback(() => {
    if (savedSettings) {
      setDocumentsPath(savedSettings.documentsPath);
      setImagesPath(savedSettings.imagesPath);
      setVideosPath(savedSettings.videosPath);
    }
    setFieldErrors({});
    setGeneralError(null);
    setSuccessMessage(null);
  }, [savedSettings]);

  useEffect(() => {
    if (resetSignal !== undefined && resetSignal > 0) {
      handleCancel();
    }
  }, [resetSignal, handleCancel]);

  // Mở hộp thoại chọn thư mục Windows an toàn
  const handleBrowse = async (target: "documents" | "images" | "videos") => {
    setIsBrowsing(target);
    setGeneralError(null);
    setSuccessMessage(null);
    try {
      const currentPath =
        target === "documents"
          ? documentsPath
          : target === "images"
            ? imagesPath
            : videosPath;

      const selected = await storageSettingsApi.browseFolder(target, currentPath);
      if (selected) {
        if (target === "documents") setDocumentsPath(selected);
        if (target === "images") setImagesPath(selected);
        if (target === "videos") setVideosPath(selected);

        // Xóa thông báo lỗi của trường vừa chọn lại thư mục
        const fieldKey =
          target === "documents"
            ? "documentsPath"
            : target === "images"
              ? "imagesPath"
              : "videosPath";

        setFieldErrors((prev) => {
          const next = { ...prev };
          delete next[fieldKey];
          return next;
        });
      }
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      setGeneralError(`Lỗi khi mở hộp thoại chọn thư mục: ${message}`);
    } finally {
      setIsBrowsing(null);
    }
  };

  // Xác thực nhanh và Lưu cài đặt đồng thời vào Host
  const handleSave = async (e?: React.FormEvent) => {
    if (e) e.preventDefault();
    if (isSaving || isLoading) return;

    setIsSaving(true);
    setGeneralError(null);
    setSuccessMessage(null);
    setFieldErrors({});

    try {
      const request: SettingsSaveRequest = {
        documentsPath: documentsPath.trim(),
        imagesPath: imagesPath.trim(),
        videosPath: videosPath.trim(),
        imageProvider: savedSettings?.imageProvider ?? null,
        scriptProvider: savedSettings?.scriptProvider ?? null,
        videoPromptProvider: savedSettings?.videoPromptProvider ?? null,
      };

      const updated = await storageSettingsApi.saveSettings(request);
      setSavedSettings(updated);
      setDocumentsPath(updated.documentsPath);
      setImagesPath(updated.imagesPath);
      setVideosPath(updated.videosPath);
      setFieldErrors({});
      setSuccessMessage("Đã lưu thành công cấu hình thư mục lưu trữ!");
    } catch (err: any) {
      const fe =
        err?.fieldErrors ||
        err?.details?.fieldErrors ||
        (err?.details &&
        typeof err.details === "object" &&
        ("documentsPath" in err.details ||
          "imagesPath" in err.details ||
          "videosPath" in err.details)
          ? err.details
          : null);

      if (fe) {
        setFieldErrors(fe);
      } else {
        const message = err?.message || (err instanceof Error ? err.message : String(err));
        setGeneralError(message || "Không thể lưu cài đặt. Vui lòng kiểm tra lại quyền ghi hoặc đường dẫn.");
      }
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <div className="space-y-6">
      <div className="space-y-1">
        <h3 className="text-lg font-bold text-foreground">Thư mục lưu trữ độc lập</h3>
        <p className="text-sm text-muted-foreground">
          Cấu hình ba thư mục riêng biệt trên ổ đĩa để lưu trữ tài liệu xuất, ảnh AI và video thành phẩm.
        </p>
      </div>

      {isLoading ? (
        <div className="flex flex-col items-center justify-center p-8 text-muted-foreground gap-2">
          <Loader2 className="h-6 w-6 animate-spin text-primary" />
          <p className="text-sm">Đang nạp thông tin cấu hình lưu trữ...</p>
        </div>
      ) : (
        <form onSubmit={handleSave} noValidate className="space-y-5">
          {/* Thông báo thành công */}
          {successMessage && (
            <Alert variant="success" role="status">
              <Check className="h-4 w-4" />
              <div>
                <strong>Thành công:</strong> {successMessage}
              </div>
            </Alert>
          )}

          {/* Thông báo lỗi chung */}
          {generalError && (
            <Alert variant="destructive" role="alert">
              <AlertCircle className="h-4 w-4" />
              <div>
                <strong>Lỗi cài đặt:</strong> {generalError}
              </div>
            </Alert>
          )}

          {/* 1. Thư mục Tài liệu */}
          <div className="space-y-2">
            <Label htmlFor="docs-path" className="font-semibold text-foreground">
              1. Thư mục Tài liệu (Documents)
            </Label>
            <div className="flex gap-2">
              <input
                id="docs-path"
                type="text"
                value={documentsPath}
                onChange={(e) => {
                  setDocumentsPath(e.target.value);
                  setSuccessMessage(null);
                }}
                placeholder="Ví dụ: C:\TiToAI\documents"
                className={cn(
                  "flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
                  fieldErrors["documentsPath"] && "border-destructive focus-visible:ring-destructive"
                )}
                disabled={isSaving}
              />
              <Button
                type="button"
                variant="secondary"
                onClick={() => handleBrowse("documents")}
                disabled={isSaving || isBrowsing === "documents"}
                aria-label="Chọn thư mục lưu trữ tài liệu"
                className="shrink-0"
              >
                <FolderOpen className="h-4 w-4" />
                {isBrowsing === "documents" ? "Đang chọn…" : "Chọn thư mục…"}
              </Button>
            </div>
            <p className="text-xs text-muted-foreground">
              Lưu trữ kịch bản, prompt và tài liệu được xuất thành tệp văn bản.
            </p>
            {fieldErrors["documentsPath"] && (
              <p className="text-xs text-destructive font-medium" role="alert">
                {fieldErrors["documentsPath"].join(" ")}
              </p>
            )}
          </div>

          {/* 2. Thư mục Ảnh AI */}
          <div className="space-y-2">
            <Label htmlFor="imgs-path" className="font-semibold text-foreground">
              2. Thư mục Ảnh AI (Images)
            </Label>
            <div className="flex gap-2">
              <input
                id="imgs-path"
                type="text"
                value={imagesPath}
                onChange={(e) => {
                  setImagesPath(e.target.value);
                  setSuccessMessage(null);
                }}
                placeholder="Ví dụ: C:\TiToAI\images"
                className={cn(
                  "flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
                  fieldErrors["imagesPath"] && "border-destructive focus-visible:ring-destructive"
                )}
                disabled={isSaving}
              />
              <Button
                type="button"
                variant="secondary"
                onClick={() => handleBrowse("images")}
                disabled={isSaving || isBrowsing === "images"}
                aria-label="Chọn thư mục lưu trữ ảnh AI"
                className="shrink-0"
              >
                <FolderOpen className="h-4 w-4" />
                {isBrowsing === "images" ? "Đang chọn…" : "Chọn thư mục…"}
              </Button>
            </div>
            <p className="text-xs text-muted-foreground">
              Lưu ảnh do AI tạo ra và ảnh tham chiếu được nhập vào thư viện.
            </p>
            {fieldErrors["imagesPath"] && (
              <p className="text-xs text-destructive font-medium" role="alert">
                {fieldErrors["imagesPath"].join(" ")}
              </p>
            )}
          </div>

          {/* 3. Thư mục Video AI */}
          <div className="space-y-2">
            <Label htmlFor="vids-path" className="font-semibold text-foreground">
              3. Thư mục Video AI (Videos)
            </Label>
            <div className="flex gap-2">
              <input
                id="vids-path"
                type="text"
                value={videosPath}
                onChange={(e) => {
                  setVideosPath(e.target.value);
                  setSuccessMessage(null);
                }}
                placeholder="Ví dụ: C:\TiToAI\videos"
                className={cn(
                  "flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-xs transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50",
                  fieldErrors["videosPath"] && "border-destructive focus-visible:ring-destructive"
                )}
                disabled={isSaving}
              />
              <Button
                type="button"
                variant="secondary"
                onClick={() => handleBrowse("videos")}
                disabled={isSaving || isBrowsing === "videos"}
                aria-label="Chọn thư mục lưu trữ video AI"
                className="shrink-0"
              >
                <FolderOpen className="h-4 w-4" />
                {isBrowsing === "videos" ? "Đang chọn…" : "Chọn thư mục…"}
              </Button>
            </div>
            <p className="text-xs text-muted-foreground">
              Lưu video thành phẩm được tạo hoặc tải về từ các dịch vụ sinh video.
            </p>
            {fieldErrors["videosPath"] && (
              <p className="text-xs text-destructive font-medium" role="alert">
                {fieldErrors["videosPath"].join(" ")}
              </p>
            )}
          </div>

          {/* Banner lưu ý đổi thư mục theo SETTINGS_PLAN §3 */}
          <Alert variant="info" className="mt-4">
            <Info className="h-4 w-4" />
            <div>
              <strong>Quy tắc lưu trữ:</strong> Đổi thư mục chỉ áp dụng cho tài liệu và tác vụ mới; các tệp đã tạo trước đó được giữ nguyên tại vị trí cũ.
            </div>
          </Alert>

          {/* Thanh hành động Lưu & Hủy */}
          <div className="flex items-center justify-end gap-3 pt-5 border-t border-border mt-6">
            <Button
              type="button"
              variant="outline"
              onClick={handleCancel}
              disabled={isSaving || !isDirty}
            >
              Hủy thay đổi
            </Button>
            <Button
              type="submit"
              variant="default"
              disabled={isSaving || !isDirty}
            >
              {isSaving ? "Đang lưu cấu hình…" : "Lưu cài đặt"}
            </Button>
          </div>
        </form>
      )}
    </div>
  );
}
