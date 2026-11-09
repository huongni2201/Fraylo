import { useState } from "react";
import { AlertCircle, CheckCircle2, Circle, Loader2 } from "lucide-react";
import { Button } from "../../components/ui/button";
import { AssetMediaPreview } from "../library/AssetMediaPreview";
import { AssetPreviewDialog } from "../library/AssetPreviewDialog";
import {
  WorkflowModes,
  WorkflowRunState,
  WorkflowStepOutcomes,
  storyboardGridSize,
  type ProjectWorkflowConfigDto,
  type SceneWorkflowDetailDto,
  type WorkflowStepKind,
  type WorkflowStepState,
} from "./videoWorkflowApi";
import { StepRetryAction, isStepStopped, stepReason } from "./WorkflowStepRetry";

export type GridMilestoneStatus = "done" | "active" | "failed" | "pending";

export interface GridMilestone {
  key: "script" | "compose" | "grid" | "prompt" | "video";
  label: string;
  status: GridMilestoneStatus;
  /** Lần thử hiện hành của bước từ backend; chỉ có khi config kèm steps. */
  step?: WorkflowStepState;
}

const milestoneStepKind: Record<GridMilestone["key"], WorkflowStepKind> = {
  script: "script",
  compose: "compose",
  grid: "grid",
  prompt: "prompt",
  video: "render",
};

/**
 * Mốc tiến trình lấy từ các bước của lượt hiện hành hoặc từ dữ liệu cảnh.
 * Mốc đầu tiên chưa xong là mốc đang chạy; run Failed thì đó là mốc lỗi.
 * Khi backend trả steps, bước đã dừng ở lỗi/bị loại/hủy luôn là mốc lỗi (kể cả khi còn ảnh hoặc prompt cũ),
 * và các mốc khác không bị gán lỗi.
 */
export function gridMilestones(config: ProjectWorkflowConfigDto, scene: SceneWorkflowDetailDto | undefined): GridMilestone[] {
  const size = storyboardGridSize(config.referenceImageMode);
  const steps: Array<[GridMilestone["key"], string, boolean]> = [
    ["script", `Viết ${size * size} cảnh`, !!scene],
    ...(config.workflowMode === WorkflowModes.ModelAndProduct
      ? [["compose", "Ghép ảnh mẫu + sản phẩm", !!scene?.composedImageAssetId] as [GridMilestone["key"], string, boolean]]
      : []),
    ["grid", `Tạo ảnh storyboard ${size}×${size}`, !!scene?.gridImageAssetId],
    ["prompt", "Viết prompt chuyển động", !!scene?.gridImageAssetId && !!scene.motionPromptText],
    ["video", "Tạo video", !!scene?.videoAssetId],
  ];
  const stepOf = (key: GridMilestone["key"]) => config.steps?.find(
    (step) => step.kind === milestoneStepKind[key] && (key === "script" || (!!scene && step.sceneId === scene.sceneId)),
  );
  // Bước đã có lần thử hiện hành thì kết quả của nó quyết định mốc xong hay chưa; ảnh/prompt cũ còn lại không đếm.
  const resolved = steps.map(([key, label, assetDone]) => {
    const step = stepOf(key);
    return { key, label, step, done: step ? step.outcome === WorkflowStepOutcomes.Succeeded : assetDone };
  });
  const firstOpen = resolved.findIndex(({ done }) => !done);
  const anyStepStopped = resolved.some(({ step }) => isStepStopped(step));
  const stopped = config.runState === WorkflowRunState.Failed && !anyStepStopped;
  const paused = config.runState === WorkflowRunState.Paused;
  return resolved.map(({ key, label, step, done }, index) => {
    const status: GridMilestoneStatus = isStepStopped(step) ? "failed"
      : done ? "done"
      : index !== firstOpen || anyStepStopped ? "pending"
      : stopped ? "failed"
      : paused || (key === "video" && (scene?.activeJobType !== "VideoRender" || scene.activeJobState !== "Running"))
        ? "pending" : "active";
    return { key, label, status, step };
  });
}

const statusText: Record<GridMilestoneStatus, string> = {
  done: "Hoàn tất",
  active: "Đang xử lý",
  failed: "Lỗi, xem nhật ký",
  pending: "Chờ",
};

function StatusIcon({ status }: { status: GridMilestoneStatus }) {
  const icon = status === "done" ? <CheckCircle2 className="size-4 text-success" aria-hidden="true" />
    : status === "active" ? <Loader2 className="size-4 animate-spin text-primary" aria-hidden="true" />
    : status === "failed" ? <AlertCircle className="size-4 text-destructive" aria-hidden="true" />
    : <Circle className="size-4 text-muted-foreground" aria-hidden="true" />;
  // key theo trạng thái: đổi mốc thì icon chạy lại animation status-pop.
  return <span key={status} className="status-pop inline-flex shrink-0">{icon}</span>;
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

/** Mốc tiến trình, ảnh đầu vào, ảnh ghép và ảnh storyboard gửi render. */
export function StoryboardGridProgress({ config, scene, retryingJobId = null, onRetryStep }: {
  config: ProjectWorkflowConfigDto;
  scene: SceneWorkflowDetailDto | undefined;
  retryingJobId?: string | null;
  onRetryStep?: (step: WorkflowStepState) => void;
}) {
  const [zoomAssetId, setZoomAssetId] = useState<string | null>(null);
  const milestones = gridMilestones(config, scene);
  const size = storyboardGridSize(config.referenceImageMode);
  const inputAssetId =
    config.workflowMode === WorkflowModes.ReadyImage ? config.readyImageAssetId : config.productAssetId;
  const inputTitle = config.workflowMode === WorkflowModes.ReadyImage ? "Ảnh sẵn sàng (đầu vào)" : "Ảnh sản phẩm (đầu vào)";

  return (
    <section className="space-y-4" aria-label={`Tiến trình ảnh storyboard ${size}×${size}`}>
      <ol className="space-y-2">
        {milestones.map((milestone) => (
          <li key={milestone.key} className="space-y-1.5 text-sm">
            <div className="flex items-center gap-2">
              <StatusIcon status={milestone.status} />
              <span className={milestone.status === "pending" ? "text-muted-foreground" : "text-foreground"}>{milestone.label}</span>
              <span className="text-xs text-muted-foreground">· {statusText[milestone.status]}</span>
            </div>
            {isStepStopped(milestone.step) && (
              <div className="ml-6 flex flex-wrap items-center gap-x-3 gap-y-1.5" role="alert">
                <p className="text-xs text-destructive">{stepReason(milestone.step)}</p>
                {onRetryStep && (
                  <StepRetryAction
                    step={milestone.step}
                    label={milestone.label}
                    retrying={retryingJobId === milestone.step.jobId}
                    onRetry={onRetryStep}
                  />
                )}
              </div>
            )}
          </li>
        ))}
      </ol>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <ImageTile title={inputTitle} assetId={inputAssetId} onZoom={setZoomAssetId} />
        {config.workflowMode === WorkflowModes.ModelAndProduct && (
          <ImageTile title="Ảnh ghép (trung gian)" assetId={scene?.composedImageAssetId} onZoom={setZoomAssetId} />
        )}
        <ImageTile title={`Ảnh storyboard ${size}×${size} (gửi render)`} assetId={scene?.gridImageAssetId} onZoom={setZoomAssetId} />
      </div>
      {zoomAssetId && <AssetPreviewDialog assetId={zoomAssetId} onClose={() => setZoomAssetId(null)} />}
    </section>
  );
}
