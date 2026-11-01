import { useState } from "react";
import { AlertCircle, CheckCircle2, Circle, Loader2 } from "lucide-react";
import { Button } from "../../components/ui/button";
import { AssetMediaPreview } from "../library/AssetMediaPreview";
import { AssetPreviewDialog } from "../library/AssetPreviewDialog";
import {
  WorkflowModes,
  WorkflowRunState,
  type ProjectWorkflowConfigDto,
  type SceneWorkflowDetailDto,
} from "./videoWorkflowApi";

export type GridMilestoneStatus = "done" | "active" | "failed" | "pending";

export interface GridMilestone {
  key: "script" | "compose" | "grid" | "prompt" | "video";
  label: string;
  status: GridMilestoneStatus;
}

/**
 * Mốc tiến trình của chế độ ảnh 9 ô, suy ra từ dữ liệu thật của cảnh (không dùng timer):
 * viết 9 cảnh → [ghép ảnh, chỉ Mẫu + sản phẩm] → ảnh storyboard 3×3 → prompt chuyển động → video.
 * Mốc đầu tiên chưa xong là mốc đang chạy; run Failed thì đó là mốc lỗi.
 */
export function gridMilestones(config: ProjectWorkflowConfigDto, scene: SceneWorkflowDetailDto | undefined): GridMilestone[] {
  const steps: Array<[GridMilestone["key"], string, boolean]> = [
    ["script", "Viết 9 cảnh", !!scene],
    ...(config.workflowMode === WorkflowModes.ModelAndProduct
      ? [["compose", "Ghép ảnh mẫu + sản phẩm", !!scene?.composedImageAssetId] as [GridMilestone["key"], string, boolean]]
      : []),
    ["grid", "Tạo ảnh storyboard 3×3", !!scene?.gridImageAssetId],
    ["prompt", "Viết prompt chuyển động", !!scene?.gridImageAssetId && !!scene.motionPromptText],
    ["video", "Tạo video 15 giây", !!scene?.videoAssetId],
  ];
  const firstOpen = steps.findIndex(([, , done]) => !done);
  const stopped = config.runState === WorkflowRunState.Failed;
  return steps.map(([key, label, done], index) => ({
    key,
    label,
    status: done ? "done" : index === firstOpen ? (stopped ? "failed" : "active") : "pending",
  }));
}

const statusText: Record<GridMilestoneStatus, string> = {
  done: "Hoàn tất",
  active: "Đang xử lý",
  failed: "Lỗi, xem nhật ký",
  pending: "Chờ",
};

function StatusIcon({ status }: { status: GridMilestoneStatus }) {
  if (status === "done") return <CheckCircle2 className="size-4 text-success" aria-hidden="true" />;
  if (status === "active") return <Loader2 className="size-4 animate-spin text-primary" aria-hidden="true" />;
  if (status === "failed") return <AlertCircle className="size-4 text-destructive" aria-hidden="true" />;
  return <Circle className="size-4 text-muted-foreground" aria-hidden="true" />;
}

function ImageTile({ title, assetId, onZoom }: { title: string; assetId: string | null | undefined; onZoom: (assetId: string) => void }) {
  return (
    <figure className="space-y-1.5 min-w-0">
      <figcaption className="text-xs font-medium text-foreground">{title}</figcaption>
      {assetId ? (
        <>
          <AssetMediaPreview assetId={assetId} assetType="image" fileName={title} heightClass="h-40" />
          <Button type="button" variant="outline" size="sm" onClick={() => onZoom(assetId)} aria-label={`Phóng to: ${title}`}>
            Phóng to
          </Button>
        </>
      ) : (
        <div className="h-40 flex items-center justify-center rounded bg-muted text-xs text-muted-foreground">Chưa có</div>
      )}
    </figure>
  );
}

/** Mốc tiến trình và ảnh của chế độ 9 ô: ảnh đầu vào, ảnh ghép trung gian (nếu có) và ảnh storyboard 3×3 đầu ra. */
export function StoryboardGridProgress({ config, scene }: { config: ProjectWorkflowConfigDto; scene: SceneWorkflowDetailDto | undefined }) {
  const [zoomAssetId, setZoomAssetId] = useState<string | null>(null);
  const milestones = gridMilestones(config, scene);
  const inputAssetId =
    config.workflowMode === WorkflowModes.ReadyImage ? config.readyImageAssetId : config.productAssetId;
  const inputTitle = config.workflowMode === WorkflowModes.ReadyImage ? "Ảnh sẵn sàng (đầu vào)" : "Ảnh sản phẩm (đầu vào)";

  return (
    <section className="space-y-4" aria-label="Tiến trình ảnh storyboard 9 ô">
      <ol className="space-y-2">
        {milestones.map((milestone) => (
          <li key={milestone.key} className="flex items-center gap-2 text-sm">
            <StatusIcon status={milestone.status} />
            <span className={milestone.status === "pending" ? "text-muted-foreground" : "text-foreground"}>{milestone.label}</span>
            <span className="text-xs text-muted-foreground">· {statusText[milestone.status]}</span>
          </li>
        ))}
      </ol>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <ImageTile title={inputTitle} assetId={inputAssetId} onZoom={setZoomAssetId} />
        {config.workflowMode === WorkflowModes.ModelAndProduct && (
          <ImageTile title="Ảnh ghép (trung gian)" assetId={scene?.composedImageAssetId} onZoom={setZoomAssetId} />
        )}
        <ImageTile title="Ảnh storyboard 3×3 (gửi render)" assetId={scene?.gridImageAssetId} onZoom={setZoomAssetId} />
      </div>
      {zoomAssetId && <AssetPreviewDialog assetId={zoomAssetId} onClose={() => setZoomAssetId(null)} />}
    </section>
  );
}
