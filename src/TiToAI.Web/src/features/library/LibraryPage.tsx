import { useState, useEffect, useCallback } from "react";
import { Button } from "@/components/ui/button";
import {
  IconSearch,
  IconRefresh,
  IconFolder,
  IconEye,
  IconImage,
  IconMusic,
  IconFilm,
  IconHardDrive,
  IconChevronLeft,
  IconChevronRight,
  IconUpload,
} from "../../components/Icons";
import {
  listAssets,
  openAssetFolder,
  uploadAsset,
  type AssetItem,
} from "./libraryApi";
import { AssetPreviewDialog } from "./AssetPreviewDialog";
import { AssetMediaPreview } from "./AssetMediaPreview";

type FilterType = "all" | "Image" | "Audio" | "Video";

export function LibraryPage() {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [assets, setAssets] = useState<AssetItem[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [page, setPage] = useState(1);
  const pageSize = 12;

  const [activeFilter, setActiveFilter] = useState<FilterType>("all");
  const [search, setSearch] = useState("");
  const [debouncedSearch, setDebouncedSearch] = useState("");

  const [previewAssetId, setPreviewAssetId] = useState<string | null>(null);
  const [actionMessage, setActionMessage] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);

  const handleUpload = async () => {
    setUploading(true);
    setActionMessage(null);
    try {
      const typeToUpload = activeFilter === "Audio" ? "Audio" : "Image";
      const res = await uploadAsset({
        assetType: typeToUpload,
      });
      if (res.asset) {
        setActionMessage(`Đã tải lên và lưu vào Thư viện: ${res.asset.relativePath.split("/").pop()}`);
        loadAssets();
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Tải lên tệp thất bại.";
      setActionMessage(msg);
    } finally {
      setUploading(false);
    }
  };

  // Debounce search input
  useEffect(() => {
    const timer = setTimeout(() => {
      setDebouncedSearch(search);
      setPage(1); // Reset về trang 1 khi search
    }, 300);
    return () => clearTimeout(timer);
  }, [search]);

  const loadAssets = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await listAssets({
        assetType: activeFilter === "all" ? undefined : activeFilter,
        page,
        pageSize,
        search: debouncedSearch.trim() || undefined,
      });
      setAssets(res?.assets ?? []);
      setTotalCount(res?.totalCount ?? 0);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Lỗi khi tải danh sách tài nguyên.";
      setError(msg);
    } finally {
      setLoading(false);
    }
  }, [activeFilter, page, pageSize, debouncedSearch]);

  useEffect(() => {
    loadAssets();
  }, [loadAssets]);

  const handleOpenFolder = async (asset: AssetItem) => {
    try {
      setActionMessage(null);
      const res = await openAssetFolder({
        assetId: asset.assetId,
      });
      if (res.success) {
        setActionMessage(`Đã mở thư mục cho tệp: ${asset.relativePath.split("/").pop()}`);
      } else {
        setActionMessage("Không thể mở thư mục hoặc đường dẫn chưa khả dụng.");
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Lỗi khi mở thư mục";
      setActionMessage(msg);
    }
  };

  const formatBytes = (bytes: number) => {
    if (bytes === 0) return "0 B";
    const k = 1024;
    const sizes = ["B", "KB", "MB", "GB"];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(1)) + " " + sizes[i];
  };

  const formatDate = (isoString: string) => {
    try {
      const d = new Date(isoString);
      return d.toLocaleDateString("vi-VN", {
        day: "2-digit",
        month: "2-digit",
        year: "numeric",
        hour: "2-digit",
        minute: "2-digit",
      });
    } catch {
      return isoString;
    }
  };

  const renderIcon = (type: string) => {
    switch (type.toLowerCase()) {
      case "image":
        return <IconImage size={24} className="text-info" />;
      case "audio":
        return <IconMusic size={24} className="text-success" />;
      case "video":
        return <IconFilm size={24} className="text-purple-500" />;
      default:
        return <IconHardDrive size={24} className="text-neutral-500" />;
    }
  };

  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

  return (
    <div className="library-page space-y-4">
      {/* Action banner / alert */}
      {actionMessage && (
        <div className="p-3 text-xs bg-muted border rounded flex items-center justify-between">
          <span>{actionMessage}</span>
          <button
            type="button"
            onClick={() => setActionMessage(null)}
            className="text-xs text-muted-foreground hover:text-foreground font-medium underline"
          >
            Đóng
          </button>
        </div>
      )}

      {/* Toolbar: Bộ lọc loại + Ô tìm kiếm + Nút refresh */}
      <div className="flex flex-col sm:flex-row items-stretch sm:items-center justify-between gap-3 p-3 bg-card border rounded-lg shadow-sm">
        <div className="flex items-center gap-1.5 overflow-x-auto pb-1 sm:pb-0">
          <Button
            type="button"
            variant={activeFilter === "all" ? "default" : "secondary"}
            size="sm"
            onClick={() => {
              setActiveFilter("all");
              setPage(1);
            }}
            className="text-xs px-3 py-1.5 h-auto rounded-md font-medium"
          >
            Tất cả ({activeFilter === "all" ? totalCount : "•"})
          </Button>
          <Button
            type="button"
            variant={activeFilter === "Image" ? "default" : "secondary"}
            size="sm"
            onClick={() => {
              setActiveFilter("Image");
              setPage(1);
            }}
            className="text-xs px-3 py-1.5 h-auto rounded-md font-medium flex items-center gap-1"
          >
            <IconImage size={14} />
            <span>Hình ảnh</span>
          </Button>
          <Button
            type="button"
            variant={activeFilter === "Audio" ? "default" : "secondary"}
            size="sm"
            onClick={() => {
              setActiveFilter("Audio");
              setPage(1);
            }}
            className="text-xs px-3 py-1.5 h-auto rounded-md font-medium flex items-center gap-1"
          >
            <IconMusic size={14} />
            <span>Âm thanh</span>
          </Button>
          <Button
            type="button"
            variant={activeFilter === "Video" ? "default" : "secondary"}
            size="sm"
            onClick={() => {
              setActiveFilter("Video");
              setPage(1);
            }}
            className="text-xs px-3 py-1.5 h-auto rounded-md font-medium flex items-center gap-1"
          >
            <IconFilm size={14} />
            <span>Video</span>
          </Button>
        </div>

        <div className="flex items-center gap-2">
          <div className="relative flex-1 sm:w-64">
            <IconSearch size={15} className="absolute left-3 top-2.5 text-muted-foreground" />
            <input
              type="text"
              placeholder="Tìm theo tên hoặc mã tệp..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="w-full pl-9 pr-3 py-1.5 text-xs rounded border bg-background"
            />
          </div>

          <Button
            type="button"
            variant="default"
            size="sm"
            onClick={handleUpload}
            disabled={uploading}
            className="h-8 text-xs px-2.5 gap-1.5 rounded font-medium"
            title="Tải tệp từ máy tính lên Thư viện"
          >
            <IconUpload size={14} />
            <span>{uploading ? "Đang tải…" : "Tải lên"}</span>
          </Button>

          <Button
            type="button"
            variant="secondary"
            size="icon"
            onClick={loadAssets}
            className="h-8 w-8 rounded"
            title="Tải lại danh sách"
          >
            <IconRefresh size={15} />
          </Button>
        </div>
      </div>

      {/* Grid danh sách tệp */}
      {loading && (
        <div className="py-16 text-center text-muted-foreground text-sm animate-pulse">
          Đang quét và tải danh mục tài nguyên...
        </div>
      )}

      {error && (
        <div className="p-4 rounded-lg bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 text-sm text-red-600 dark:text-red-400">
          {error}
        </div>
      )}

      {!loading && !error && assets.length === 0 && (
        <div className="empty-card p-10 text-center border rounded-lg bg-card space-y-2">
          <div className="mx-auto w-12 h-12 flex items-center justify-center rounded-full bg-muted text-muted-foreground">
            <IconHardDrive size={24} />
          </div>
          <h4 className="text-base font-semibold">Chưa có tài nguyên nào</h4>
          <p className="text-xs text-muted-foreground max-w-md mx-auto">
            Các hình ảnh, video và tệp âm thanh được tải lên từ Phân cảnh (Scenes) hoặc được
            sinh từ AI sẽ tự động xuất hiện và được quản lý tập trung tại đây.
          </p>
        </div>
      )}

      {!loading && !error && assets.length > 0 && (
        <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
          {assets.map((asset) => {
            const fileName = asset.relativePath.split("/").pop() || asset.relativePath;
            return (
              <div
                key={asset.assetId}
                className="asset-card group bg-card border rounded-lg p-3 hover:border-primary/60 hover:shadow-md transition flex flex-col justify-between"
              >
                <div>
                  <div className="mb-3"><AssetMediaPreview assetId={asset.assetId} assetType={asset.assetType} fileName={fileName} /></div>
                  <div className="flex items-start justify-between gap-2 mb-2">
                    <div className="p-2 rounded bg-muted/70 group-hover:bg-primary/10 transition">
                      {renderIcon(asset.assetType)}
                    </div>
                    <span className="text-[10px] font-mono uppercase px-2 py-0.5 rounded bg-muted text-muted-foreground">
                      {asset.assetType}
                    </span>
                  </div>

                  <h5
                    className="text-xs font-semibold text-foreground truncate"
                    title={fileName}
                  >
                    {fileName}
                  </h5>

                  <p
                    className="text-[11px] font-mono text-muted-foreground truncate mt-0.5"
                    title={asset.relativePath}
                  >
                    {asset.relativePath}
                  </p>

                  <div className="flex items-center justify-between text-[11px] text-muted-foreground mt-3 pt-2 border-t">
                    <span>{formatBytes(asset.byteSize)}</span>
                    <span>{formatDate(asset.createdAtUtc)}</span>
                  </div>
                </div>

                <div className="flex items-center gap-1.5 mt-3 pt-2 border-t">
                  <Button
                    type="button"
                    variant="secondary"
                    size="sm"
                    onClick={() => setPreviewAssetId(asset.assetId)}
                    className="flex-1 text-xs py-1 h-auto flex items-center justify-center gap-1"
                  >
                    <IconEye size={13} />
                    <span>Chi tiết</span>
                  </Button>
                  <Button
                    type="button"
                    variant="secondary"
                    size="icon"
                    onClick={() => handleOpenFolder(asset)}
                    className="h-7 w-7 text-xs rounded"
                    title="Mở thư mục"
                  >
                    <IconFolder size={14} />
                  </Button>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {/* Pagination Footer */}
      {!loading && !error && totalCount > 0 && (
        <div className="flex items-center justify-between p-3 bg-card border rounded-lg text-xs text-muted-foreground">
          <div>
            Hiển thị <strong>{assets.length}</strong> / <strong>{totalCount}</strong> tài nguyên
            (Trang {page} / {totalPages})
          </div>
          <div className="flex items-center gap-2">
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={page <= 1}
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              className="px-2.5 py-1 h-auto flex items-center gap-1 disabled:opacity-50"
            >
              <IconChevronLeft size={14} />
              <span>Trước</span>
            </Button>
            <Button
              type="button"
              variant="secondary"
              size="sm"
              disabled={page >= totalPages}
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              className="px-2.5 py-1 h-auto flex items-center gap-1 disabled:opacity-50"
            >
              <span>Sau</span>
              <IconChevronRight size={14} />
            </Button>
          </div>
        </div>
      )}

      {/* Modal chi tiết tài nguyên */}
      {previewAssetId && (
        <AssetPreviewDialog
          assetId={previewAssetId}
          onClose={() => setPreviewAssetId(null)}
        />
      )}
    </div>
  );
}
