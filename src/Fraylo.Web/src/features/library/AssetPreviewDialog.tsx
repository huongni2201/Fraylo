import { useState, useEffect } from "react";
import { Button } from "@/components/ui/button";
import {
  IconClose,
  IconFolder,
  IconInfo,
  IconWarning,
  IconCheck,
  IconImage,
  IconMusic,
  IconFilm,
} from "../../components/Icons";
import { getAssetPreview, openAssetFolder, type GetAssetPreviewResponse } from "./libraryApi";
import { AssetMediaPreview } from "./AssetMediaPreview";

interface AssetPreviewDialogProps {
  assetId: string;
  onClose: () => void;
}

export function AssetPreviewDialog({ assetId, onClose }: AssetPreviewDialogProps) {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<GetAssetPreviewResponse | null>(null);
  const [openFolderMsg, setOpenFolderMsg] = useState<string | null>(null);

  useEffect(() => {
    let isMounted = true;
    setLoading(true);
    setError(null);

    getAssetPreview({ assetId })
      .then((data) => {
        if (isMounted) {
          setPreview(data);
          setLoading(false);
        }
      })
      .catch((err) => {
        if (isMounted) {
          setError(err.message || "Không thể tải chi tiết tài nguyên.");
          setLoading(false);
        }
      });

    return () => {
      isMounted = false;
    };
  }, [assetId]);

  const handleOpenFolder = async () => {
    try {
      setOpenFolderMsg(null);
      const res = await openAssetFolder({ assetId });
      if (res.success) {
        setOpenFolderMsg("Đã mở thư mục trên máy.");
      } else {
        setOpenFolderMsg("Không thể mở thư mục hoặc đường dẫn không khả dụng.");
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Lỗi khi mở thư mục";
      setOpenFolderMsg(msg);
    }
  };

  const formatBytes = (bytes: number) => {
    if (bytes === 0) return "0 Bytes";
    const k = 1024;
    const sizes = ["Bytes", "KB", "MB", "GB"];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + " " + sizes[i];
  };

  const renderIcon = (type: string) => {
    switch (type.toLowerCase()) {
      case "image":
        return <IconImage size={24} className="text-info" />;
      case "audio":
        return <IconMusic size={24} className="text-success" />;
      case "video":
        return <IconFilm size={24} className="text-primary-text" />;
      default:
        return <IconInfo size={24} className="text-neutral-500" />;
    }
  };

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="asset-preview-title"
      className="asset-preview-dialog fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
    >
      <div className="bg-card text-card-foreground border rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto p-6 space-y-4">
        <div className="flex items-center justify-between border-b pb-3">
          <div className="flex items-center gap-2">
            {preview && renderIcon(preview.assetType)}
            <h3 id="asset-preview-title" className="text-lg font-semibold">
              Chi tiết tài nguyên
            </h3>
          </div>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            onClick={onClose}
            className="h-8 w-8 p-1"
            aria-label="Đóng"
          >
            <IconClose size={18} />
          </Button>
        </div>

        {loading && (
          <div className="py-8 text-center text-muted-foreground animate-pulse">
            Đang kiểm tra và tải thông tin tệp...
          </div>
        )}

        {error && (
          <div className="p-3 text-sm text-red-600 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 rounded">
            {error}
          </div>
        )}

        {preview && (
          <div className="space-y-3 text-sm">
            {preview.fileExists && !preview.isRootOffline && <AssetMediaPreview assetId={preview.assetId} assetType={preview.assetType} fileName={preview.relativePath.split("/").pop() || preview.relativePath} />}
            <div className="grid grid-cols-3 gap-2 py-1 border-b">
              <span className="text-muted-foreground font-medium">Mã tài nguyên:</span>
              <span className="col-span-2 font-mono text-xs break-all">{preview.assetId}</span>
            </div>

            <div className="grid grid-cols-3 gap-2 py-1 border-b">
              <span className="text-muted-foreground font-medium">Loại tệp:</span>
              <span className="col-span-2 capitalize font-medium">{preview.assetType}</span>
            </div>

            <div className="grid grid-cols-3 gap-2 py-1 border-b">
              <span className="text-muted-foreground font-medium">Đường dẫn:</span>
              <span className="col-span-2 font-mono text-xs break-all">{preview.relativePath}</span>
            </div>

            <div className="grid grid-cols-3 gap-2 py-1 border-b">
              <span className="text-muted-foreground font-medium">Định dạng (MIME):</span>
              <span className="col-span-2 font-mono text-xs">{preview.mimeType || "Chưa xác định"}</span>
            </div>

            <div className="grid grid-cols-3 gap-2 py-1 border-b">
              <span className="text-muted-foreground font-medium">Kích thước:</span>
              <span className="col-span-2">{formatBytes(preview.byteSize)}</span>
            </div>

            <div className="grid grid-cols-3 gap-2 py-1 border-b">
              <span className="text-muted-foreground font-medium">Mã SHA-256:</span>
              <span className="col-span-2 font-mono text-xs break-all">{preview.sha256Hash}</span>
            </div>

            {/* Trạng thái kiểm tra tệp vật lý */}
            <div className="pt-2">
              {preview.isRootOffline ? (
                <div className="flex items-center gap-2 p-2.5 rounded bg-warning/10 dark:bg-warning/10/30 border border-warning/40 dark:border-warning text-warning dark:text-warning">
                  <IconWarning size={18} className="shrink-0" />
                  <span className="text-xs">
                    Thư mục lưu trữ ngoại tuyến hoặc không truy cập được (Root Offline).
                  </span>
                </div>
              ) : !preview.fileExists ? (
                <div className="flex items-center gap-2 p-2.5 rounded bg-red-50 dark:bg-red-950/30 border border-red-300 dark:border-red-700 text-red-800 dark:text-red-300">
                  <IconWarning size={18} className="shrink-0" />
                  <span className="text-xs">
                    Tệp vật lý bị mất hoặc không tìm thấy tại đường dẫn lưu trữ.
                  </span>
                </div>
              ) : (
                <div className="flex items-center gap-2 p-2.5 rounded bg-success/10 dark:bg-success/10/30 border border-success/40 dark:border-success text-success dark:text-success">
                  <IconCheck size={18} className="shrink-0" />
                  <span className="text-xs">Tệp vật lý sẵn sàng.</span>
                </div>
              )}
            </div>

            {openFolderMsg && (
              <div className="text-xs text-muted-foreground italic">{openFolderMsg}</div>
            )}
          </div>
        )}

        <div className="flex items-center justify-between pt-3 border-t">
          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={handleOpenFolder}
            className="text-sm flex items-center gap-1.5"
            disabled={loading || !preview}
          >
            <IconFolder size={16} />
            <span>Mở thư mục chứa</span>
          </Button>
          <Button
            type="button"
            variant="default"
            size="sm"
            onClick={onClose}
            className="text-sm px-4"
          >
            Đóng
          </Button>
        </div>
      </div>
    </div>
  );
}
