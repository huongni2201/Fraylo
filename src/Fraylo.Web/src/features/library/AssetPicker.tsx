import { useState, useEffect, useCallback, useRef } from "react";
import { Button } from "@/components/ui/button";
import { Film } from "lucide-react";
import {
  IconClose,
  IconSearch,
  IconCheck,
  IconImage,
  IconMusic,
  IconRefresh,
  IconUpload,
} from "../../components/Icons";
import { listAssets, uploadAsset, type AssetItem } from "./libraryApi";

interface AssetPickerProps {
  includeShared?: boolean;
  projectId?: string;
  assetType: "Image" | "Audio" | "Video";
  onSelect: (asset: AssetItem) => void;
  onClose: () => void;
}

export function AssetPicker({
  includeShared = false,
  projectId,
  assetType,
  onSelect,
  onClose,
}: AssetPickerProps) {
  const assetLabel = assetType === "Image" ? "hình ảnh" : assetType === "Video" ? "video" : "âm thanh";
  const [loading, setLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [assets, setAssets] = useState<AssetItem[]>([]);
  const [search, setSearch] = useState("");
  const [selectedAsset, setSelectedAsset] = useState<AssetItem | null>(null);
  const [isDragging, setIsDragging] = useState(false);

  const fileInputRef = useRef<HTMLInputElement>(null);

  const fetchAssets = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await listAssets({
        includeShared,
        projectId,
        assetType,
        search: search.trim() || undefined,
        page: 1,
        pageSize: 50,
      });
      setAssets(res.assets);
      if (res.assets.length > 0 && !selectedAsset) {
        setSelectedAsset(res.assets[0]);
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Không thể tải danh sách tài nguyên.";
      setError(msg);
    } finally {
      setLoading(false);
    }
  }, [projectId, assetType, search, includeShared]);

  useEffect(() => {
    fetchAssets();
  }, [fetchAssets]);

  const handleConfirm = () => {
    if (selectedAsset) {
      onSelect(selectedAsset);
      onClose();
    }
  };

  const handleUploadFile = async (file: File) => {
    setUploading(true);
    setError(null);
    try {
      const reader = new FileReader();
      const base64Data = await new Promise<string>((resolve, reject) => {
        reader.onload = () => resolve(reader.result as string);
        reader.onerror = reject;
        reader.readAsDataURL(file);
      });

      const res = await uploadAsset({
        projectId,
        assetType,
        fileName: file.name,
        base64Data,
      });

      if (res.asset) {
        await fetchAssets();
        setSelectedAsset(res.asset);
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Tải lên tệp thất bại.";
      setError(msg);
    } finally {
      setUploading(false);
      if (fileInputRef.current) {
        fileInputRef.current.value = "";
      }
    }
  };

  const handleTriggerUpload = async () => {

    // Nếu môi trường desktop WebView2 có window.chrome?.webview, gọi asset.upload mở native dialog
    if (assetType === "Video" || (typeof window !== "undefined" && window.chrome?.webview)) {
      setUploading(true);
      setError(null);
      try {
        const res = await uploadAsset({
          projectId,
          assetType,
        });
        if (res.asset) {
          await fetchAssets();
          setSelectedAsset(res.asset);
        }
      } catch (err: unknown) {
        const msg = err instanceof Error ? err.message : "Tải lên tài nguyên thất bại.";
        setError(msg);
      } finally {
        setUploading(false);
      }
    } else {
      // Fallback web / mock test: kích hoạt file input ẩn
      fileInputRef.current?.click();
    }
  };

  const formatBytes = (bytes: number) => {
    if (bytes === 0) return "0 B";
    const k = 1024;
    const sizes = ["B", "KB", "MB"];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + " " + sizes[i];
  };

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="asset-picker-title"
      className="asset-picker-dialog fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
    >
      <div className="bg-card text-card-foreground border rounded-lg shadow-xl max-w-2xl w-full flex flex-col max-h-[85vh]">
        {/* Input file ẩn phục vụ upload */}
        <input
          ref={fileInputRef}
          type="file"
          accept={assetType === "Image" ? "image/png,image/jpeg,image/webp" : assetType === "Video" ? "video/mp4" : "audio/mp3,audio/wav"}
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) {
              handleUploadFile(file);
            }
          }}
          className="hidden"
          data-testid="asset-picker-file-input"
        />

        {/* Header */}
        <div className="flex items-center justify-between border-b p-4">
          <div className="flex items-center gap-2">
            {assetType === "Image" ? (
              <IconImage size={20} className="text-info" />
            ) : (
              assetType === "Video" ? <Film size={20} className="text-info" /> : <IconMusic size={20} className="text-success" />
            )}
            <h3 id="asset-picker-title" className="text-base font-semibold">
              Chọn {assetLabel} từ Thư viện
            </h3>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="p-1 rounded hover:bg-neutral-200 dark:hover:bg-neutral-800 transition"
            aria-label="Đóng"
          >
            <IconClose size={18} />
          </button>
        </div>

        {/* Filters & Actions */}
        <div className="p-4 border-b flex flex-wrap items-center gap-3 bg-muted/30">
          <div className="relative flex-1 min-w-[200px]">
            <IconSearch size={16} className="absolute left-3 top-2.5 text-muted-foreground" />
            <input
              type="text"
              placeholder="Tìm theo tên file hoặc mã tài nguyên..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="w-full pl-9 pr-3 py-1.5 text-sm rounded border bg-background"
            />
          </div>

          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={handleTriggerUpload}
            disabled={uploading}
            className="text-xs px-2.5 py-1.5 flex items-center gap-1.5 font-medium"
            title={`Tải ${assetLabel} từ máy`}
          >
            <IconUpload size={14} />
            <span>{uploading ? "Đang tải lên…" : "Tải lên"}</span>
          </Button>

          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={fetchAssets}
            className="text-xs px-2.5 py-1.5 flex items-center gap-1"
            title="Làm mới"
          >
            <IconRefresh size={14} />
            <span>Làm mới</span>
          </Button>
        </div>

        {/* Content list & Drop area */}
        <div
          className={`flex-1 overflow-y-auto p-4 min-h-[250px] transition ${
            isDragging ? "bg-primary/5 ring-2 ring-primary ring-inset rounded-b" : ""
          }`}
          onDragOver={(e) => {
            e.preventDefault();
            setIsDragging(true);
          }}
          onDragLeave={() => setIsDragging(false)}
          onDrop={(e) => {
            e.preventDefault();
            setIsDragging(false);
            const file = e.dataTransfer.files?.[0];
            if (file) {
              handleUploadFile(file);
            }
          }}
        >
          {loading && (
            <div className="py-12 text-center text-muted-foreground animate-pulse text-sm">
              Đang tải danh sách tài nguyên...
            </div>
          )}

          {error && (
            <div className="p-3 text-sm text-red-600 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 rounded mb-3">
              {error}
            </div>
          )}

          {!loading && !error && assets.length === 0 && (
            <div className="py-10 flex flex-col items-center justify-center text-center p-6 border-2 border-dashed rounded-lg border-border bg-muted/20">
              <div className="p-3 rounded-full bg-muted mb-3">
                {assetType === "Image" ? (
                  <IconImage size={28} className="text-muted-foreground" />
                ) : (
                  <IconMusic size={28} className="text-muted-foreground" />
                )}
              </div>
              <p className="text-sm font-medium text-foreground mb-1">
                {search ? "Không tìm thấy tài nguyên nào phù hợp." : `Không tìm thấy tài nguyên ${assetLabel} nào phù hợp.`}
              </p>
              <p className="text-xs text-muted-foreground mb-4 max-w-sm">
                Bạn có thể tải lên tệp mới từ máy tính. Tệp sẽ tự động lưu vào Thư viện media để tái sử dụng bất cứ lúc nào.
              </p>
              <Button
                type="button"
                variant="default"
                size="sm"
                disabled={uploading}
                onClick={handleTriggerUpload}
                className="text-xs gap-1.5 px-4"
              >
                <IconUpload size={14} />
                <span>{uploading ? "Đang tải lên…" : `Tải lên ${assetLabel}`}</span>
              </Button>
            </div>
          )}

          {!loading && !error && assets.length > 0 && (
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-2.5">
              {assets.map((asset) => {
                const isSelected = selectedAsset?.assetId === asset.assetId;
                const fileName = asset.relativePath.split("/").pop() || asset.relativePath;
                return (
                  <div
                    key={asset.assetId}
                    role="button"
                    tabIndex={0}
                    onClick={() => setSelectedAsset(asset)}
                    onKeyDown={(e) => {
                      if (e.key === "Enter" || e.key === " ") {
                        e.preventDefault();
                        setSelectedAsset(asset);
                      }
                    }}
                    className={`p-3 rounded border text-left cursor-pointer transition flex items-center gap-3 ${
                      isSelected
                        ? "border-primary bg-primary/10 shadow-sm"
                        : "hover:border-neutral-400 bg-card"
                    }`}
                  >
                    <div className="p-2 rounded bg-muted shrink-0">
                      {asset.assetType === "Image" ? (
                        <IconImage size={20} className="text-info" />
                      ) : (
                        <IconMusic size={20} className="text-success" />
                      )}
                    </div>
                    <div className="min-w-0 flex-1">
                      <div className="text-xs font-medium truncate" title={fileName}>
                        {fileName}
                      </div>
                      <div className="text-[11px] text-muted-foreground flex items-center gap-2 mt-0.5">
                        <span>{formatBytes(asset.byteSize)}</span>
                        <span>•</span>
                        <span className="font-mono">{asset.assetId.substring(0, 8)}...</span>
                      </div>
                    </div>
                    {isSelected && (
                      <IconCheck size={16} className="text-primary shrink-0" />
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between border-t p-4 bg-muted/20">
          <div className="text-xs text-muted-foreground">
            {selectedAsset ? (
              <span>
                Đã chọn: <strong className="text-foreground">{selectedAsset.relativePath.split("/").pop()}</strong>
              </span>
            ) : (
              <span>Chưa chọn tài nguyên</span>
            )}
          </div>
          <div className="flex items-center gap-2">
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={onClose}
              className="text-sm px-3 py-1.5"
            >
              Hủy
            </Button>
            <Button
              type="button"
              variant="default"
              size="sm"
              onClick={handleConfirm}
              disabled={!selectedAsset}
              className="text-sm px-4 py-1.5"
            >
              Xác nhận chọn
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}
