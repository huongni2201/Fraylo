import { AlertCircle } from "lucide-react";
import { Button } from "../../components/ui/button";
import { formatReasonCode } from "../jobs/jobTypes";
import {
  WorkflowStepKinds,
  WorkflowStepOutcomes,
  type SceneWorkflowDetailDto,
  type WorkflowStepKind,
  type WorkflowStepState,
} from "./videoWorkflowApi";

export const stepKindLabel: Record<WorkflowStepKind, string> = {
  [WorkflowStepKinds.Script]: "Viết cảnh",
  [WorkflowStepKinds.Compose]: "Ghép ảnh mẫu + sản phẩm",
  [WorkflowStepKinds.Grid]: "Tạo ảnh storyboard",
  [WorkflowStepKinds.Image]: "Tạo ảnh cảnh",
  [WorkflowStepKinds.Prompt]: "Viết prompt chuyển động",
  [WorkflowStepKinds.Render]: "Tạo video",
};

/** Bước đã dừng ở lỗi: job lỗi, job báo xong nhưng kết quả bị loại, hoặc đã hủy. */
export function isStepStopped(step: WorkflowStepState | undefined): step is WorkflowStepState {
  return !!step && (
    step.outcome === WorkflowStepOutcomes.Failed
    || step.outcome === WorkflowStepOutcomes.Rejected
    || step.outcome === WorkflowStepOutcomes.Canceled
  );
}

export function stepReason(step: WorkflowStepState): string {
  if (step.outcome === WorkflowStepOutcomes.Canceled) return "Tác vụ đã bị hủy.";
  if (step.outcome === WorkflowStepOutcomes.Rejected) return step.reasonMessage || "Kết quả không hợp lệ.";
  return formatReasonCode(step.reasonCode) || "Tác vụ thất bại, xem nhật ký bên dưới.";
}

/** Nút "Thử lại bước này", hoặc lý do không thử lại được. Bước đã hủy không có retry. */
export function StepRetryAction({ step, label, retrying, onRetry }: {
  step: WorkflowStepState;
  label: string;
  retrying: boolean;
  onRetry: (step: WorkflowStepState) => void;
}) {
  if (step.outcome === WorkflowStepOutcomes.Canceled) return null;
  if (step.canRetry) {
    return (
      <Button
        type="button"
        variant="outline"
        size="sm"
        disabled={retrying}
        onClick={() => onRetry(step)}
        aria-label={`Thử lại bước: ${label}`}
      >
        {retrying ? "Đang đưa vào hàng đợi…" : "Thử lại bước này"}
      </Button>
    );
  }
  return step.blockedReason ? <p className="text-xs text-muted-foreground">{step.blockedReason}</p> : null;
}

/**
 * Danh sách bước đã dừng ở lỗi, mỗi bước một dòng kèm nút thử lại. Tiến trình storyboard có nút riêng tại mỗi mốc.
 */
export function WorkflowStepIssues({ steps, scenes, retryingJobId, onRetry }: {
  steps: WorkflowStepState[] | null | undefined;
  scenes: SceneWorkflowDetailDto[];
  retryingJobId: string | null;
  onRetry: (step: WorkflowStepState) => void;
}) {
  const stopped = (steps ?? []).filter(isStepStopped);
  if (stopped.length === 0) return null;
  return (
    <ul className="space-y-2" aria-label="Các bước đang dừng ở lỗi">
      {stopped.map((step) => {
        const sceneName = step.sceneId ? scenes.find((scene) => scene.sceneId === step.sceneId)?.name : undefined;
        const label = sceneName ? `${stepKindLabel[step.kind]} · ${sceneName}` : stepKindLabel[step.kind];
        return (
          <li
            key={step.jobId}
            role="alert"
            className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-md border border-destructive/20 bg-destructive/10 p-3 text-sm"
          >
            <AlertCircle className="size-4 shrink-0 text-destructive" aria-hidden="true" />
            <div className="min-w-0 flex-1">
              <p className="font-medium text-foreground">{label}</p>
              <p className="text-xs text-muted-foreground">{stepReason(step)}</p>
            </div>
            <StepRetryAction step={step} label={label} retrying={retryingJobId === step.jobId} onRetry={onRetry} />
          </li>
        );
      })}
    </ul>
  );
}
