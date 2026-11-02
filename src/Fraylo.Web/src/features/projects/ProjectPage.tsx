import { useState, useEffect, useCallback, useRef } from "react";
import {
  projectApi,
  type ProjectDetailDto,
  STATUS_LABEL,
} from "./projectApi";
import { ProjectForm } from "./ProjectForm";
import { VideoProductionTab } from "./VideoProductionTab";
import { videoWorkflowApi, WorkflowRunState } from "./videoWorkflowApi";
import type { ErrorDto } from "../../contracts/bridge";
import { Card } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  ArrowLeft,
  Pencil,
  RefreshCw,
  Loader2,
  AlertCircle,
  ChevronRight,
} from "lucide-react";

export interface ProjectPageProps {
  projectId: string;
  onBack: () => void;
  onOpenPublishing?: (projectId: string) => void;
}

export function ProjectPage({ projectId, onBack, onOpenPublishing }: ProjectPageProps) {
  const [project, setProject] = useState<ProjectDetailDto | null>(null);
  const [runState, setRunState] = useState<string | null>(null);

  const [isLoading, setIsLoading] = useState(true);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  const [isEditProjectOpen, setIsEditProjectOpen] = useState(false);

  const abortControllerRef = useRef<AbortController | null>(null);

  const fetchData = useCallback(async () => {
    if (!projectId) return;

    if (abortControllerRef.current) {
      abortControllerRef.current.abort();
    }
    const controller = new AbortController();
    abortControllerRef.current = controller;

    setIsLoading(true);
    setErrorMessage(null);

    try {
      const [prj, config] = await Promise.all([
        projectApi.getProject(projectId),
        videoWorkflowApi.getConfig(projectId),
      ]);

      if (controller.signal.aborted) return;

      setProject(prj);
      setRunState(config.runState);
    } catch (err: unknown) {
      if (controller.signal.aborted) return;
      const errorDto = err as ErrorDto;
      console.error("[ProjectPage] Lỗi tải dữ liệu dự án:", errorDto);
      setErrorMessage(errorDto?.message || "Không thể tải dữ liệu chi tiết của dự án.");
    } finally {
      if (!controller.signal.aborted) {
        setIsLoading(false);
      }
    }
  }, [projectId]);

  useEffect(() => {
    fetchData();
    return () => {
      if (abortControllerRef.current) {
        abortControllerRef.current.abort();
      }
    };
  }, [fetchData]);

  if (isLoading && !project) {
    return (
      <Card className="flex flex-col items-center justify-center p-12 text-center mt-6">
        <Loader2 className="h-8 w-8 animate-spin text-primary mb-3" />
        <p className="text-sm text-muted-foreground">Đang tải thông tin chi tiết dự án…</p>
      </Card>
    );
  }

  if (errorMessage || !project) {
    return (
      <Card className="p-8 text-center border-destructive/50 bg-destructive/5 mt-6">
        <AlertCircle className="h-10 w-10 text-destructive mx-auto mb-3" />
        <h3 className="text-lg font-semibold text-foreground mb-1">Không thể tải thông tin dự án</h3>
        <p className="text-sm text-muted-foreground mb-4">
          {errorMessage || "Dự án không tồn tại hoặc đã bị xóa."}
        </p>
        <div className="flex items-center justify-center gap-2">
          <Button variant="outline" onClick={onBack}>
            <ArrowLeft className="mr-2 h-4 w-4" /> Quay lại danh sách dự án
          </Button>
          <Button variant="outline" onClick={fetchData}>
            <RefreshCw className="mr-2 h-4 w-4" /> Thử lại
          </Button>
        </div>
      </Card>
    );
  }

  return (
    <div className="space-y-5 pb-10">
      {/* Header gọn: đường dẫn quay lại + tên + trạng thái + sửa */}
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0 space-y-1">
          <nav aria-label="Đường dẫn" className="flex items-center gap-1 text-xs text-muted-foreground">
            <button
              type="button"
              onClick={onBack}
              className="inline-flex items-center gap-1 rounded px-1 py-0.5 transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <ArrowLeft className="size-3.5" aria-hidden="true" />
              {project.groupName ? project.groupName : "Dự án"}
            </button>
            <ChevronRight className="size-3" aria-hidden="true" />
            <span className="truncate text-foreground">{project.name}</span>
          </nav>
          <div className="flex flex-wrap items-center gap-2.5">
            <h2 className="text-xl font-bold tracking-tight text-foreground">{project.name}</h2>
            <Badge
              variant={
                runState === WorkflowRunState.Failed ? "destructive" : runState === WorkflowRunState.Paused ? "warning"
                  : project.status === "Completed" ? "success" : project.status === "InProgress" ? "info" : "secondary"
              }
            >
              {runState === WorkflowRunState.Failed ? "Thất bại" : runState === WorkflowRunState.Paused ? "Đang tạm dừng" : STATUS_LABEL[project.status] ?? project.status}
            </Badge>
          </div>
          {project.description && <p className="text-xs text-muted-foreground">{project.description}</p>}
        </div>

        <Button type="button" variant="outline" size="sm" onClick={() => setIsEditProjectOpen(true)} className="gap-1.5">
          <Pencil className="size-3.5" aria-hidden="true" /> Sửa dự án
        </Button>
      </header>

      <VideoProductionTab
        projectId={projectId}
        onRefreshProject={fetchData}
        onOpenPublishing={onOpenPublishing}
      />

      <ProjectForm
        isOpen={isEditProjectOpen}
        mode="edit"
        project={project}
        onClose={() => setIsEditProjectOpen(false)}
        onSuccess={() => {
          setIsEditProjectOpen(false);
          fetchData();
        }}
      />
    </div>
  );
}
