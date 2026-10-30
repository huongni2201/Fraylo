import { useState } from "react";
import { projectApi, type SceneDto } from "./projectApi";
import { bridgeClient } from "../../services/bridgeClient";
import type { ErrorDto } from "../../contracts/bridge";
import {
  Table,
  TableHeader,
  TableBody,
  TableHead,
  TableRow,
  TableCell,
} from "@/components/ui/table";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogCancel,
} from "@/components/ui/alert-dialog";
import {
  ChevronUp,
  ChevronDown,
  Check,
  Loader2,
  Pencil,
  Trash2,
  Image as ImageIcon,
  Music,
  AlertTriangle,
  Eye,
  X,
  Library,
  Upload,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { AssetPicker, AssetPreviewDialog, type AssetItem } from "../library";

export interface SceneTableProps {
  projectId: string;
  scenes: SceneDto[];
  isLoading: boolean;
  onRefresh: () => void;
  onEditScene: (scene: SceneDto) => void;
}

// Định dạng giây thành mm:ss (ví dụ: 15 -> 00:15)
function formatDuration(seconds: number): string {
  const mins = Math.floor(seconds / 60);
  const secs = seconds % 60;
  return `${mins.toString().padStart(2, "0")}:${secs.toString().padStart(2, "0")}`;
}

export function SceneTable({
  projectId,
  scenes,
  isLoading,
  onRefresh,
  onEditScene,
}: SceneTableProps) {
  const [reorderingSceneId, setReorderingSceneId] = useState<string | null>(null);
  const [deletingScene, setDeletingScene] = useState<SceneDto | null>(null);
  const [isDeleting, setIsDeleting] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [activeAssetOperation, setActiveAssetOperation] = useState<{
    sceneId: string;
    type: "audio" | "image";
  } | null>(null);

  const [pickerState, setPickerState] = useState<{
    scene: SceneDto;
    type: "Image" | "Audio";
  } | null>(null);

  const [previewAssetId, setPreviewAssetId] = useState<string | null>(null);

  // Thao tác gỡ tài nguyên khỏi Scene (Detach không xóa file gốc)
  const handleDetachAsset = async (scene: SceneDto, type: "image" | "audio") => {
    setActiveAssetOperation({ sceneId: scene.sceneId, type });
    setErrorMessage(null);
    try {
      await projectApi.updateScene({
        projectId,
        sceneId: scene.sceneId,
        expectedRevision: scene.revision,
        [type === "image" ? "imageAssetId" : "audioAssetId"]: "",
      });
      onRefresh();
    } catch (err: unknown) {
      const errorDto = err as ErrorDto;
      console.error("[SceneTable] Lỗi gỡ tài nguyên:", errorDto);
      setErrorMessage(errorDto?.message || "Không thể gỡ tài nguyên khỏi phân cảnh.");
    } finally {
      setActiveAssetOperation(null);
    }
  };

  // Callback khi chọn tài nguyên từ Thư viện qua AssetPicker
  const handleSelectFromLibrary = async (asset: AssetItem) => {
    if (!pickerState) return;
    const { scene, type } = pickerState;
    setActiveAssetOperation({ sceneId: scene.sceneId, type: type === "Image" ? "image" : "audio" });
    setErrorMessage(null);
    try {
      await projectApi.updateScene({
        projectId,
        sceneId: scene.sceneId,
        expectedRevision: scene.revision,
        [type === "Image" ? "imageAssetId" : "audioAssetId"]: asset.assetId,
      });
      setPickerState(null);
      onRefresh();
    } catch (err: unknown) {
      const errorDto = err as ErrorDto;
      console.error("[SceneTable] Lỗi gán tài nguyên từ Thư viện:", errorDto);
      setErrorMessage(errorDto?.message || "Không thể gán tài nguyên từ Thư viện.");
    } finally {
      setActiveAssetOperation(null);
    }
  };

  // Thao tác nhập âm thanh gốc (MP3/WAV)
  const handleImportAudio = async (sceneId: string) => {
    setActiveAssetOperation({ sceneId, type: "audio" });
    setErrorMessage(null);
    try {
      const response = await bridgeClient.invoke<{ asset?: { id: string } | null; canceled?: boolean }>(
        "asset.import_audio",
        {
          projectId,
          sceneId,
        }
      );
      if (response && !response.canceled) {
        onRefresh();
      }
    } catch (err: unknown) {
      const errorDto = err as ErrorDto;
      console.error("[SceneTable] Lỗi nhập âm thanh:", errorDto);
      setErrorMessage(errorDto?.message || "Không thể nhập tệp âm thanh. Vui lòng kiểm tra lại định dạng MP3/WAV.");
    } finally {
      setActiveAssetOperation(null);
    }
  };

  // Thao tác chọn ảnh tham chiếu (PNG/JPG/WEBP)
  const handleSelectImage = async (sceneId: string) => {
    setActiveAssetOperation({ sceneId, type: "image" });
    setErrorMessage(null);
    try {
      const response = await bridgeClient.invoke<{ asset?: { id: string } | null; canceled?: boolean }>(
        "asset.select_reference_image",
        {
          projectId,
          sceneId,
        }
      );
      if (response && !response.canceled) {
        onRefresh();
      }
    } catch (err: unknown) {
      const errorDto = err as ErrorDto;
      console.error("[SceneTable] Lỗi chọn ảnh tham chiếu:", errorDto);
      setErrorMessage(errorDto?.message || "Không thể chọn tệp hình ảnh tham chiếu.");
    } finally {
      setActiveAssetOperation(null);
    }
  };

  // Thao tác đổi thứ tự (Reorder Lên / Xuống)
  const handleMove = async (index: number, direction: "up" | "down") => {
    const targetIndex = direction === "up" ? index - 1 : index + 1;
    if (targetIndex < 0 || targetIndex >= scenes.length) return;

    const movingScene = scenes[index];
    setReorderingSceneId(movingScene.sceneId);
    setErrorMessage(null);

    // Tạo mảng thứ tự mới
    const newScenes = [...scenes];
    const temp = newScenes[index];
    newScenes[index] = newScenes[targetIndex];
    newScenes[targetIndex] = temp;

    const newSceneIds = newScenes.map((s) => s.sceneId);

    // Chuẩn bị dictionary expectedSceneRevisions
    const expectedSceneRevisions: Record<string, string> = {};
    scenes.forEach((s) => {
      expectedSceneRevisions[s.sceneId] = s.revision;
    });

    try {
      await projectApi.reorderScenes({
        projectId,
        sceneIds: newSceneIds,
        expectedSceneRevisions,
      });
      onRefresh();
    } catch (err: unknown) {
      const errorDto = err as ErrorDto;
      console.error("[SceneTable] Lỗi sắp xếp cảnh:", errorDto);
      if (errorDto?.code === "CONCURRENCY_CONFLICT") {
        setErrorMessage(
          "Không thể thay đổi thứ tự do có phiên khác vừa cập nhật cảnh. Bảng đã được tải lại dữ liệu mới nhất.",
        );
      } else {
        setErrorMessage(errorDto?.message || "Đã xảy ra lỗi khi sắp xếp lại phân cảnh.");
      }
      onRefresh();
    } finally {
      setReorderingSceneId(null);
    }
  };

  // Xác nhận xóa cảnh
  const handleConfirmDelete = async () => {
    if (!deletingScene || isDeleting) return;

    setIsDeleting(true);
    setErrorMessage(null);

    try {
      await projectApi.deleteScene(projectId, deletingScene.sceneId);
      setDeletingScene(null);
      onRefresh();
    } catch (err: unknown) {
      const errorDto = err as ErrorDto;
      console.error("[SceneTable] Lỗi xóa phân cảnh:", errorDto);
      setErrorMessage(errorDto?.message || "Đã xảy ra lỗi khi xóa phân cảnh.");
    } finally {
      setIsDeleting(false);
    }
  };

  if (isLoading && scenes.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center p-12 text-center text-muted-foreground">
        <Loader2 className="h-6 w-6 animate-spin text-primary mb-2" />
        <p className="text-sm">Đang tải danh sách phân cảnh…</p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {errorMessage && (
        <Alert variant="destructive">
          <AlertTriangle className="h-4 w-4" />
          <AlertDescription>{errorMessage}</AlertDescription>
        </Alert>
      )}

      <div className="rounded-lg border border-border bg-card shadow-xs overflow-hidden">
        <Table aria-label="Danh sách phân cảnh">
          <TableHeader>
            <TableRow className="bg-muted/40 hover:bg-muted/40">
              <TableHead className="w-12 text-center font-bold">#</TableHead>
              <TableHead className="w-20">Xem trước</TableHead>
              <TableHead className="min-w-[220px]">Tên cảnh & Lời dẫn</TableHead>
              <TableHead className="w-28">Thời lượng</TableHead>
              <TableHead className="w-16 text-center">Ảnh</TableHead>
              <TableHead className="w-16 text-center">Prompt</TableHead>
              <TableHead className="w-16 text-center">Video</TableHead>
              <TableHead className="w-36">Âm thanh</TableHead>
              <TableHead className="w-28">Trạng thái</TableHead>
              <TableHead className="w-36 text-right">Thao tác</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {scenes.length === 0 ? (
              <TableRow>
                <TableCell
                  colSpan={10}
                  className="h-32 text-center text-sm text-muted-foreground"
                >
                  Dự án này chưa có phân cảnh nào. Hãy bấm "+ Thêm Cảnh" để bắt đầu!
                </TableCell>
              </TableRow>
            ) : (
              scenes.map((scene, idx) => {
                const hasImage = !!scene.imageAssetId;
                const hasPrompt = !!scene.promptText && scene.promptText.trim().length > 0;
                const hasVideo = !!scene.videoAssetId;
                const hasAudio = !!scene.audioAssetId;
                const isReorderingThis = reorderingSceneId === scene.sceneId;

                return (
                  <TableRow
                    key={scene.sceneId}
                    className={cn(
                      "transition-colors",
                      isReorderingThis && "bg-accent/50"
                    )}
                  >
                    {/* # Thứ tự */}
                    <TableCell className="text-center font-bold text-muted-foreground">
                      {scene.sceneSequence}
                    </TableCell>

                    {/* Thumbnail Xem trước & Thao tác chọn ảnh */}
                    <TableCell>
                      <div className="flex items-center gap-1">
                        <div
                          className={cn(
                            "w-12 h-9 rounded border text-xs font-medium flex items-center justify-center transition-[color,background-color,border-color,box-shadow,opacity,transform]",
                            hasImage
                              ? "border-success/40 bg-success/10 text-success dark:text-success"
                              : "border-dashed border-border bg-muted/40 text-muted-foreground"
                          )}
                          title={hasImage ? `Mã ảnh: ${scene.imageAssetId}` : "Chưa có ảnh minh họa"}
                        >
                          {activeAssetOperation?.sceneId === scene.sceneId && activeAssetOperation.type === "image" ? (
                            <Loader2 className="h-3.5 w-3.5 animate-spin" />
                          ) : (
                            <ImageIcon className="h-4 w-4" />
                          )}
                        </div>

                        <div className="flex flex-col gap-0.5">
                          {hasImage ? (
                            <div className="flex items-center gap-0.5">
                              <button
                                type="button"
                                onClick={() => setPreviewAssetId(scene.imageAssetId)}
                                className="p-1 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition"
                                title="Xem chi tiết ảnh"
                                aria-label="Xem chi tiết ảnh"
                              >
                                <Eye className="h-3 w-3" />
                              </button>
                              <button
                                type="button"
                                onClick={() => setPickerState({ scene, type: "Image" })}
                                className="p-1 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition"
                                title="Đổi từ Thư viện"
                                aria-label="Đổi từ Thư viện"
                              >
                                <Library className="h-3 w-3" />
                              </button>
                              <button
                                type="button"
                                onClick={() => handleDetachAsset(scene, "image")}
                                className="p-1 rounded hover:bg-destructive/10 text-muted-foreground hover:text-destructive transition"
                                title="Gỡ ảnh khỏi cảnh (không xóa file gốc)"
                                aria-label="Gỡ ảnh khỏi cảnh"
                              >
                                <X className="h-3 w-3" />
                              </button>
                            </div>
                          ) : (
                            <div className="flex items-center gap-0.5">
                              <button
                                type="button"
                                onClick={() => setPickerState({ scene, type: "Image" })}
                                className="p-1 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition"
                                title="Chọn ảnh từ Thư viện"
                                aria-label="Chọn ảnh từ Thư viện"
                              >
                                <Library className="h-3 w-3" />
                              </button>
                              <button
                                type="button"
                                onClick={() => handleSelectImage(scene.sceneId)}
                                className="p-1 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition"
                                title="Tải ảnh từ máy"
                                aria-label="Tải ảnh từ máy"
                              >
                                <Upload className="h-3 w-3" />
                              </button>
                            </div>
                          )}
                        </div>
                      </div>
                    </TableCell>

                    {/* Tên cảnh & Lời dẫn */}
                    <TableCell>
                      <div className="font-semibold text-foreground text-sm leading-tight">
                        {scene.name}
                      </div>
                      <div className="text-xs text-muted-foreground line-clamp-2 mt-1 leading-relaxed">
                        {scene.promptText || "(Chưa có lời dẫn / prompt)"}
                      </div>
                    </TableCell>

                    {/* Thời lượng */}
                    <TableCell className="tabular-nums text-muted-foreground text-xs font-mono">
                      {formatDuration(scene.targetDurationSeconds)} ({scene.targetDurationSeconds}s)
                    </TableCell>

                    {/* Trạng thái Ảnh */}
                    <TableCell className="text-center">
                      {hasImage ? (
                        <span
                          className="inline-flex items-center justify-center w-5 h-5 rounded-full bg-success/20 text-success dark:text-success"
                          title="Đã có ảnh minh họa"
                        >
                          <Check className="h-3 w-3" />
                        </span>
                      ) : (
                        <span
                          className="inline-block w-2 h-2 rounded-full bg-muted-foreground/30"
                          title="Chưa có ảnh minh họa"
                        />
                      )}
                    </TableCell>

                    {/* Trạng thái Prompt */}
                    <TableCell className="text-center">
                      {hasPrompt ? (
                        <span
                          className="inline-flex items-center justify-center w-5 h-5 rounded-full bg-success/20 text-success dark:text-success"
                          title="Đã có prompt chuyển động"
                        >
                          <Check className="h-3 w-3" />
                        </span>
                      ) : (
                        <span
                          className="inline-block w-2 h-2 rounded-full bg-muted-foreground/30"
                          title="Chưa có prompt"
                        />
                      )}
                    </TableCell>

                    {/* Trạng thái Video */}
                    <TableCell className="text-center">
                      {hasVideo ? (
                        <span
                          className="inline-flex items-center justify-center w-5 h-5 rounded-full bg-success/20 text-success dark:text-success"
                          title="Đã kết xuất video"
                        >
                          <Check className="h-3 w-3" />
                        </span>
                      ) : (
                        <span
                          className="inline-block w-2 h-2 rounded-full bg-muted-foreground/30"
                          title="Chưa có video"
                        />
                      )}
                    </TableCell>

                    {/* Cột Âm thanh độc lập & Thao tác nhập âm thanh */}
                    <TableCell>
                      <div className="flex items-center gap-1">
                        <div
                          className={cn(
                            "inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs font-medium border transition-colors",
                            hasAudio
                              ? "border-success/30 bg-success/15 text-success dark:text-success"
                              : "border-dashed border-border bg-transparent text-muted-foreground"
                          )}
                          title={hasAudio ? `Mã audio: ${scene.audioAssetId}` : "Chưa có âm thanh"}
                        >
                          {activeAssetOperation?.sceneId === scene.sceneId && activeAssetOperation.type === "audio" ? (
                            <Loader2 className="h-3 w-3 animate-spin" />
                          ) : (
                            <Music className="h-3 w-3" />
                          )}
                          <span>{hasAudio ? "Có audio" : "Trống"}</span>
                        </div>

                        <div className="flex items-center gap-0.5">
                          {hasAudio ? (
                            <>
                              <button
                                type="button"
                                onClick={() => setPreviewAssetId(scene.audioAssetId)}
                                className="p-1 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition"
                                title="Xem chi tiết audio"
                                aria-label="Xem chi tiết audio"
                              >
                                <Eye className="h-3 w-3" />
                              </button>
                              <button
                                type="button"
                                onClick={() => setPickerState({ scene, type: "Audio" })}
                                className="p-1 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition"
                                title="Đổi từ Thư viện"
                                aria-label="Đổi từ Thư viện"
                              >
                                <Library className="h-3 w-3" />
                              </button>
                              <button
                                type="button"
                                onClick={() => handleDetachAsset(scene, "audio")}
                                className="p-1 rounded hover:bg-destructive/10 text-muted-foreground hover:text-destructive transition"
                                title="Gỡ audio khỏi cảnh (không xóa file gốc)"
                                aria-label="Gỡ audio khỏi cảnh"
                              >
                                <X className="h-3 w-3" />
                              </button>
                            </>
                          ) : (
                            <>
                              <button
                                type="button"
                                onClick={() => setPickerState({ scene, type: "Audio" })}
                                className="p-1 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition"
                                title="Chọn audio từ Thư viện"
                                aria-label="Chọn audio từ Thư viện"
                              >
                                <Library className="h-3 w-3" />
                              </button>
                              <button
                                type="button"
                                onClick={() => handleImportAudio(scene.sceneId)}
                                className="p-1 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition"
                                title="Tải audio từ máy"
                                aria-label="Tải audio từ máy"
                              >
                                <Upload className="h-3 w-3" />
                              </button>
                            </>
                          )}
                        </div>
                      </div>
                    </TableCell>

                    {/* Trạng thái cảnh */}
                    <TableCell>
                      <Badge
                        variant={
                          scene.status === "Completed"
                            ? "success"
                            : scene.status === "Generating"
                            ? "info"
                            : scene.status === "PromptReady"
                            ? "secondary"
                            : "outline"
                        }
                        className="text-[11px]"
                      >
                        ● {scene.status}
                      </Badge>
                    </TableCell>

                    {/* Thao tác: Lên / Xuống / Sửa / Xóa */}
                    <TableCell className="text-right">
                      <div className="inline-flex items-center gap-1">
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="h-7 w-7"
                          disabled={idx === 0 || !!reorderingSceneId}
                          onClick={() => handleMove(idx, "up")}
                          title="Di chuyển lên trên"
                          aria-label="Di chuyển lên"
                        >
                          <ChevronUp className="h-3.5 w-3.5" />
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          size="icon"
                          className="h-7 w-7"
                          disabled={idx === scenes.length - 1 || !!reorderingSceneId}
                          onClick={() => handleMove(idx, "down")}
                          title="Di chuyển xuống dưới"
                          aria-label="Di chuyển xuống"
                        >
                          <ChevronDown className="h-3.5 w-3.5" />
                        </Button>
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          className="h-7 px-2 text-xs"
                          onClick={() => onEditScene(scene)}
                          title="Chỉnh sửa cảnh & prompt"
                        >
                          <Pencil className="mr-1 h-3 w-3" /> Sửa
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          className="h-7 px-2 text-xs text-destructive hover:bg-destructive/10 hover:text-destructive"
                          onClick={() => setDeletingScene(scene)}
                          title="Xóa cảnh"
                        >
                          <Trash2 className="mr-1 h-3 w-3" /> Xóa
                        </Button>
                      </div>
                    </TableCell>
                  </TableRow>
                );
              })
            )}
          </TableBody>
        </Table>
      </div>

      {/* Modal xác nhận xóa cảnh */}
      <AlertDialog
        open={!!deletingScene}
        onOpenChange={(open) => {
          if (!open && !isDeleting) {
            setDeletingScene(null);
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle className="text-destructive">
              Xác nhận xóa phân cảnh #{deletingScene?.sceneSequence}
            </AlertDialogTitle>
            <AlertDialogDescription className="space-y-2 text-sm text-muted-foreground">
              <span>
                Bạn có chắc chắn muốn xóa phân cảnh{" "}
                <strong className="text-foreground">{deletingScene?.name}</strong>?
              </span>
              <span className="block text-xs">
                Thứ tự các phân cảnh sau sẽ được tự động cập nhật liên tục.
              </span>
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={isDeleting} onClick={() => setDeletingScene(null)}>
              Hủy
            </AlertDialogCancel>
            <Button
              variant="destructive"
              disabled={isDeleting}
              onClick={handleConfirmDelete}
            >
              {isDeleting ? (
                <>
                  <Loader2 className="mr-2 h-4 w-4 animate-spin" /> Đang xóa…
                </>
              ) : (
                "Xác nhận xóa"
              )}
            </Button>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      {/* Modal chọn tài nguyên từ Thư viện */}
      {pickerState && (
        <AssetPicker
          projectId={projectId}
          assetType={pickerState.type}
          onSelect={handleSelectFromLibrary}
          onClose={() => setPickerState(null)}
        />
      )}

      {/* Modal xem trước chi tiết tài nguyên */}
      {previewAssetId && (
        <AssetPreviewDialog
          assetId={previewAssetId}
          onClose={() => setPreviewAssetId(null)}
        />
      )}
    </div>
  );
}
