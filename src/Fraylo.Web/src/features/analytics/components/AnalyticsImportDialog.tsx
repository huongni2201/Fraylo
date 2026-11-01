import React, { useState, useId } from "react";
import {
  Upload,
  FileText,
  AlertCircle,
  CheckCircle2,
  Loader2,
  FileCode,
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
import { Textarea } from "@/components/ui/textarea";
import { importAnalyticsData } from "../analyticsApi";

interface AnalyticsImportDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  accountId: string;
  onSuccess: () => void;
}

export function AnalyticsImportDialog({
  open,
  onOpenChange,
  accountId,
  onSuccess,
}: AnalyticsImportDialogProps) {
  const fileInputId = useId();
  const [format, setFormat] = useState<"csv" | "json">("csv");
  const [rawContent, setRawContent] = useState("");
  const [fileName, setFileName] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [isDragging, setIsDragging] = useState(false);

  // Quick preview parse
  const getPreviewCount = (): number => {
    if (!rawContent.trim()) return 0;
    try {
      if (format === "json") {
        const parsed = JSON.parse(rawContent);
        if (Array.isArray(parsed)) return parsed.length;
        if (parsed.videos && Array.isArray(parsed.videos)) return parsed.videos.length;
        if (parsed.data && Array.isArray(parsed.data)) return parsed.data.length;
        return 1;
      } else {
        // CSV: count non-empty lines minus header
        const lines = rawContent.split(/\r?\n/).filter((l) => l.trim().length > 0);
        return Math.max(0, lines.length - 1);
      }
    } catch {
      return 0;
    }
  };

  const previewCount = getPreviewCount();

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    readIncomingFile(file);
  };

  const readIncomingFile = (file: File) => {
    setError(null);
    setFileName(file.name);
    const isJson = file.name.toLowerCase().endsWith(".json");
    setFormat(isJson ? "json" : "csv");

    const reader = new FileReader();
    reader.onload = (event) => {
      const text = event.target?.result;
      if (typeof text === "string") {
        setRawContent(text);
      }
    };
    reader.onerror = () => {
      setError("Không thể đọc tệp đã chọn. Vui lòng thử lại.");
    };
    reader.readAsText(file);
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragging(false);
    const file = e.dataTransfer.files?.[0];
    if (file) {
      readIncomingFile(file);
    }
  };

  const handleSubmit = async () => {
    if (!rawContent.trim()) {
      setError("Vui lòng nhập hoặc tải tệp dữ liệu.");
      return;
    }

    setIsSubmitting(true);
    setError(null);
    setSuccessMessage(null);

    try {
      const res = await importAnalyticsData({
        accountId,
        format,
        rawContent,
        source: fileName ? `import:${fileName}` : "manual_paste",
      });

      if (res.success) {
        setSuccessMessage(
          res.message || `Đã nhập thành công ${res.recordCount} bản ghi dữ liệu!`
        );
        setTimeout(() => {
          onSuccess();
          onOpenChange(false);
          // reset form
          setRawContent("");
          setFileName(null);
          setSuccessMessage(null);
        }, 800);
      } else {
        setError(res.message || "Nhập dữ liệu không thành công.");
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Đã có lỗi xảy ra khi nhập dữ liệu.";
      setError(msg);
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleClose = () => {
    if (isSubmitting) return;
    setError(null);
    setSuccessMessage(null);
    onOpenChange(false);
  };

  return (
    <Dialog open={open} onOpenChange={handleClose}>
      <DialogContent className="max-w-lg" data-testid="import-dialog-content">
        <DialogHeader>
          <DialogTitle className="text-base font-semibold flex items-center gap-2">
            <Upload className="w-5 h-5 text-primary" />
            Nhập dữ liệu TikTok Analytics & Affiliate
          </DialogTitle>
          <DialogDescription className="text-xs text-muted-foreground">
            Hỗ trợ báo cáo xuất từ TikTok Studio, TikTok Analytics hoặc TikTok Shop dưới định dạng CSV/JSON.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4 py-2">
          {/* Format Selection */}
          <div className="flex items-center justify-between text-xs">
            <span className="font-medium text-foreground">Định dạng tệp:</span>
            <div className="flex items-center gap-1 bg-muted/60 p-1 rounded-md">
              <button
                type="button"
                onClick={() => setFormat("csv")}
                className={`px-2.5 py-1 rounded text-xs font-medium transition-[color,background-color,border-color,box-shadow,opacity,transform] ${
                  format === "csv"
                    ? "bg-background text-foreground shadow-xs font-semibold"
                    : "text-muted-foreground hover:text-foreground"
                }`}
                data-testid="format-csv-btn"
              >
                CSV
              </button>
              <button
                type="button"
                onClick={() => setFormat("json")}
                className={`px-2.5 py-1 rounded text-xs font-medium transition-[color,background-color,border-color,box-shadow,opacity,transform] ${
                  format === "json"
                    ? "bg-background text-foreground shadow-xs font-semibold"
                    : "text-muted-foreground hover:text-foreground"
                }`}
                data-testid="format-json-btn"
              >
                JSON
              </button>
            </div>
          </div>

          {/* Drag & Drop Upload Box */}
          <div
            onDragOver={(e) => {
              e.preventDefault();
              setIsDragging(true);
            }}
            onDragLeave={() => setIsDragging(false)}
            onDrop={handleDrop}
            className={`border-2 border-dashed rounded-lg p-4 text-center transition-colors cursor-pointer ${
              isDragging
                ? "border-primary bg-primary/5"
                : "border-border hover:border-border/80 bg-muted/20"
            }`}
            onClick={() => document.getElementById(fileInputId)?.click()}
          >
            <input
              id={fileInputId}
              type="file"
              accept=".csv,.json,text/csv,application/json"
              className="hidden"
              onChange={handleFileChange}
              data-testid="import-file-input"
            />
            <div className="flex flex-col items-center gap-1.5">
              {fileName ? (
                <>
                  <FileCode className="w-7 h-7 text-primary" />
                  <p className="text-xs font-medium text-foreground">{fileName}</p>
                  <p className="text-[11px] text-muted-foreground">Bấm để đổi tệp khác</p>
                </>
              ) : (
                <>
                  <Upload className="w-7 h-7 text-muted-foreground" />
                  <p className="text-xs font-medium text-foreground">
                    Kéo thả tệp CSV / JSON vào đây hoặc <span className="text-primary underline">chọn tệp</span>
                  </p>
                  <p className="text-[11px] text-muted-foreground">Tối đa 4MB dữ liệu</p>
                </>
              )}
            </div>
          </div>

          {/* Raw Text Content Input */}
          <div className="space-y-1.5">
            <div className="flex items-center justify-between text-xs">
              <label htmlFor="raw-textarea" className="font-medium text-muted-foreground">
                Hoặc dán nội dung văn bản:
              </label>
              {previewCount > 0 && (
                <span className="text-[11px] font-mono text-primary font-medium" data-testid="import-preview-badge">
                  Nhận diện: ~{previewCount} dòng/bản ghi
                </span>
              )}
            </div>
            <Textarea
              id="raw-textarea"
              rows={5}
              placeholder={
                format === "csv"
                  ? "video_key,title,views,likes,comments,shares,gmv\nv_001,Video mẫu 1,12000,850,45,20,500000"
                  : '[\n  {\n    "videoKey": "v_001",\n    "title": "Video mẫu",\n    "views": 12000\n  }\n]'
              }
              value={rawContent}
              onChange={(e) => setRawContent(e.target.value)}
              className="font-mono text-xs resize-none"
              data-testid="import-textarea"
            />
          </div>

          {/* Error & Success Feedback */}
          {error && (
            <div className="flex items-center gap-2 p-2.5 rounded-md bg-destructive/10 border border-destructive/20 text-xs text-destructive" data-testid="import-error">
              <AlertCircle className="w-4 h-4 shrink-0" />
              <span>{error}</span>
            </div>
          )}

          {successMessage && (
            <div className="flex items-center gap-2 p-2.5 rounded-md bg-success/10 border border-success/20 text-xs text-success dark:text-success" data-testid="import-success">
              <CheckCircle2 className="w-4 h-4 shrink-0" />
              <span>{successMessage}</span>
            </div>
          )}
        </div>

        <DialogFooter className="gap-2 sm:gap-0">
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={handleClose}
            disabled={isSubmitting}
            className="text-xs"
          >
            Hủy
          </Button>
          <Button
            type="button"
            size="sm"
            onClick={handleSubmit}
            disabled={isSubmitting || !rawContent.trim()}
            className="text-xs gap-1.5"
            data-testid="import-submit-btn"
          >
            {isSubmitting ? (
              <>
                <Loader2 className="w-3.5 h-3.5 animate-spin" />
                <span>Đang lưu...</span>
              </>
            ) : (
              <>
                <FileText className="w-3.5 h-3.5" />
                <span>Lưu dữ liệu</span>
              </>
            )}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
