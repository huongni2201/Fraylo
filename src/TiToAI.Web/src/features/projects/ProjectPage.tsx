import { useState, useEffect, useCallback, useRef, useMemo } from "react";
import {
  projectApi,
  type ProjectDetailDto,
  type SceneDto,
  STATUS_LABEL,
} from "./projectApi";
import { SceneTable } from "./SceneTable";
import { SceneForm } from "./SceneForm";
import { ProjectForm } from "./ProjectForm";
import { VideoProductionTab } from "./VideoProductionTab";
import type { ErrorDto } from "../../contracts/bridge";
import {
  Card,
  CardHeader,
  CardTitle,
  CardContent,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import { Progress } from "@/components/ui/progress";
import {
  ArrowLeft,
  Pencil,
  RefreshCw,
  Loader2,
  AlertCircle,
  Sparkles,
} from "lucide-react";

export interface ProjectPageProps {
  projectId: string;
  onBack: () => void;
  onOpenPublishing?: (projectId: string) => void;
}

type ProjectTab = "production" | "scenes" | "overview" | "assets";

export function ProjectPage({ projectId, onBack, onOpenPublishing }: ProjectPageProps) {
  const [project, setProject] = useState<ProjectDetailDto | null>(null);
  const [scenes, setScenes] = useState<SceneDto[]>([]);
  const [activeTab, setActiveTab] = useState<ProjectTab>("production");

  const [isLoading, setIsLoading] = useState(true);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  // Modals
  const [isSceneFormOpen, setIsSceneFormOpen] = useState(false);
  const [sceneFormMode, setSceneFormMode] = useState<"create" | "edit">("create");
  const [editingScene, setEditingScene] = useState<SceneDto | null>(null);

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
      const [prj, sceneList] = await Promise.all([
        projectApi.getProject(projectId),
        projectApi.listScenes(projectId),
      ]);

      if (controller.signal.aborted) return;

      setProject(prj);
      setScenes(sceneList);
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

  // Thống kê tổng hợp cho Tab Tổng quan
  const stats = useMemo(() => {
    const totalDuration = scenes.reduce((sum, s) => sum + (s.targetDurationSeconds || 0), 0);
    const withPrompt = scenes.filter((s) => s.promptText && s.promptText.trim().length > 0).length;
    const withImage = scenes.filter((s) => !!s.imageAssetId).length;
    const withAudio = scenes.filter((s) => !!s.audioAssetId).length;
    const withVideo = scenes.filter((s) => !!s.videoAssetId).length;
    const completed = scenes.filter((s) => s.status === "Completed").length;

    return {
      totalDuration,
      withPrompt,
      withImage,
      withAudio,
      withVideo,
      completed,
    };
  }, [scenes]);

  const handleEditScene = (scene: SceneDto) => {
    setEditingScene(scene);
    setSceneFormMode("edit");
    setIsSceneFormOpen(true);
  };

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
    <div className="space-y-6 pb-10">
      {/* Header dự án theo Panel 03 */}
      <Card className="p-5 shadow-xs">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
          <div className="flex items-start sm:items-center gap-3">
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={onBack}
              className="gap-1.5"
            >
              <ArrowLeft className="h-4 w-4" /> Quay lại
            </Button>

            <div>
              <div className="flex items-center gap-2.5 flex-wrap">
                <h2 className="text-xl font-bold tracking-tight text-foreground">
                  {project.name}
                </h2>
                <Badge
                  variant={
                    project.status === "Completed"
                      ? "success"
                      : project.status === "InProgress"
                      ? "info"
                      : "secondary"
                  }
                >
                  {STATUS_LABEL[project.status] ?? project.status}
                </Badge>
              </div>
              <p className="text-xs text-muted-foreground mt-0.5">
                {project.description || "Chưa có mô tả chi tiết."}
              </p>
            </div>
          </div>

          <div className="flex items-center gap-2 self-end sm:self-auto">
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setIsEditProjectOpen(true)}
              className="gap-1.5"
            >
              <Pencil className="h-3.5 w-3.5" /> Sửa dự án
            </Button>
          </div>
        </div>
      </Card>

      {/* Tabs Điều Hướng */}
      <Tabs
        value={activeTab}
        onValueChange={(val) => setActiveTab(val as ProjectTab)}
        className="w-full"
      >
        <TabsList className="grid w-full grid-cols-1 sm:w-auto sm:inline-flex h-10">
          <TabsTrigger value="production" className="gap-2">
            <Sparkles className="h-4 w-4 text-primary" />
            <span>Sản xuất video</span>
          </TabsTrigger>
        </TabsList>

        {/* Tab 1 (Mặc định): Sản xuất video */}
        <TabsContent value="production" className="mt-4">
          <VideoProductionTab
            projectId={projectId}
            onRefreshProject={fetchData}
            onOpenPublishing={onOpenPublishing}
          />
        </TabsContent>

        {/* Tab 2: Bảng phân cảnh */}
        <TabsContent value="scenes" className="mt-4">
          <SceneTable
            projectId={projectId}
            scenes={scenes}
            isLoading={isLoading}
            onRefresh={fetchData}
            onEditScene={handleEditScene}
          />
        </TabsContent>

        {/* Tab 2: Tổng quan tiến độ */}
        <TabsContent value="overview" className="mt-4 space-y-6">
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
            <Card className="p-4">
              <span className="text-xs font-medium text-muted-foreground">Tổng số cảnh</span>
              <div className="text-2xl font-bold text-foreground mt-1">{scenes.length}</div>
              <span className="text-xs text-muted-foreground mt-0.5 block">Phân đoạn nội dung</span>
            </Card>

            <Card className="p-4">
              <span className="text-xs font-medium text-muted-foreground">Thời lượng ước tính</span>
              <div className="text-2xl font-bold text-foreground mt-1">{stats.totalDuration}s</div>
              <span className="text-xs text-muted-foreground mt-0.5 block">
                Khoảng {Math.floor(stats.totalDuration / 60)}m {stats.totalDuration % 60}s
              </span>
            </Card>

            <Card className="p-4">
              <span className="text-xs font-medium text-muted-foreground">Cảnh đã có Prompt</span>
              <div className="text-2xl font-bold text-foreground mt-1">
                {stats.withPrompt} / {scenes.length}
              </div>
              <span className="text-xs text-muted-foreground mt-0.5 block">
                {scenes.length > 0 ? Math.round((stats.withPrompt / scenes.length) * 100) : 0}% hoàn thành
              </span>
            </Card>

            <Card className="p-4">
              <span className="text-xs font-medium text-muted-foreground">Cảnh đã kết xuất Video</span>
              <div className="text-2xl font-bold text-foreground mt-1">
                {stats.withVideo} / {scenes.length}
              </div>
              <span className="text-xs text-muted-foreground mt-0.5 block">
                {scenes.length > 0 ? Math.round((stats.withVideo / scenes.length) * 100) : 0}% hoàn thành
              </span>
            </Card>
          </div>

          <Card>
            <CardHeader>
              <CardTitle className="text-base font-semibold">
                Tiến độ chuẩn bị sản xuất
              </CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-1.5">
                <div className="flex justify-between text-xs font-medium">
                  <span>1. Lời dẫn & Kịch bản</span>
                  <span className="text-muted-foreground">
                    {stats.withPrompt}/{scenes.length} cảnh (
                    {scenes.length ? Math.round((stats.withPrompt / scenes.length) * 100) : 0}%)
                  </span>
                </div>
                <Progress
                  value={scenes.length ? (stats.withPrompt / scenes.length) * 100 : 0}
                  className="h-2 [&>div]:bg-success"
                />
              </div>

              <div className="space-y-1.5">
                <div className="flex justify-between text-xs font-medium">
                  <span>2. Hình ảnh minh họa AI</span>
                  <span className="text-muted-foreground">
                    {stats.withImage}/{scenes.length} cảnh (
                    {scenes.length ? Math.round((stats.withImage / scenes.length) * 100) : 0}%)
                  </span>
                </div>
                <Progress
                  value={scenes.length ? (stats.withImage / scenes.length) * 100 : 0}
                  className="h-2 [&>div]:bg-primary"
                />
              </div>

              <div className="space-y-1.5">
                <div className="flex justify-between text-xs font-medium">
                  <span>3. Âm thanh gốc</span>
                  <span className="text-muted-foreground">
                    {stats.withAudio}/{scenes.length} cảnh (
                    {scenes.length ? Math.round((stats.withAudio / scenes.length) * 100) : 0}%)
                  </span>
                </div>
                <Progress
                  value={scenes.length ? (stats.withAudio / scenes.length) * 100 : 0}
                  className="h-2 [&>div]:bg-info"
                />
              </div>

              <div className="space-y-1.5">
                <div className="flex justify-between text-xs font-medium">
                  <span>4. Kết xuất Video MuseAI</span>
                  <span className="text-muted-foreground">
                    {stats.withVideo}/{scenes.length} cảnh (
                    {scenes.length ? Math.round((stats.withVideo / scenes.length) * 100) : 0}%)
                  </span>
                </div>
                <Progress
                  value={scenes.length ? (stats.withVideo / scenes.length) * 100 : 0}
                  className="h-2 [&>div]:bg-warning"
                />
              </div>
            </CardContent>
          </Card>
        </TabsContent>

        {/* Tab 3: Tài nguyên phân cảnh */}
        <TabsContent value="assets" className="mt-4">
          <Card>
            <CardHeader>
              <CardTitle className="text-base font-semibold">
                Danh mục tệp tài nguyên phân cảnh
              </CardTitle>
            </CardHeader>
            <CardContent>
              {scenes.every((s) => !s.imageAssetId && !s.audioAssetId && !s.videoAssetId) ? (
                <div className="text-center py-8 text-sm text-muted-foreground">
                  Chưa có tài nguyên media nào được tạo hoặc nhập vào các phân cảnh.
                </div>
              ) : (
                <div className="divide-y divide-border">
                  {scenes.map((s) => (
                    <div
                      key={s.sceneId}
                      className="flex flex-col sm:flex-row sm:items-center sm:justify-between py-3 gap-2 text-xs"
                    >
                      <span className="font-semibold text-foreground">
                        Cảnh #{s.sceneSequence}: {s.name}
                      </span>
                      <div className="flex items-center gap-4 text-muted-foreground">
                        <span>Ảnh: {s.imageAssetId || "—"}</span>
                        <span>Âm thanh: {s.audioAssetId || "—"}</span>
                        <span>Video: {s.videoAssetId || "—"}</span>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>

      {/* Modal Form Cảnh */}
      <SceneForm
        isOpen={isSceneFormOpen}
        mode={sceneFormMode}
        projectId={projectId}
        scene={editingScene}
        onClose={() => setIsSceneFormOpen(false)}
        onSuccess={() => {
          setIsSceneFormOpen(false);
          fetchData();
        }}
      />

      {/* Modal Form Sửa Dự án */}
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
