import { jobApi } from "../jobs/jobApi";
import { useState, useEffect, useCallback, useMemo } from "react";
import { StoryboardPanel } from "./StoryboardPanel";
import { ReferenceVideoInputs } from "./ReferenceVideoInputs";
import { ProjectActivityLog } from "./ProjectActivityLog";
import { StoryboardGridProgress } from "./StoryboardGridProgress";
import { WorkflowStepIssues } from "./WorkflowStepRetry";
import {
  videoWorkflowApi,
  WorkflowModes,
  VideoStrategies,
  ReferenceImageModes,
  AudioOptions,
  WorkflowRunState,
  SceneWorkflowStatus,
  type WorkflowMode,
  type VideoStrategy,
  type AudioOption,
  type ProjectWorkflowConfigDto,
  type SceneWorkflowDetailDto,
  type PreviewAiPromptResponseData,
  type VideoContentItemDto,
  type WorkflowStepState,
  DEFAULT_SUPPORTED_DURATIONS,
} from "./videoWorkflowApi";
import {
  Table,
  TableHeader,
  TableBody,
  TableRow,
  TableHead,
  TableCell,
} from "@/components/ui/table";
import { AssetPicker } from "../library/AssetPicker";
import { AssetMediaPreview } from "../library/AssetMediaPreview";
import { TikTokPostPreview } from "./TikTokPostPreview";
import { promptApi } from "../prompts/promptApi";
import {
  getActivePromptId,
  getStoredPrompts,
  DEFAULT_PRESET_PROMPTS,
} from "../prompts/promptStorage";
import { openAssetFolder, uploadAsset, type AssetItem } from "../library/libraryApi";
import { Card, CardHeader, CardTitle, CardContent, CardFooter } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { PromptCell } from "@/components/PromptCell";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { Progress } from "@/components/ui/progress";
import { Separator } from "@/components/ui/separator";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import {
  Sparkles,
  Play,
  RotateCcw,
  Pause,
  AlertCircle,
  CheckCircle2,
  Image as ImageIcon,
  Music,
  FolderOpen,
  Upload,
  X,
  Sliders,
  Layers,
  Film,
  Loader2,
  RefreshCw,
  Eye,
  Edit3,
  Video,
  FileText,
  Send,
} from "lucide-react";

async function loadActiveTemplateInfo(
  purpose: "Script" | "VideoPrompt"
): Promise<ActiveTemplateInfo | null> {
  try {
    const backendTemplate = await promptApi.getTemplate(undefined, purpose);
    const templateText = backendTemplate?.content?.templateText;
    if (templateText) {
      return {
        name: backendTemplate.template.name,
        isCustom: false,
      };
    }
  } catch {
    // Fallback sang prompt local nếu chưa đọc được template từ backend.
  }

  const activePromptId = getActivePromptId(purpose);
  const storedPrompt = activePromptId
    ? getStoredPrompts().find((prompt) => prompt.id === activePromptId) ||
      DEFAULT_PRESET_PROMPTS[purpose].find((prompt) => prompt.id === activePromptId)
    : null;
  const fallbackPrompt = storedPrompt || DEFAULT_PRESET_PROMPTS[purpose][0];
  if (!fallbackPrompt) return null;

  return {
    name: fallbackPrompt.name,
    isCustom: !fallbackPrompt.isPreset,
  };
}

const GUIDANCE_FIELDS = [
  { field: "scriptGuidance", label: "Prompt kịch bản" },
  { field: "videoGuidance", label: "Prompt video" },
] as const;

/** Prompt đang áp dụng theo thứ tự ưu tiên: Dự án > mẫu toàn cục (khớp VideoWorkflowPromptComposer.ResolvePromptAsync). */
function appliedPromptSource(
  projectText: string | null | undefined,
  globalName: string | undefined,
): { label: string; isGlobal: boolean } {
  if (projectText?.trim()) return { label: "Dự án", isGlobal: false };
  return { label: globalName || "Mẫu toàn cục", isGlobal: true };
}

export interface ActiveTemplateInfo {
  name: string;
  isCustom: boolean;
}

export interface VideoProductionTabProps {
  projectId: string;
  onRefreshProject?: () => void;
  onOpenPublishing?: (projectId: string, assetId: string) => void;
}

const STAGES = [
  { key: "preparing", label: "Chuẩn bị" },
  { key: "running", label: "Sản xuất" },
  { key: "completed", label: "Đăng" },
] as const;

/** Ba bước của một video; bước hiện tại nổi bật, bước đã qua có dấu tích. */
function StageStepper({ current }: { current: (typeof STAGES)[number]["key"] }) {
  const currentIndex = STAGES.findIndex((stage) => stage.key === current);
  return (
    <ol className="flex items-center gap-1.5" aria-label="Các bước sản xuất video">
      {STAGES.map((stage, index) => {
        const done = index < currentIndex;
        const active = index === currentIndex;
        return (
          <li key={stage.key} className="flex items-center gap-1.5" aria-current={active ? "step" : undefined}>
            <span
              className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-semibold ${
                active
                  ? "bg-primary text-primary-foreground"
                  : done
                    ? "bg-success/15 text-success"
                    : "bg-muted text-muted-foreground"
              }`}
            >
              {done ? <CheckCircle2 className="size-3.5" aria-hidden="true" /> : <span className="tabular-nums">{index + 1}</span>}
              {stage.label}
            </span>
            {index < STAGES.length - 1 && <span className="h-px w-4 bg-border" aria-hidden="true" />}
          </li>
        );
      })}
    </ol>
  );
}

export function VideoProductionTab({
  projectId,
  onRefreshProject,
  onOpenPublishing,
}: VideoProductionTabProps) {
  const [config, setConfig] = useState<ProjectWorkflowConfigDto | null>(null);
  const [scenes, setScenes] = useState<SceneWorkflowDetailDto[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [isStarting, setIsStarting] = useState(false);
  const [rerunRequested, setRerunRequested] = useState(false);
  // Job của bước đang gửi lệnh thử lại: khóa nút cho tới khi backend trả lời và đã tải lại trạng thái.
  const [retryingJobId, setRetryingJobId] = useState<string | null>(null);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  // Asset Pickers
  const [pickerTarget, setPickerTarget] = useState<"model" | "product" | "ready" | "audio" | null>(null);
  const [uploadingTarget, setUploadingTarget] = useState<"model" | "product" | "ready" | "audio" | null>(null);

  // Scene Edit Dialog
  const [editingScene, setEditingScene] = useState<SceneWorkflowDetailDto | null>(null);
  const [editPromptText, setEditPromptText] = useState("");
  const [isUpdatingScene, setIsUpdatingScene] = useState(false);

  // Video Preview Dialog
  const [previewVideoScene, setPreviewVideoScene] = useState<SceneWorkflowDetailDto | null>(null);

  // Workflow 4-Step Technical Details Dialog
  const [isDetailsOpen, setIsDetailsOpen] = useState(false);
  const [detailedScene, setDetailedScene] = useState<SceneWorkflowDetailDto | null>(null);

  // AI Prompt Preview Dialog (§3.1-3.3)
  const [isAiPreviewOpen, setIsAiPreviewOpen] = useState(false);
  const [aiPreviewData, setAiPreviewData] = useState<PreviewAiPromptResponseData | null>(null);
  const [isLoadingAiPreview, setIsLoadingAiPreview] = useState(false);

  // Dirty state tracking
  const [isDirty, setIsDirty] = useState(false);

  // Active Prompt Template Info & Dynamic Variables
  const [activeTemplateInfo, setActiveTemplateInfo] = useState<ActiveTemplateInfo | null>(null);
  const [videoPromptTemplateInfo, setVideoPromptTemplateInfo] = useState<ActiveTemplateInfo | null>(null);

  const scriptPromptSource = appliedPromptSource(config?.scriptGuidance, activeTemplateInfo?.name);
  const videoPromptSource = appliedPromptSource(config?.videoGuidance, videoPromptTemplateInfo?.name);
  const isReferenceVideo = config?.workflowMode === WorkflowModes.ReferenceVideo;

  const isGridMode =
    (config?.videoStrategy ?? VideoStrategies.MultiVideo) === VideoStrategies.SingleVideo &&
    config?.referenceImageMode === ReferenceImageModes.StoryboardGrid9;

  const saveConfigRequest = () => {
    if (!config) throw new Error("Cấu hình chưa sẵn sàng.");
    const strategy = config.videoStrategy ?? VideoStrategies.MultiVideo;
    return {
      projectId,
      workflowMode: config.workflowMode,
      goalPrompt: "",
      modelAssetId: config.modelAssetId,
      productAssetId: config.productAssetId,
      readyImageAssetId: config.readyImageAssetId,
      referenceVideoAssetId: isReferenceVideo ? config.referenceVideoAssetId : null,
      useGeminiReferenceAnalysis: isReferenceVideo && config.useGeminiReferenceAnalysis === true,
      audioOption: config.audioOption,
      audioAssetId: config.audioAssetId,
      videoStrategy: strategy,
      // Ảnh 9 ô chỉ hợp lệ với một video; các trường hợp khác luôn gửi Direct.
      referenceImageMode: isGridMode ? ReferenceImageModes.StoryboardGrid9 : ReferenceImageModes.Direct,
      // Một video nhiều phân cảnh luôn có một cảnh trong bảng; nhiều video dùng số cảnh người dùng chọn.
      targetSceneCount: strategy === VideoStrategies.SingleVideo ? 1 : config.targetSceneCount,
      targetDurationPerSceneSeconds: config.targetDurationPerSceneSeconds,
      scriptGuidance: config.scriptGuidance ?? "",
      videoGuidance: config.videoGuidance ?? "",
    };
  };

  // Video Content & Script Items (Table view for AI content generated)
  const [contentItems, setContentItems] = useState<VideoContentItemDto[]>([]);
  const [editingContentItem, setEditingContentItem] = useState<VideoContentItemDto | null>(null);
  const [editIdea, setEditIdea] = useState("");
  const [editHook, setEditHook] = useState("");
  const [editVideoScript, setEditVideoScript] = useState("");
  const [editVideoPrompt, setEditVideoPrompt] = useState("");
  const [editTikTokContent, setEditTikTokContent] = useState("");
  const [isUpdatingContent, setIsUpdatingContent] = useState(false);
  const [submittingVideoItemId, setSubmittingVideoItemId] = useState<string | null>(null);
  const [isPreparingTikTokExport, setIsPreparingTikTokExport] = useState(false);

  // Fetch initial config & scene list & content items & prompt template
  const loadData = useCallback(async () => {
    if (!projectId) return;
    try {
      setIsLoading(true);
      setErrorMessage(null);
      const [cfg, scns, cItems] = await Promise.all([
        videoWorkflowApi.getConfig(projectId),
        videoWorkflowApi.listScenes(projectId),
        videoWorkflowApi.listContentItems(projectId),
      ]);

      setConfig({ ...cfg, goalPrompt: "" });

      setScenes(scns);
      setContentItems(cItems);

      const [scriptTemplate, videoPromptTemplate] = await Promise.all([
        loadActiveTemplateInfo("Script"),
        loadActiveTemplateInfo("VideoPrompt"),
      ]);
      setActiveTemplateInfo(scriptTemplate);
      setVideoPromptTemplateInfo(videoPromptTemplate);
    } catch (err: unknown) {
      console.error("[VideoProductionTab] Lỗi tải dữ liệu quy trình:", err);
      setErrorMessage((err as Error)?.message || "Không thể tải cấu hình quy trình sản xuất video.");
    } finally {
      setIsLoading(false);
    }
  }, [projectId]);

  // Open Edit Content Modal
  const handleOpenEditContent = (item: VideoContentItemDto) => {
    setEditingContentItem(item);
    setEditIdea(item.idea || "");
    setEditHook(item.hook || "");
    setEditVideoScript(item.videoScript || "");
    setEditVideoPrompt(item.videoPrompt || "");
    setEditTikTokContent(item.tiktokContent || "");
  };

  // Save Content Edit
  const handleSaveContentEdit = async () => {
    if (!editingContentItem) return;
    try {
      setIsUpdatingContent(true);
      await videoWorkflowApi.updateContentItem({
        projectId,
        itemId: editingContentItem.id,
        idea: editIdea,
        hook: editHook,
        videoScript: editVideoScript,
        videoPrompt: editVideoPrompt,
        tiktokContent: editTikTokContent,
      });
      setEditingContentItem(null);
      setSuccessMessage("Đã cập nhật nội dung kịch bản & prompt AI thành công!");
      await loadData();
    } catch (err: unknown) {
      setErrorMessage((err as Error)?.message || "Không thể cập nhật kịch bản.");
    } finally {
      setIsUpdatingContent(false);
    }
  };

  // Trigger Video Generation for a specific content item
  const handleCreateSceneVideo = async (item: VideoContentItemDto) => {
    try {
      setSubmittingVideoItemId(item.id);
      setErrorMessage(null);
      await videoWorkflowApi.createSceneVideo({
        projectId,
        itemId: item.id,
      });
      setSuccessMessage(`Đã đưa yêu cầu tạo video cho Cảnh ${item.sequenceOrder} vào hàng đợi xử lý!`);
      await loadData();
      onRefreshProject?.();
    } catch (err: unknown) {
      setErrorMessage((err as Error)?.message || "Không thể tạo video cho cảnh.");
    } finally {
      setSubmittingVideoItemId(null);
    }
  };

  const handlePrepareTikTokExport = async () => {
    // Nhiều video: các clip cùng nằm trong thư mục video của Project nên mở theo clip đầu.
    const exportAssetId = finalVideoAssetId ?? (multiVideoDone ? scenes[0]?.videoAssetId ?? null : null);
    if (!exportAssetId || hasStaleScene) {
      setErrorMessage("Cần video hoàn chỉnh của Project, không có phân cảnh nào bị đánh dấu cần xem lại, trước khi chuẩn bị đăng TikTok.");
      return;
    }

    setIsPreparingTikTokExport(true);
    setErrorMessage(null);
    try {
      // Caption phải thuộc đúng clip đang mở, không lấy của cảnh khác.
      const caption = tiktokPreviews.find((preview) => preview.assetId === exportAssetId)?.caption;
      let captionCopied = false;
      if (caption) {
        try {
          await navigator.clipboard.writeText(caption);
          captionCopied = true;
        } catch {
          // File export remains useful if clipboard permission is unavailable.
        }
      }

      const result = await openAssetFolder({
        assetId: exportAssetId,
      });
      if (!result.success) {
        throw new Error("Không mở được thư mục chứa video. Hãy mở thư viện để kiểm tra tệp.");
      }

      setSuccessMessage(
        captionCopied
          ? "Đã mở thư mục video và sao chép caption. Hãy tự tải video lên TikTok; Fraylo chưa xác nhận bài đăng."
          : caption
            ? "Đã mở thư mục video. Không sao chép được caption; hãy sao chép thủ công rồi tự đăng TikTok."
            : "Đã mở thư mục video. Chưa có caption để sao chép; hãy thêm nội dung rồi tự đăng TikTok.",
      );
    } catch (error) {
      setErrorMessage(error instanceof Error ? error.message : "Không thể chuẩn bị tệp đăng TikTok.");
    } finally {
      setIsPreparingTikTokExport(false);
    }
  };

  useEffect(() => {
    loadData();
  }, [loadData]);

  // Job đổi trạng thái (xong Script/ảnh/render...) thì tải lại cấu hình và cảnh; bỏ qua khi người dùng đang sửa dở để không mất nháp.
  useEffect(() => {
    if (isDirty) return;
    return jobApi.onStateChanged((event) => {
      if (event.payload.projectId === projectId) {
        void loadData();
        onRefreshProject?.();
      }
    });
  }, [isDirty, loadData, projectId, onRefreshProject]);

  // Đồng bộ cả workflow lẫn cảnh: sự kiện job có thể đến trước apply hoặc bị bỏ lỡ.
  const hasRunningContentItem = submittingVideoItemId !== null
    || contentItems.some((item) => item.status === "Queued" || item.status === "InProgress");
  const runState = config?.runState;
  useEffect(() => {
    if (!projectId || (!hasRunningContentItem && runState !== WorkflowRunState.Running && runState !== WorkflowRunState.Paused)) return;
    let active = true;
    let refreshing = false;
    const timer = window.setInterval(async () => {
      if (refreshing) return;
      refreshing = true;
      try {
        const [cfg, scns, items] = await Promise.all([
          videoWorkflowApi.getConfig(projectId),
          videoWorkflowApi.listScenes(projectId),
          videoWorkflowApi.listContentItems(projectId),
        ]);
        if (!active) return;
        if (!isDirty) setConfig({ ...cfg, goalPrompt: "" });
        setScenes(scns);
        setContentItems(items);
        if (cfg.runState !== runState) onRefreshProject?.();
      } catch {
        // Lỗi tạm thời: giữ dữ liệu cũ, lần sau thử lại.
      } finally {
        refreshing = false;
      }
    }, 3000);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [projectId, hasRunningContentItem, runState, isDirty, onRefreshProject]);

  // Handle Mode Change
  const handleModeChange = (mode: WorkflowMode) => {
    if (!config) return;
    setConfig({
      ...config,
      workflowMode: mode,
      ...(mode === WorkflowModes.ReferenceVideo ? {
        videoStrategy: VideoStrategies.SingleVideo,
        referenceImageMode: ReferenceImageModes.Direct,
        targetSceneCount: 1,
        targetDurationPerSceneSeconds: 10,
        audioOption: AudioOptions.None,
        audioAssetId: null,
      } : {}),
    });
    setIsDirty(true);
  };

  const handleStrategyChange = (strategy: VideoStrategy) => {
    setConfig((prev) =>
      prev
        ? {
            ...prev,
            videoStrategy: strategy,
            // Ảnh 9 ô chỉ có ở một video; đổi sang nhiều video thì tắt.
            referenceImageMode:
              strategy === VideoStrategies.SingleVideo ? prev.referenceImageMode : ReferenceImageModes.Direct,
          }
        : prev,
    );
    setIsDirty(true);
  };

  const handleGridModeChange = (enabled: boolean) => {
    setConfig((prev) =>
      prev
        ? {
            ...prev,
            referenceImageMode: enabled ? ReferenceImageModes.StoryboardGrid9 : ReferenceImageModes.Direct,
            targetDurationPerSceneSeconds: prev.targetDurationPerSceneSeconds,
          }
        : prev,
    );
    setIsDirty(true);
  };

  // Số cảnh của luồng nhiều video: backend giới hạn 1–10.
  const handleSceneCountChange = (count: number) => {
    setConfig((prev) => (prev ? { ...prev, targetSceneCount: Math.max(1, Math.min(10, count)) } : prev));
    setIsDirty(true);
  };

  // Hướng dẫn riêng của dự án.
  const handleGuidanceChange = (field: "scriptGuidance" | "videoGuidance", value: string) => {
    setConfig((prev) => (prev ? { ...prev, [field]: value } : prev));
    setIsDirty(true);
  };

  const handleAudioOptionChange = (option: AudioOption) => {
    if (!config) return;
    setConfig({
      ...config,
      audioOption: option,
      audioAssetId: option === AudioOptions.None ? null : config.audioAssetId,
    });
    setIsDirty(true);
  };

  const handleTargetDurationChange = (val: number) => {
    if (!config) return;
    setConfig({ ...config, targetDurationPerSceneSeconds: Math.max(3, Math.min(60, val)) });
    setIsDirty(true);
  };

  // Asset picker selections
  const handleSelectAsset = (asset: AssetItem) => {
    if (!config) return;

    if (pickerTarget === "model") {
      setConfig({ ...config, modelAssetId: asset.assetId });
    } else if (pickerTarget === "product") {
      setConfig({ ...config, productAssetId: asset.assetId });
    } else if (pickerTarget === "ready") {
      setConfig({ ...config, readyImageAssetId: asset.assetId });
    } else if (pickerTarget === "audio") {
      setConfig({ ...config, audioAssetId: asset.assetId, audioOption: AudioOptions.CustomAudio });
    }
    setIsDirty(true);
    setPickerTarget(null);
  };

  const handleClearAsset = (target: "model" | "product" | "ready" | "audio") => {
    if (!config) return;
    if (target === "model") setConfig({ ...config, modelAssetId: null });
    if (target === "product") setConfig({ ...config, productAssetId: null });
    if (target === "ready") setConfig({ ...config, readyImageAssetId: null });
    if (target === "audio") setConfig({ ...config, audioAssetId: null, audioOption: AudioOptions.None });
    setIsDirty(true);
  };

  const handleTriggerUpload = async (target: "model" | "product" | "ready" | "audio") => {
    setUploadingTarget(target);
    setErrorMessage(null);
    try {
      const isAudio = target === "audio";
      const res = await uploadAsset({
        projectId,
        assetType: isAudio ? "Audio" : "Image",
      });
      if (res.asset) {
        if (target === "model") {
          setConfig((prev) => (prev ? { ...prev, modelAssetId: res.asset!.assetId } : prev));
        } else if (target === "product") {
          setConfig((prev) => (prev ? { ...prev, productAssetId: res.asset!.assetId } : prev));
        } else if (target === "ready") {
          setConfig((prev) => (prev ? { ...prev, readyImageAssetId: res.asset!.assetId } : prev));
        } else if (target === "audio") {
          setConfig((prev) =>
            prev ? { ...prev, audioAssetId: res.asset!.assetId, audioOption: AudioOptions.CustomAudio } : prev
          );
        }
        setIsDirty(true);
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : "Tải lên tệp thất bại.";
      setErrorMessage(msg);
    } finally {
      setUploadingTarget(null);
    }
  };

  // Save Config
  const handleSaveConfig = async () => {
    if (!config) return;
    try {
      setErrorMessage(null);
      const saved = await videoWorkflowApi.saveConfig({
        ...saveConfigRequest(),
      });
      setConfig(saved);
      setIsDirty(false);
      setSuccessMessage("Đã lưu cấu hình dữ liệu ban đầu.");
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err: unknown) {
      setErrorMessage((err as Error)?.message || "Không thể lưu cấu hình.");
    }
  };

  // Preview AI Request (§3.1-3.3)
  const handleOpenAiPreview = async () => {
    if (!config) return;
    try {
      setIsLoadingAiPreview(true);
      setErrorMessage(null);

      // Lưu config nếu có thay đổi để preview khớp dữ liệu hiện hành
      if (isDirty) {
        await videoWorkflowApi.saveConfig({
          ...saveConfigRequest(),
        });
        setIsDirty(false);
      }

      const res = await videoWorkflowApi.previewAiPrompt({
        projectId,
      });
      setAiPreviewData(res);
      setIsAiPreviewOpen(true);
    } catch (err: unknown) {
      setErrorMessage((err as Error)?.message || "Không thể tải bản xem trước yêu cầu gửi AI.");
    } finally {
      setIsLoadingAiPreview(false);
    }
  };

  // Start Workflow (Bắt đầu tạo video 1 lần duy nhất)
  const handleStartWorkflow = async () => {
    if (!config) return;
    try {
      setIsStarting(true);
      setErrorMessage(null);

      const isSupportedDuration = DEFAULT_SUPPORTED_DURATIONS.includes(config.targetDurationPerSceneSeconds as (typeof DEFAULT_SUPPORTED_DURATIONS)[number]);
      if (!isSupportedDuration && !isReferenceVideo) {
        setErrorMessage(`Thời lượng ${config.targetDurationPerSceneSeconds}s không được nhà cung cấp video hỗ trợ. Vui lòng chọn lại thời lượng hợp lệ (${DEFAULT_SUPPORTED_DURATIONS.join("s hoặc ")}s) trước khi bắt đầu.`);
        setIsStarting(false);
        return;
      }

      // Lưu config trước khi start
      await videoWorkflowApi.saveConfig({
        ...saveConfigRequest(),
      });

      const res = await videoWorkflowApi.startWorkflow(projectId);
      setSuccessMessage(res.message);
      setIsDirty(false);
      setRerunRequested(false);
      await loadData();
      onRefreshProject?.();
    } catch (err: unknown) {
      setErrorMessage((err as Error)?.message || "Không thể bắt đầu quy trình tạo video.");
    } finally {
      setIsStarting(false);
    }
  };

  // Pause / Resume
  const handlePause = async () => {
    try {
      const updated = await videoWorkflowApi.pauseWorkflow(projectId);
      setConfig(updated);
    } catch (err: unknown) {
      setErrorMessage((err as Error)?.message || "Không thể tạm dừng.");
    }
  };

  const handleResume = async () => {
    try {
      const updated = await videoWorkflowApi.resumeWorkflow(projectId);
      setConfig(updated);
    } catch (err: unknown) {
      setErrorMessage((err as Error)?.message || "Không thể tiếp tục.");
    }
  };

  // Thử lại đúng một bước lỗi; tải lại config/cảnh để bước chuyển sang Chờ chạy và run quay lại Đang chạy.
  const handleRetryStep = async (step: WorkflowStepState) => {
    if (!projectId || retryingJobId) return;
    setRetryingJobId(step.jobId);
    setErrorMessage(null);
    try {
      await videoWorkflowApi.retryStep(projectId, step);
    } catch (err: unknown) {
      // Bước đã có lần thử mới hơn (bấm lặp): lệnh đã được nhận, chỉ cần tải lại trạng thái.
      if ((err as { code?: string })?.code !== "CONCURRENCY_CONFLICT") {
        setErrorMessage((err as Error)?.message || "Không thể thử lại bước này.");
      }
    }
    try {
      const [cfg, scns] = await Promise.all([videoWorkflowApi.getConfig(projectId), videoWorkflowApi.listScenes(projectId)]);
      setConfig({ ...cfg, goalPrompt: "" });
      setScenes(scns);
      onRefreshProject?.();
    } catch {
      // Lỗi tạm thời: lần làm mới sau sẽ đồng bộ lại.
    } finally {
      setRetryingJobId(null);
    }
  };

  // Open Edit Scene Prompt
  const handleOpenEditScene = (scene: SceneWorkflowDetailDto) => {
    setEditingScene(scene);
    setEditPromptText(scene.motionPromptText || "");
  };

  // Save Scene Prompt Edit (chỉ làm lại video cảnh đó)
  const handleSaveSceneEdit = async () => {
    if (!editingScene) return;
    try {
      setIsUpdatingScene(true);
      await videoWorkflowApi.updateSceneInput({
        projectId,
        sceneId: editingScene.sceneId,
        motionPromptText: editPromptText,
        expectedRevision: editingScene.revision,
      });
      setEditingScene(null);
      await loadData();
    } catch (err: unknown) {
      setErrorMessage((err as Error)?.message || "Không thể cập nhật cảnh.");
    } finally {
      setIsUpdatingScene(false);
    }
  };

  // Determine current active main screen state:
  // - Preparing: Khi chưa có run hoặc đang ở trạng thái Idle/Preparing
  // - Running: Khi runState === "Running" hoặc "Paused"
  // - Completed: Khi toàn bộ cảnh đã có video hoặc runState === "Completed"
  const completedVideoCount = useMemo(() => {
    return scenes.filter((s) => !!s.videoAssetId).length;
  }, [scenes]);
  const videoJobState = scenes.find((scene) => scene.activeJobType === "VideoRender")?.activeJobState;
  const videoJobLabel = videoJobState === "AwaitingUser" ? "Cần xử lý"
    : videoJobState === "Running" ? "Đang tạo video"
    : videoJobState ? "Chờ lượt tạo video" : null;
  const awaitingVideoProvider = scenes.length > 0 && scenes.every(
    (scene) => scene.workflowStatus === SceneWorkflowStatus.ReadyForVideo && !scene.activeJobId,
  );

  // Video cuối của Project (một revision đã chọn) quyết định màn hoàn tất, không phụ thuộc số phân cảnh.
  // Project một cảnh cũ chưa có revision vẫn dùng video của cảnh đó.
  const finalVideoAssetId = config?.finalVideoAssetId ?? (scenes.length === 1 ? scenes[0]?.videoAssetId ?? null : null);
  // Nhiều video: mỗi cảnh là một clip đăng riêng, hoàn tất khi mọi cảnh đã có video (backend không tạo video cuối).
  const multiVideoDone = config?.videoStrategy !== VideoStrategies.SingleVideo
    && scenes.length > 1 && completedVideoCount === scenes.length;
  const hasStaleScene = scenes.some((s) => Boolean(s.isStale));
  const canPublishFinalVideo = (Boolean(finalVideoAssetId) || multiVideoDone) && !hasStaleScene;

  // Mỗi video đăng được là một bài TikTok: video cuối của Project, hoặc từng clip khi nhiều video.
  const tiktokPreviews = useMemo(() => {
    const fallbackCaption = contentItems.find((item) => item.tiktokContent.trim())?.tiktokContent.trim() ?? "";
    if (finalVideoAssetId) {
      return [{ key: finalVideoAssetId, assetId: finalVideoAssetId, title: "Video cuối của Project", caption: fallbackCaption }];
    }
    if (!multiVideoDone) return [];
    return scenes.filter((s) => s.videoAssetId).map((s) => ({
      key: s.sceneId,
      assetId: s.videoAssetId as string,
      title: s.name,
      caption: contentItems.find((item) => item.sceneId === s.sceneId)?.tiktokContent.trim() ?? "",
    }));
  }, [contentItems, finalVideoAssetId, multiVideoDone, scenes]);

  const mainView = useMemo<"preparing" | "running" | "completed">(() => {
    if (!config) return "preparing";
    const runActive = config.runState === WorkflowRunState.Running || config.runState === WorkflowRunState.Paused;
    if (rerunRequested && !runActive) return "preparing";
    if (finalVideoAssetId || multiVideoDone || (scenes.length === 1 && (config.runState === WorkflowRunState.Completed || completedVideoCount === 1))) {
      return "completed";
    }
    // Run dừng ở bước lỗi vẫn ở màn tiến trình để thử lại đúng bước; Failed không có bước nào thì về form như trước.
    if (runActive || (config.runState === WorkflowRunState.Failed && (config.steps?.length ?? 0) > 0)) {
      return "running";
    }
    return "preparing";
  }, [config, scenes.length, completedVideoCount, finalVideoAssetId, multiVideoDone, rerunRequested]);

  if (isLoading && !config) {
    return (
      <Card className="flex flex-col items-center justify-center p-12 text-center mt-4">
        <Loader2 className="h-8 w-8 animate-spin text-primary mb-3" />
        <p className="text-sm text-muted-foreground">Đang tải cấu hình quy trình sản xuất video…</p>
      </Card>
    );
  }

  if (!config) return null;

  return (
    <div className="space-y-6">
      {/* Thông báo lỗi / thành công */}
      {errorMessage && (
        <div className="flex items-center gap-2 p-3 text-sm rounded-md bg-destructive/10 text-destructive border border-destructive/20">
          <AlertCircle className="h-4 w-4 shrink-0" />
          <span className="flex-1">{errorMessage}</span>
          <Button variant="ghost" size="icon" className="h-6 w-6" aria-label="Đóng thông báo lỗi" onClick={() => setErrorMessage(null)}>
            <X className="h-3.5 w-3.5" />
          </Button>
        </div>
      )}

      {successMessage && (
        <div className="flex items-center gap-2 p-3 text-sm rounded-md bg-success/10 text-success border border-success/20">
          <CheckCircle2 className="h-4 w-4 shrink-0" />
          <span className="flex-1">{successMessage}</span>
          <Button variant="ghost" size="icon" className="h-6 w-6" aria-label="Đóng thông báo thành công" onClick={() => setSuccessMessage(null)}>
            <X className="h-3.5 w-3.5" />
          </Button>
        </div>
      )}

      {/* Header điều hướng trạng thái quy trình */}
      <div className="flex items-center justify-between flex-wrap gap-3">
        <div className="flex items-center gap-2">
          <StageStepper current={mainView} />
          {scenes.length > 0 && (
            <span className="text-xs text-muted-foreground">
              {completedVideoCount}/{scenes.length} clip sẵn sàng
            </span>
          )}
        </div>

        <div className="flex items-center gap-2">
          {mainView === "running" && (
            <>
              {config.runState === WorkflowRunState.Running ? (
                <Button variant="outline" size="sm" onClick={handlePause} className="gap-1.5">
                  <Pause className="h-3.5 w-3.5" /> Tạm dừng
                </Button>
              ) : config.runState === WorkflowRunState.Failed ? (
                <Button variant="outline" size="sm" onClick={() => setRerunRequested(true)} className="gap-1.5">
                  <RotateCcw className="h-3.5 w-3.5" /> Sửa cấu hình & Bắt đầu lại
                </Button>
              ) : (
                <Button variant="outline" size="sm" onClick={handleResume} className="gap-1.5">
                  <Play className="h-3.5 w-3.5" /> Tiếp tục
                </Button>
              )}
            </>
          )}

          {mainView === "completed" && (
            <Button
              variant="outline"
              size="sm"
              onClick={() => setRerunRequested(true)}
              className="gap-1.5"
            >
              <RotateCcw className="h-3.5 w-3.5" /> Sửa cấu hình & Chạy lại
            </Button>
          )}

          <Button
            variant="ghost"
            size="sm"
            onClick={loadData}
            title="Làm mới trạng thái"
          >
            <RefreshCw className="h-3.5 w-3.5" />
          </Button>
        </div>
      </div>


      {/* ============================================================== */}
      {/* 1. MÀN HÌNH CHÍNH: CHUẨN BỊ (PREPARING)                         */}
      {/* ============================================================== */}
      {mainView === "preparing" && (
        <div className="space-y-6">
          {config.runState === WorkflowRunState.Failed && (
            <div role="alert" className="space-y-3 rounded-md border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive">
              <p className="flex items-center gap-2 font-medium">
                <AlertCircle className="h-4 w-4 shrink-0" />
                Lượt chạy trước đã dừng vì lỗi. Xem nhật ký bên dưới, chỉnh cấu hình rồi bắt đầu lại.
              </p>
              <ProjectActivityLog projectId={projectId} />
            </div>
          )}
          <Card>
            <CardHeader className="pb-4">
              <CardTitle className="text-lg flex items-center gap-2">
                <Sparkles className="h-5 w-5 text-primary" />
                1. Chọn cách bắt đầu & Dữ liệu hình ảnh ban đầu
              </CardTitle>
              <p className="text-xs text-muted-foreground">
                Cung cấp hình ảnh và yêu cầu một lần ở cấp dự án. Hệ thống sẽ tự động lập các cảnh, chuẩn bị ảnh nền tảng, viết prompt chuyển động và tạo clip video.
              </p>
            </CardHeader>

            <CardContent className="space-y-6">
              {/* Ba chế độ bắt đầu */}
              <div>
                <Label className="text-sm font-medium mb-3 block">Chế độ đầu vào (§2):</Label>
                <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
                  {/* Mode 1: Mẫu + sản phẩm */}
                  <div
                    onClick={() => handleModeChange(WorkflowModes.ModelAndProduct)}
                    className={`p-4 rounded-lg border cursor-pointer transition-[color,background-color,border-color,box-shadow,opacity,transform] ${
                      config.workflowMode === WorkflowModes.ModelAndProduct
                        ? "border-primary bg-primary/5 ring-2 ring-primary/20"
                        : "border-border hover:border-primary/40 bg-card"
                    }`}
                  >
                    <div className="flex items-center gap-2.5 font-semibold text-sm mb-1.5 text-foreground">
                      <Layers className="h-4 w-4 text-primary" />
                      <span>Mẫu + sản phẩm</span>
                    </div>
                    <p className="text-xs text-muted-foreground leading-relaxed">
                      Đưa một ảnh mẫu người và một ảnh sản phẩm. AI tự tạo ảnh mẫu mặc/dùng sản phẩm theo từng cảnh.
                    </p>
                  </div>

                  {/* Mode 2: Ảnh đã sẵn sàng */}
                  <div
                    onClick={() => handleModeChange(WorkflowModes.ReadyImage)}
                    className={`p-4 rounded-lg border cursor-pointer transition-[color,background-color,border-color,box-shadow,opacity,transform] ${
                      config.workflowMode === WorkflowModes.ReadyImage
                        ? "border-primary bg-primary/5 ring-2 ring-primary/20"
                        : "border-border hover:border-primary/40 bg-card"
                    }`}
                  >
                    <div className="flex items-center gap-2.5 font-semibold text-sm mb-1.5 text-foreground">
                      <ImageIcon className="h-4 w-4 text-primary" />
                      <span>Ảnh đã hoàn thiện</span>
                    </div>
                    <p className="text-xs text-muted-foreground leading-relaxed">
                      Ảnh mẫu đã mặc hoặc cầm sẵn sản phẩm. Đi thẳng sang viết prompt video, không cần sinh ảnh.
                    </p>
                  </div>

                  {/* Mode 3: Chỉ sản phẩm */}
                  <div
                    role="button"
                    tabIndex={0}
                    onKeyDown={e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); handleModeChange(WorkflowModes.ReferenceVideo); } }}
                    onClick={() => handleModeChange(WorkflowModes.ReferenceVideo)}
                    className={`p-4 rounded-lg border cursor-pointer transition-[color,background-color,border-color,box-shadow,opacity,transform] ${
                      config.workflowMode === WorkflowModes.ReferenceVideo
                        ? "border-primary bg-primary/5 ring-2 ring-primary/20"
                        : "border-border hover:border-primary/40 bg-card"
                    }`}
                  >
                    <div className="flex items-center gap-2.5 font-semibold text-sm mb-1.5 text-foreground">
                      <Film className="h-4 w-4 text-primary" />
                      <span>Làm theo video mẫu</span>
                    </div>
                    <p className="text-xs text-muted-foreground leading-relaxed">
                      Ảnh mặc sản phẩm + video mẫu. Mô phỏng chuyển động, dùng nhạc gốc theo độ dài kết quả; thời lượng có thể khác mẫu.
                    </p>
                  </div>
                </div>
              </div>

              <Separator />

              {/* Ô gán vai trò hình ảnh theo chế độ */}
              {isReferenceVideo ? <ReferenceVideoInputs key={projectId} config={config} disabled={isStarting}
                onChange={patch => { setConfig(previous => previous?.projectId === projectId ? { ...previous, ...patch } : previous); setIsDirty(true); }} /> : <>
              <div>
                <Label className="text-sm font-medium mb-3 block">Hình ảnh đầu vào:</Label>

                <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-4">
                  {/* Ô Ảnh Mẫu (nếu mode là ModelAndProduct) */}
                  {config.workflowMode === WorkflowModes.ModelAndProduct && (
                    <div className="p-4 rounded-lg border border-border bg-muted/20 flex flex-col items-center justify-center text-center min-h-[180px]">
                      <span className="text-xs font-semibold text-foreground mb-2 flex items-center gap-1.5">
                        <ImageIcon className="h-3.5 w-3.5 text-primary" />
                        Ảnh Mẫu người (Bắt buộc)
                      </span>
                      {config.modelAssetId ? (
                        <div className="space-y-2 w-full">
                          <AssetMediaPreview key={config.modelAssetId} assetId={config.modelAssetId} assetType="image" fileName="Ảnh mẫu người" />
                          <div className="flex items-center justify-center gap-1.5">
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="h-7 text-xs px-2 gap-1"
                              disabled={uploadingTarget === "model"}
                              onClick={() => handleTriggerUpload("model")}
                              title="Tải ảnh mới từ máy tính (tự động lưu vào Media)"
                            >
                              <Upload className="h-3 w-3" />
                              <span>{uploadingTarget === "model" ? "..." : "Tải lên"}</span>
                            </Button>
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="h-7 text-xs px-2 gap-1"
                              onClick={() => setPickerTarget("model")}
                              title="Chọn từ Thư viện media"
                            >
                              <FolderOpen className="h-3 w-3" />
                              <span>Thư viện</span>
                            </Button>
                            <Button
                              type="button"
                              variant="ghost"
                              size="sm"
                              className="h-7 text-xs px-2 text-destructive"
                              onClick={() => handleClearAsset("model")}
                            >
                              Gỡ
                            </Button>
                          </div>
                        </div>
                      ) : (
                        <div className="space-y-2 w-full">
                          <p className="text-xs text-muted-foreground mb-2">Chưa chọn ảnh mẫu</p>
                          <div className="flex flex-col gap-1.5">
                            <Button
                              type="button"
                              variant="default"
                              size="sm"
                              className="w-full gap-1.5 text-xs h-8"
                              disabled={uploadingTarget === "model"}
                              onClick={() => handleTriggerUpload("model")}
                              title="Tải tệp từ máy tính, tự động lưu vào Media"
                            >
                              <Upload className="h-3.5 w-3.5" />
                              <span>{uploadingTarget === "model" ? "Đang tải lên…" : "Tải lên ảnh"}</span>
                            </Button>
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="w-full gap-1.5 text-xs h-8"
                              onClick={() => setPickerTarget("model")}
                            >
                              <FolderOpen className="h-3.5 w-3.5" /> Chọn từ Thư viện
                            </Button>
                          </div>
                        </div>
                      )}
                    </div>
                  )}

                  {/* Ô Ảnh Sản Phẩm (cho ModelAndProduct và ProductOnly) */}
                  {(config.workflowMode === WorkflowModes.ModelAndProduct ||
                    config.workflowMode === WorkflowModes.ProductOnly) && (
                    <div className="p-4 rounded-lg border border-border bg-muted/20 flex flex-col items-center justify-center text-center min-h-[180px]">
                      <span className="text-xs font-semibold text-foreground mb-2 flex items-center gap-1.5">
                        <ImageIcon className="h-3.5 w-3.5 text-primary" />
                        Ảnh Sản Phẩm (Bắt buộc)
                      </span>
                      {config.productAssetId ? (
                        <div className="space-y-2 w-full">
                          <AssetMediaPreview key={config.productAssetId} assetId={config.productAssetId} assetType="image" fileName="Ảnh sản phẩm" />
                          <div className="flex items-center justify-center gap-1.5">
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="h-7 text-xs px-2 gap-1"
                              disabled={uploadingTarget === "product"}
                              onClick={() => handleTriggerUpload("product")}
                              title="Tải ảnh mới từ máy tính (tự động lưu vào Media)"
                            >
                              <Upload className="h-3 w-3" />
                              <span>{uploadingTarget === "product" ? "..." : "Tải lên"}</span>
                            </Button>
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="h-7 text-xs px-2 gap-1"
                              onClick={() => setPickerTarget("product")}
                              title="Chọn từ Thư viện media"
                            >
                              <FolderOpen className="h-3 w-3" />
                              <span>Thư viện</span>
                            </Button>
                            <Button
                              type="button"
                              variant="ghost"
                              size="sm"
                              className="h-7 text-xs px-2 text-destructive"
                              onClick={() => handleClearAsset("product")}
                            >
                              Gỡ
                            </Button>
                          </div>
                        </div>
                      ) : (
                        <div className="space-y-2 w-full">
                          <p className="text-xs text-muted-foreground mb-2">Chưa chọn ảnh sản phẩm</p>
                          <div className="flex flex-col gap-1.5">
                            <Button
                              type="button"
                              variant="default"
                              size="sm"
                              className="w-full gap-1.5 text-xs h-8"
                              disabled={uploadingTarget === "product"}
                              onClick={() => handleTriggerUpload("product")}
                              title="Tải tệp từ máy tính, tự động lưu vào Media"
                            >
                              <Upload className="h-3.5 w-3.5" />
                              <span>{uploadingTarget === "product" ? "Đang tải lên…" : "Tải lên ảnh"}</span>
                            </Button>
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="w-full gap-1.5 text-xs h-8"
                              onClick={() => setPickerTarget("product")}
                            >
                              <FolderOpen className="h-3.5 w-3.5" /> Chọn từ Thư viện
                            </Button>
                          </div>
                        </div>
                      )}
                    </div>
                  )}

                  {/* Ô Ảnh Đã Có Sẵn (cho ReadyImage) */}
                  {config.workflowMode === WorkflowModes.ReadyImage && (
                    <div className="p-4 rounded-lg border border-border bg-muted/20 flex flex-col items-center justify-center text-center min-h-[180px]">
                      <span className="text-xs font-semibold text-foreground mb-2 flex items-center gap-1.5">
                        <ImageIcon className="h-3.5 w-3.5 text-primary" />
                        Ảnh Đã Hoàn Thiện (Bắt buộc)
                      </span>
                      {config.readyImageAssetId ? (
                        <div className="space-y-2 w-full">
                          <AssetMediaPreview key={config.readyImageAssetId} assetId={config.readyImageAssetId} assetType="image" fileName="Ảnh đã hoàn thiện" />
                          <div className="flex items-center justify-center gap-1.5">
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="h-7 text-xs px-2 gap-1"
                              disabled={uploadingTarget === "ready"}
                              onClick={() => handleTriggerUpload("ready")}
                              title="Tải ảnh mới từ máy tính (tự động lưu vào Media)"
                            >
                              <Upload className="h-3 w-3" />
                              <span>{uploadingTarget === "ready" ? "..." : "Tải lên"}</span>
                            </Button>
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="h-7 text-xs px-2 gap-1"
                              onClick={() => setPickerTarget("ready")}
                              title="Chọn từ Thư viện media"
                            >
                              <FolderOpen className="h-3 w-3" />
                              <span>Thư viện</span>
                            </Button>
                            <Button
                              type="button"
                              variant="ghost"
                              size="sm"
                              className="h-7 text-xs px-2 text-destructive"
                              onClick={() => handleClearAsset("ready")}
                            >
                              Gỡ
                            </Button>
                          </div>
                        </div>
                      ) : (
                        <div className="space-y-2 w-full">
                          <p className="text-xs text-muted-foreground mb-2">Chưa chọn ảnh hoàn thiện</p>
                          <div className="flex flex-col gap-1.5">
                            <Button
                              type="button"
                              variant="default"
                              size="sm"
                              className="w-full gap-1.5 text-xs h-8"
                              disabled={uploadingTarget === "ready"}
                              onClick={() => handleTriggerUpload("ready")}
                              title="Tải tệp từ máy tính, tự động lưu vào Media"
                            >
                              <Upload className="h-3.5 w-3.5" />
                              <span>{uploadingTarget === "ready" ? "Đang tải lên…" : "Tải lên ảnh"}</span>
                            </Button>
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="w-full gap-1.5 text-xs h-8"
                              onClick={() => setPickerTarget("ready")}
                            >
                              <FolderOpen className="h-3.5 w-3.5" /> Chọn từ Thư viện
                            </Button>
                          </div>
                        </div>
                      )}
                    </div>
                  )}
                </div>
              </div>

              <Separator />

              {/* Prompt áp dụng: Dự án > mẫu toàn cục */}
              <div className="space-y-4">
                {/* Thông báo mẫu prompt áp dụng */}
                <div className="flex items-center justify-between flex-wrap gap-2 p-2.5 rounded-md bg-muted/40 border border-border text-xs">
                  <div className="flex items-center gap-2">
                    <FileText className="h-4 w-4 text-primary shrink-0" />
                    <span>
                      Mẫu prompt áp dụng:{" "}
                      <strong className="text-foreground">
                        Kịch bản: {scriptPromptSource.label} · Video: {videoPromptSource.label}
                      </strong>
                    </span>
                    <Badge
                      variant={scriptPromptSource.isGlobal && videoPromptSource.isGlobal ? "secondary" : "default"}
                      className="text-[10px] h-5"
                    >
                      {scriptPromptSource.isGlobal && videoPromptSource.isGlobal ? "Toàn cục" : "Prompt tùy chỉnh"}
                    </Badge>
                  </div>
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    className="h-6 text-xs px-2 gap-1 text-primary hover:text-primary"
                    disabled={isLoadingAiPreview}
                    onClick={handleOpenAiPreview}
                    title="Xem chi tiết cách ứng dụng ghép yêu cầu này cùng mẫu Prompt video"
                  >
                    <Eye className="h-3 w-3" />
                    <span>{isLoadingAiPreview ? "Đang tải…" : "Xem yêu cầu gửi AI"}</span>
                  </Button>
                </div>

                {/* Prompt riêng của dự án: để trống thì dùng mẫu toàn cục */}
                <div className="space-y-3 p-3.5 rounded-lg border border-border bg-muted/20">
                  <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                    {GUIDANCE_FIELDS.map(({ field, label }) => {
                      const projectText = config?.[field];
                      return (
                        <div key={field} className="space-y-1.5">
                          <Label htmlFor={`guidance-${field}`} className="text-xs font-medium text-foreground">
                            {label}:
                          </Label>
                          <Textarea
                            id={`guidance-${field}`}
                            rows={3}
                            className="resize-y text-xs bg-background"
                            placeholder="Để trống để dùng mẫu toàn cục."
                            value={projectText ?? ""}
                            disabled={!config}
                            onChange={(e) => handleGuidanceChange(field, e.target.value)}
                          />
                          <p className="text-[11px] text-muted-foreground">
                            Đang áp dụng: {(field === "scriptGuidance" ? scriptPromptSource : videoPromptSource).label}
                          </p>
                        </div>
                      );
                    })}
                  </div>
                </div>

              </div>

              {/* Tùy chọn Audio (§3.3 - Audio không bắt buộc) */}
              <div className="space-y-3">
                <Label className="text-sm font-medium">Âm thanh đi kèm (Không bắt buộc):</Label>
                <div className="flex items-center gap-4 flex-wrap">
                  <label className="flex items-center gap-2 cursor-pointer text-xs">
                    <input
                      type="radio"
                      name="audioOption"
                      checked={config.audioOption === AudioOptions.None}
                      onChange={() => handleAudioOptionChange(AudioOptions.None)}
                      className="accent-primary"
                    />
                    <span>Không gửi audio (Mặc định)</span>
                  </label>

                  <label className="flex items-center gap-2 cursor-pointer text-xs">
                    <input
                      type="radio"
                      name="audioOption"
                      checked={config.audioOption === AudioOptions.CustomAudio}
                      onChange={() => handleAudioOptionChange(AudioOptions.CustomAudio)}
                      className="accent-primary"
                    />
                    <span>Chọn tệp MP3/WAV có sẵn</span>
                  </label>
                </div>

                {config.audioOption === AudioOptions.CustomAudio && (
                  <div className="flex items-center gap-3 p-3 rounded-md border border-border bg-muted/10 w-full sm:w-auto flex-wrap">
                    <Music className="h-4 w-4 text-primary shrink-0" />
                    <span className="text-xs text-muted-foreground truncate max-w-[200px]">
                      {config.audioAssetId
                        ? `Tệp âm thanh: ${config.audioAssetId.slice(0, 16)}…`
                        : "Chưa chọn tệp âm thanh"}
                    </span>
                    <div className="flex items-center gap-1.5">
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        className="h-7 text-xs px-2.5 gap-1"
                        disabled={uploadingTarget === "audio"}
                        onClick={() => handleTriggerUpload("audio")}
                        title="Tải âm thanh từ máy tính (tự động lưu vào Media)"
                      >
                        <Upload className="h-3 w-3" />
                        <span>{uploadingTarget === "audio" ? "Đang tải…" : "Tải lên audio"}</span>
                      </Button>
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        className="h-7 text-xs px-2.5 gap-1"
                        onClick={() => setPickerTarget("audio")}
                        title="Chọn từ Thư viện media"
                      >
                        <FolderOpen className="h-3 w-3" />
                        <span>Thư viện</span>
                      </Button>
                      {config.audioAssetId && (
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          className="h-7 text-xs px-2 text-destructive"
                          onClick={() => handleClearAsset("audio")}
                        >
                          Gỡ
                        </Button>
                      )}
                    </div>
                  </div>
                )}
              </div>

              {/* Chiến lược video: mỗi cảnh một clip, hoặc một video nhiều phân cảnh (VIDEO_TWO_FLOW_SPLIT §3.1). */}
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 max-w-lg pt-2">
                <div className="space-y-1.5">
                  <Label htmlFor="video-strategy" className="text-xs font-medium">
                    Chiến lược video:
                  </Label>
                  <select
                    id="video-strategy"
                    className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-xs shadow-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                    value={config.videoStrategy ?? VideoStrategies.MultiVideo}
                    onChange={(e) => handleStrategyChange(e.target.value as VideoStrategy)}
                  >
                    <option value={VideoStrategies.MultiVideo}>Nhiều video: mỗi cảnh một clip</option>
                    <option value={VideoStrategies.SingleVideo}>Một video nhiều phân cảnh</option>
                  </select>
                  <p className="text-[11px] text-muted-foreground">
                    {(config.videoStrategy ?? VideoStrategies.MultiVideo) === VideoStrategies.SingleVideo
                      ? "AI viết một storyboard theo mốc thời gian và render một video duy nhất."
                      : "Mỗi cảnh có ảnh và prompt riêng, render thành clip riêng; Fraylo không ghép các clip."}
                  </p>
                  {(config.videoStrategy ?? VideoStrategies.MultiVideo) === VideoStrategies.SingleVideo && (
                    <div className="pt-1">
                      <label className="flex items-center gap-2 text-xs font-medium">
                        <input
                          id="grid-mode"
                          type="checkbox"
                          checked={isGridMode}
                          onChange={(e) => handleGridModeChange(e.target.checked)}
                        />
                        Tạo ảnh storyboard 9 ô (thử nghiệm)
                      </label>
                      <p className="text-[11px] text-muted-foreground">
                        {isGridMode
                          ? "AI viết 9 cảnh, tạo một ảnh 3×3 từ ảnh nguồn rồi gửi nguyên ảnh đó để tạo một video theo thời lượng đã chọn. Chưa kiểm chứng với provider thật."
                          : "Tạo một ảnh 9 ô rồi gửi nguyên ảnh để tạo một video theo thời lượng đã chọn."}
                      </p>
                    </div>
                  )}
                </div>

                {(config.videoStrategy ?? VideoStrategies.MultiVideo) === VideoStrategies.MultiVideo && (
                  <div className="space-y-1.5">
                    <Label htmlFor="scene-count" className="text-xs font-medium">
                      Số cảnh:
                    </Label>
                    <input
                      id="scene-count"
                      type="number"
                      min={1}
                      max={10}
                      className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-xs shadow-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                      value={config.targetSceneCount}
                      onChange={(e) => handleSceneCountChange(parseInt(e.target.value) || 1)}
                    />
                    <p className="text-[11px] text-muted-foreground">AI viết đúng số cảnh này, tối đa 10.</p>
                  </div>
                )}

                <div className="space-y-1.5">
                  <Label htmlFor="target-duration" className="text-xs font-medium">
                    Thời lượng mỗi clip:
                  </Label>
                  <select
                    id="target-duration"
                    className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-xs shadow-xs focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
                    value={config.targetDurationPerSceneSeconds}
                    onChange={(e) => handleTargetDurationChange(parseInt(e.target.value) || 15)}
                  >
                    {DEFAULT_SUPPORTED_DURATIONS.map((dur) => (
                      <option key={dur} value={dur}>
                        {dur} giây{dur === 15 ? " (Ưu tiên)" : ""}
                      </option>
                    ))}
                    {!DEFAULT_SUPPORTED_DURATIONS.includes(config.targetDurationPerSceneSeconds as (typeof DEFAULT_SUPPORTED_DURATIONS)[number]) && (
                      <option value={config.targetDurationPerSceneSeconds}>
                        {config.targetDurationPerSceneSeconds} giây (Cần chọn lại thời lượng được hỗ trợ)
                      </option>
                    )}
                  </select>
                  <p className="text-[11px] text-muted-foreground">
                    {isGridMode
                      ? "Ảnh storyboard 9 ô cố định 9 cảnh, chia đều trong thời lượng đã chọn."
                      : (config.videoStrategy ?? VideoStrategies.MultiVideo) === VideoStrategies.SingleVideo
                      ? "AI chia các đoạn của video theo nội dung; mốc thời gian có thể lệch tối đa 2 giây."
                      : "Mỗi cảnh là một clip có thời lượng này. Các clip được xuất riêng."}
                  </p>
                </div>

                {/* Cảnh báo nếu dữ liệu cũ chưa hợp lệ theo capability */}
                {!DEFAULT_SUPPORTED_DURATIONS.includes(config.targetDurationPerSceneSeconds as (typeof DEFAULT_SUPPORTED_DURATIONS)[number]) && (
                  <div className="flex items-center gap-2 p-2.5 rounded text-xs bg-warning/10 text-warning border border-warning/20 col-span-1 sm:col-span-2">
                    <AlertCircle className="h-4 w-4 shrink-0" />
                    <span>
                      Thời lượng {config.targetDurationPerSceneSeconds}s hiện tại chưa được nhà cung cấp hỗ trợ. Vui lòng chọn lại 10s, 15s hoặc 30s trước khi bắt đầu.
                    </span>
                  </div>
                )}

                {/* Tóm tắt trước khi Bắt đầu */}
                <div className="col-span-1 sm:col-span-2 p-3 rounded-lg bg-muted/40 border border-border text-xs text-foreground space-y-1">
                  <div className="font-semibold flex items-center gap-1.5 text-primary">
                    <Sparkles className="h-3.5 w-3.5" />
                    <span>
                      {(config.videoStrategy ?? VideoStrategies.MultiVideo) === VideoStrategies.SingleVideo
                        ? `Tóm tắt: Tạo 1 video, mục tiêu ${config.targetDurationPerSceneSeconds} giây`
                        : `Tóm tắt: Tạo ${config.targetSceneCount} video, mỗi video ${config.targetDurationPerSceneSeconds} giây`}
                    </span>
                  </div>
                  <p className="text-muted-foreground text-[11px]">
                    {(config.videoStrategy ?? VideoStrategies.MultiVideo) === VideoStrategies.SingleVideo
                      ? "Mỗi Project tạo một video đầu ra. Nếu provider chỉ trả các clip rời, Fraylo sẽ không ghép chúng thành video."
                      : "Mỗi cảnh là một video riêng. Fraylo không ghép các clip thành một video."}
                  </p>
                </div>
              </div>
              </>}
              {config.workflowMode === WorkflowModes.ProductOnly && <p role="alert" className="text-sm text-warning">Chế độ Chỉ sản phẩm cũ đã được thay thế. Hãy chọn một trong ba chế độ hiện hành và lưu nháp.</p>}
            </CardContent>

            <CardFooter className="flex items-center justify-between border-t pt-4">
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={handleSaveConfig}
                disabled={!isDirty}
              >
                Lưu cấu hình
              </Button>

              <Button
                type="button"
                size="lg"
                onClick={handleStartWorkflow}
                disabled={isStarting || (isReferenceVideo && (!config.readyImageAssetId || !config.referenceVideoAssetId || !config.referenceVideoAvailable)) || config.workflowMode === WorkflowModes.ProductOnly}
                className="gap-2 bg-primary text-primary-foreground font-semibold px-6 shadow-md hover:shadow-lg transition-[color,background-color,border-color,box-shadow,opacity,transform]"
              >
                {isStarting ? (
                  <>
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Đang khởi tạo quy trình…
                  </>
                ) : (
                  <>
                    <Sparkles className="h-4 w-4" />
                    {isReferenceVideo ? "Tạo video theo mẫu" : "Bắt đầu tạo video"}
                  </>
                )}
              </Button>
            </CardFooter>
          </Card>
        </div>
      )}

      {/* ============================================================== */}
      {/* 2. MÀN HÌNH CHÍNH: ĐANG CHẠY (RUNNING)                          */}
      {/* ============================================================== */}
      {mainView === "running" && (
        <div className="space-y-6">
          <Card>
            <CardHeader className="pb-3">
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-lg flex items-center gap-2">
                    {config.runState === WorkflowRunState.Failed
                      ? <AlertCircle className="h-5 w-5 text-destructive" />
                      : !awaitingVideoProvider && <Loader2 className="h-5 w-5 text-primary animate-spin" />}
                    Tiến độ sản xuất video tự động
                  </CardTitle>
                  <p className="text-xs text-muted-foreground mt-1">
                    {config.runState === WorkflowRunState.Failed
                      ? "Quy trình đã dừng ở bước lỗi. Thử lại đúng bước đó, các bước đã xong được giữ nguyên."
                      : awaitingVideoProvider
                      ? "Ảnh và prompt đã sẵn sàng, đang chờ tác vụ tạo video."
                      : videoJobLabel ? "Tiến độ tạo video được đồng bộ với tác vụ trong Tiến trình."
                        : "Ứng dụng đang chuẩn bị ảnh và prompt chuyển động cho video."}
                  </p>
                </div>
                <Badge variant={config.runState === WorkflowRunState.Failed ? "destructive" : config.runState === WorkflowRunState.Paused ? "warning" : "info"}>
                  {config.runState === WorkflowRunState.Failed ? "Đã dừng vì lỗi"
                    : config.runState === WorkflowRunState.Paused ? "Đang tạm dừng"
                    : videoJobLabel ?? (awaitingVideoProvider ? "Chờ tạo video" : "Đang xử lý")}
                </Badge>
              </div>
            </CardHeader>

            <CardContent className="space-y-4">
              <div className="space-y-1.5">
                <div className="flex justify-between text-xs text-muted-foreground">
                  <span>Tiến độ tổng thể:</span>
                  <span>{completedVideoCount}/{scenes.length} clip hoàn tất</span>
                </div>
                <Progress
                  value={scenes.length > 0 ? (completedVideoCount / scenes.length) * 100 : 0}
                  className="h-2"
                />
              </div>

              {isGridMode ? (
                <StoryboardGridProgress config={config} scene={scenes[0]} retryingJobId={retryingJobId} onRetryStep={handleRetryStep} />
              ) : (
                <WorkflowStepIssues steps={config.steps} scenes={scenes} retryingJobId={retryingJobId} onRetry={handleRetryStep} />
              )}

              <ProjectActivityLog projectId={projectId} />
            </CardContent>
          </Card>
        </div>
      )}

      {/* ============================================================== */}
      {/* 3. MÀN HÌNH CHÍNH: KẾT QUẢ (COMPLETED)                          */}
      {/* ============================================================== */}
      {mainView === "completed" && (
        <div className="space-y-6">
          {isGridMode && (
            <Card>
              <CardHeader className="pb-3">
                <CardTitle className="text-base">Ảnh storyboard 9 ô</CardTitle>
              </CardHeader>
              <CardContent>
                <StoryboardGridProgress config={config} scene={scenes[0]} />
              </CardContent>
            </Card>
          )}
          <Card>
            <CardHeader className="pb-3">
              <CardTitle className="text-base">Chuẩn bị đăng TikTok</CardTitle>
              <p className="text-xs text-muted-foreground">
                Chọn clip để mở form cùng caption đã điền sẵn. Quyền xem theo thiết lập hiện có trên TikTok.
              </p>
            </CardHeader>
            <CardContent className="grid gap-5 lg:grid-cols-[minmax(0,auto)_minmax(16rem,1fr)]">
              <div className="flex gap-5 overflow-x-auto pb-2">
                {tiktokPreviews.map((preview) => (
                  <div key={preview.key} className="w-[200px] shrink-0 space-y-2">
                    <TikTokPostPreview
                      title={preview.title}
                      caption={preview.caption}
                      videoSrc={`https://media.fraylo.local/media/${encodeURIComponent(preview.assetId)}`}
                    />
                    {!finalVideoAssetId && onOpenPublishing && <Button
                      type="button" variant="outline" className="w-full"
                      aria-label={`Đăng clip ${preview.title}`} disabled={!canPublishFinalVideo}
                      onClick={() => onOpenPublishing(projectId, preview.assetId)}
                    >Đăng video</Button>}
                  </div>
                ))}
              </div>

              <div className="flex flex-col gap-4 rounded-lg border border-border bg-muted/40 p-4">
                <div className="space-y-2">
                  <p className="text-sm font-medium text-foreground">
                    {canPublishFinalVideo ? (
                      multiVideoDone && !finalVideoAssetId
                        ? `${scenes.length} clip đã sẵn sàng; mỗi clip đăng riêng.`
                        : "Một video đầu ra đã sẵn sàng."
                    ) : (
                      "Cần một video cuối hoàn chỉnh của Project và không phân cảnh nào bị đánh dấu cần xem lại."
                    )}
                  </p>
                </div>

                <dl className="divide-y divide-border text-xs">
                  <div className="flex items-center justify-between gap-3 py-2.5 first:pt-0">
                    <dt className="text-muted-foreground">Bài đăng</dt>
                    <dd className="font-medium text-foreground">
                      {multiVideoDone && !finalVideoAssetId ? `${tiktokPreviews.length} clip` : "1 video"}
                    </dd>
                  </div>
                  <div className="flex items-center justify-between gap-3 py-2.5 last:pb-0">
                    <dt className="text-muted-foreground">Caption</dt>
                    <dd className="font-medium text-foreground">
                      {tiktokPreviews.filter((preview) => preview.caption).length}/{tiktokPreviews.length} có caption
                    </dd>
                  </div>
                </dl>

                <div className="mt-auto flex flex-col gap-2">
                  {finalVideoAssetId && onOpenPublishing && <Button
                    type="button"
                    onClick={() => onOpenPublishing(projectId, finalVideoAssetId)}
                    disabled={!canPublishFinalVideo}
                    className="w-full gap-2"
                  >
                    <Send className="h-4 w-4" aria-hidden="true" />Đăng video
                  </Button>}
                  <Button
                    type="button"
                    onClick={handlePrepareTikTokExport}
                    variant="outline"
                    disabled={isPreparingTikTokExport || !canPublishFinalVideo}
                    className="w-full gap-2"
                  >
                    {isPreparingTikTokExport ? (
                      <Loader2 className="h-4 w-4 animate-spin" />
                    ) : (
                      <FolderOpen className="h-4 w-4" />
                    )}
                    Xuất để đăng thủ công
                  </Button>
                </div>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader className="pb-3">
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-lg flex items-center gap-2">
                    <CheckCircle2 className="h-5 w-5 text-success" />
                    Video và clip đã tạo ({completedVideoCount})
                  </CardTitle>
                  <p className="text-xs text-muted-foreground mt-1">
                    {multiVideoDone
                      ? "Mỗi clip là một video đăng riêng."
                      : "Các clip rời chưa phải video cuối. Chỉ video đầu ra hoàn chỉnh mới có thể dùng để đăng."}
                  </p>
                </div>
              </div>
            </CardHeader>

            <CardContent>
              <ul className="divide-y divide-border">
                {scenes.map((scene) => (
                  <li key={scene.sceneId} className="flex flex-col gap-4 py-4 first:pt-0 last:pb-0 sm:flex-row sm:items-center">
                    {/* Video Player / Thumbnail Preview */}
                    <div className="relative flex h-24 w-full shrink-0 items-center justify-center overflow-hidden rounded-lg border border-border bg-muted/60 sm:w-44">
                      {scene.videoAssetId ? (
                        <div className="flex flex-col items-center justify-center gap-1.5">
                          <Film className="h-7 w-7 text-primary/70" />
                          <Button
                            type="button"
                            variant="secondary"
                            size="sm"
                            className="h-7 gap-1.5 px-2.5 text-xs shadow-xs"
                            onClick={() => setPreviewVideoScene(scene)}
                          >
                            <Play className="h-3 w-3 fill-current" /> Xem clip
                          </Button>
                        </div>
                      ) : (
                        <div className="p-2 text-center">
                          <AlertCircle className="mx-auto mb-1 h-5 w-5 text-warning" />
                          <p className="text-xs text-muted-foreground">Clip chưa hoàn thành</p>
                        </div>
                      )}

                      <div className="absolute top-2 left-2">
                        <Badge variant="secondary" className="text-[10px] bg-background/80 backdrop-blur-xs">
                          {scene.targetDurationSeconds}s
                        </Badge>
                      </div>

                      {scene.isStale && (
                        <div className="absolute top-2 right-2">
                          <Badge variant="warning" className="text-[10px]">
                            Cần xem lại
                          </Badge>
                        </div>
                      )}
                    </div>

                    <div className="flex min-w-0 flex-1 flex-col gap-2">
                      <h4 className="truncate text-sm font-semibold text-foreground">
                        {scene.name}
                      </h4>
                      <p className="line-clamp-2 text-xs leading-relaxed text-muted-foreground">
                        {scene.motionPromptText || "Không có mô tả prompt"}
                      </p>

                      <div className="mt-auto flex items-center gap-1.5 pt-1">
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          className="h-7 gap-1 px-2 text-xs"
                          onClick={() => handleOpenEditScene(scene)}
                        >
                          <Sliders className="h-3 w-3" /> Sửa yêu cầu
                        </Button>
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          className="h-7 gap-1 px-2 text-xs"
                          onClick={() => {
                            setDetailedScene(scene);
                            setIsDetailsOpen(true);
                          }}
                        >
                          <Eye className="h-3 w-3" /> Chi tiết
                        </Button>
                      </div>
                    </div>
                  </li>
                ))}
              </ul>
            </CardContent>
          </Card>
        </div>
      )}

      {/* Kịch bản & prompt: chi tiết kỹ thuật, đặt sau kết quả để không che bước chính */}
      {/* ============================================================== */}
      <StoryboardPanel projectId={projectId} />
      {/* BẢNG KỊCH BẢN & PROMPT VIDEO AI (DẠNG BẢNG - REVIEW & ACTIONS) */}
      {/* ============================================================== */}
      <Card className="border-primary/30 shadow-xs">
          <CardHeader className="pb-3">
            <div className="flex items-center justify-between">
              <div>
                <CardTitle className="text-base flex items-center gap-2 text-foreground">
                  <FileText className="h-5 w-5 text-primary" />
                  Bảng Kịch bản & Prompt Video AI
                </CardTitle>
                <p className="text-xs text-muted-foreground mt-1">
                  Mỗi cảnh hiển thị ảnh tham chiếu, nội dung và prompt AI trả về, prompt gửi Muse và video khi đã render xong. Bạn có thể bấm "Sửa" hoặc "Tạo video" để đưa vào hàng đợi sản xuất.
                </p>
              </div>
              <Badge variant="outline" className="border-primary/40 text-primary bg-primary/5">
                {contentItems.filter((i) => i.status === "Completed" || !!i.videoAssetId).length}/{contentItems.length} video đã render
              </Badge>
            </div>
          </CardHeader>

          <CardContent className="p-0">
            <div className="overflow-x-auto">
              <Table>
                <TableHeader className="bg-muted/40">
                  <TableRow>
                    <TableHead className="w-[60px] text-center font-semibold">Cảnh</TableHead>
                    <TableHead className="min-w-[160px] text-center font-semibold">Ảnh tham chiếu</TableHead>
                    <TableHead className="min-w-[220px] font-semibold">Nội dung đăng TikTok</TableHead>
                    <TableHead className="min-w-[220px] font-semibold">Prompt ảnh</TableHead>
                    <TableHead className="min-w-[240px] font-semibold">Prompt gửi Muse</TableHead>
                    <TableHead className="min-w-[180px] text-center font-semibold">Video</TableHead>
                    <TableHead className="min-w-[110px] font-semibold text-center">Trạng thái</TableHead>
                    <TableHead className="min-w-[160px] text-right font-semibold pr-4">Thao tác</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {contentItems.map((item, index) => {
                    const isRendering = submittingVideoItemId === item.id || item.status === "Queued" || item.status === "InProgress";
                    const isDone = item.status === "Completed" || !!item.videoAssetId;
                    const isPrimaryOutput = index === 0;
                    return (
                      <TableRow key={item.id} className="hover:bg-muted/20">
                        <TableCell className="text-center font-bold text-foreground">
                          #{item.sequenceOrder}
                        </TableCell>
                        <TableCell className="text-center align-top">
                          {item.imageAssetId ? (
                            <AssetMediaPreview assetId={item.imageAssetId} assetType="Image" fileName={`Ảnh cảnh ${item.sequenceOrder}`} />
                          ) : (
                            <span className="text-[11px] text-muted-foreground">Chưa có ảnh</span>
                          )}
                        </TableCell>
                        <TableCell className="text-xs align-top">
                          <PromptCell
                            text={item.tiktokContent}
                            emptyText="Chưa có nội dung đăng TikTok"
                            title={`Nội dung đăng TikTok cảnh ${item.sequenceOrder}`}
                          />
                        </TableCell>
                        <TableCell className="text-xs align-top">
                          <PromptCell text={item.imagePrompt} emptyText="Chưa có prompt ảnh" title={`Prompt ảnh cảnh ${item.sequenceOrder}`} />
                        </TableCell>
                        <TableCell className="text-xs align-top">
                          <PromptCell text={item.videoPrompt} emptyText="Chưa có prompt video" title={`Prompt gửi Muse cảnh ${item.sequenceOrder}`} />
                        </TableCell>
                        <TableCell className="text-center align-top">
                          {item.videoAssetId ? (
                            <AssetMediaPreview assetId={item.videoAssetId} assetType="Video" fileName={`Video cảnh ${item.sequenceOrder}`} />
                          ) : (
                            <span className="text-[11px] text-muted-foreground">
                              {isRendering ? "Đang render video" : "Chưa có video"}
                            </span>
                          )}
                        </TableCell>
                        <TableCell className="text-center align-top">
                          <Badge
                            variant={isDone ? "success" : isRendering ? "info" : "secondary"}
                            className="text-[10px] px-2 py-0.5"
                          >
                            {isDone ? "Hoàn tất" : isRendering ? "Đang xử lý" : item.status === "Failed" ? "Lỗi" : item.status === "ReadyForVideo" ? "Chờ video" : "Bản nháp"}
                          </Badge>
                        </TableCell>
                        <TableCell className="text-right align-top pr-4">
                          <div className="flex items-center justify-end gap-1.5 flex-wrap">
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="h-7 text-xs px-2 gap-1"
                              onClick={() => handleOpenEditContent(item)}
                              title="Chỉnh sửa nội dung & prompt"
                            >
                              <Edit3 className="h-3 w-3" /> Sửa
                            </Button>

                            <Button
                              type="button"
                              size="sm"
                              className="h-7 text-xs px-2.5 gap-1 bg-primary text-primary-foreground font-medium"
                              disabled={isRendering || !isPrimaryOutput}
                              onClick={() => handleCreateSceneVideo(item)}
                              title={isPrimaryOutput ? "Tạo hoặc tạo lại video duy nhất của dự án" : "Một dự án chỉ tạo một video đầu ra"}
                            >
                              {isRendering ? (
                                <Loader2 className="h-3 w-3 animate-spin" />
                              ) : (
                                <Video className="h-3 w-3" />
                              )}
                              <span>{!isPrimaryOutput ? "Video đã chọn" : isDone ? "Tạo lại" : "Tạo video"}</span>
                            </Button>
                          </div>
                        </TableCell>
                      </TableRow>
                    );
                  })}
                  {contentItems.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={8} className="text-center text-sm text-muted-foreground py-8">
                        Chưa có cảnh nào. Bảng sẽ hiện khi prompt video được lưu.
                      </TableCell>
                    </TableRow>
                  )}
                </TableBody>
              </Table>
            </div>
          </CardContent>
        </Card>

      {/* ============================================================== */}
      {/* DIALOG CHỈNH SỬA YÊU CẦU CẢNH (CHỈ TẠO LẠI PHẦN PHỤ THUỘC §3.4) */}
      {/* ============================================================== */}
      <Dialog open={!!editingScene} onOpenChange={(open) => !open && setEditingScene(null)}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Sửa yêu cầu cảnh: {editingScene?.name}</DialogTitle>
            <DialogDescription className="text-xs">
              Theo ma trận phụ thuộc §3.4: Sửa prompt chuyển động chỉ làm lại video của cảnh này; ảnh gốc được giữ nguyên.
            </DialogDescription>
          </DialogHeader>

          <div className="space-y-4 py-2">
            <div className="space-y-1.5">
              <Label className="text-xs">Prompt chuyển động tiếng Anh (Motion Prompt):</Label>
              <Textarea
                value={editPromptText}
                onChange={(e) => setEditPromptText(e.target.value)}
                rows={4}
                className="resize-none text-xs"
              />
            </div>
          </div>

          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setEditingScene(null)}
            >
              Hủy
            </Button>
            <Button
              type="button"
              size="sm"
              onClick={handleSaveSceneEdit}
              disabled={isUpdatingScene}
              className="gap-1.5"
            >
              {isUpdatingScene ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <CheckCircle2 className="h-3.5 w-3.5" />}
              Lưu & Đánh dấu tạo lại
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* ============================================================== */}
      {/* DIALOG XEM THỬ VIDEO CLIP                                      */}
      {/* ============================================================== */}
      <Dialog open={!!previewVideoScene} onOpenChange={(open) => !open && setPreviewVideoScene(null)}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>{previewVideoScene?.name}</DialogTitle>
            <DialogDescription className="text-xs">
              Thời lượng: {previewVideoScene?.targetDurationSeconds} giây • Tệp Asset ID: {previewVideoScene?.videoAssetId}
            </DialogDescription>
          </DialogHeader>

          <div className="h-64 bg-black/90 rounded-md flex items-center justify-center my-2 text-white">
            <div className="text-center space-y-2">
              <Film className="h-12 w-12 text-primary mx-auto animate-pulse" />
              <p className="text-sm text-white">Chưa hỗ trợ phát trực tiếp trong ứng dụng.</p>
              <p className="text-xs text-white/80">Mở thư mục chứa để xem video bằng trình phát trên máy.</p>
            </div>
          </div>

          <DialogFooter className="flex items-center justify-between sm:justify-between">
            <span className="text-xs text-muted-foreground truncate max-w-[260px]">
              Prompt: {previewVideoScene?.motionPromptText}
            </span>
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={async () => {
                if (!previewVideoScene?.videoAssetId) return;
                try {
                  const result = await openAssetFolder({ assetId: previewVideoScene.videoAssetId });
                  if (!result.success) throw new Error("Không thể mở thư mục chứa video.");
                } catch (error) {
                  setErrorMessage(error instanceof Error ? error.message : "Không thể mở thư mục chứa video.");
                  setPreviewVideoScene(null);
                }
              }}
            >
              Mở thư mục chứa
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setPreviewVideoScene(null)}
            >
              Đóng
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* ============================================================== */}
      {/* DIALOG CHI TIẾT KỸ THUẬT 4 BƯỚC (§4)                           */}
      {/* ============================================================== */}
      <Dialog open={isDetailsOpen} onOpenChange={setIsDetailsOpen}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>Chi tiết kỹ thuật 4 bước: {detailedScene?.name}</DialogTitle>
            <DialogDescription className="text-xs">
              Đầu vào → Ảnh dùng cho video → Prompt chuyển động → Video
            </DialogDescription>
          </DialogHeader>

          {detailedScene && (
            <div className="space-y-3 py-2 text-xs">
              <div className="p-2.5 rounded border bg-muted/20 space-y-1">
                <span className="font-semibold text-foreground">Bước 1: Đầu vào</span>
                <p className="text-muted-foreground">Chiến lược: {detailedScene.imageStrategy}</p>
                <p className="text-muted-foreground">Audio: {detailedScene.audioOption}</p>
              </div>

              <div className="p-2.5 rounded border bg-muted/20 space-y-1">
                <span className="font-semibold text-foreground">Bước 2: Ảnh dùng cho video</span>
                <p className="text-muted-foreground truncate">
                  Selected Asset ID: {detailedScene.selectedImageAssetId || "Chưa có"}
                </p>
              </div>

              <div className="p-2.5 rounded border bg-muted/20 space-y-1">
                <span className="font-semibold text-foreground">Bước 3: Prompt chuyển động</span>
                <p className="text-muted-foreground leading-relaxed">
                  {detailedScene.motionPromptText || "Chưa có"}
                </p>
              </div>

              <div className="p-2.5 rounded border bg-muted/20 space-y-1">
                <span className="font-semibold text-foreground">Bước 4: Clip Video</span>
                <p className="text-muted-foreground truncate">
                  Video Asset ID: {detailedScene.videoAssetId || "Chưa hoàn tất"}
                </p>
                <p className="text-muted-foreground">Trạng thái: {detailedScene.workflowStatus}</p>
              </div>
            </div>
          )}

          <DialogFooter>
            <Button variant="outline" size="sm" onClick={() => setIsDetailsOpen(false)}>
              Đóng
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* ============================================================== */}
      {/* DIALOG XEM TRƯỚC YÊU CẦU GỬI AI WRITER (§3.1-3.3)             */}
      {/* ============================================================== */}
      <Dialog open={isAiPreviewOpen} onOpenChange={setIsAiPreviewOpen}>
        <DialogContent className="max-w-2xl max-h-[85vh] flex flex-col">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2 text-base">
              <Eye className="h-4 w-4 text-primary" />
              Xem yêu cầu gửi AI viết prompt chuyển động
            </DialogTitle>
            <DialogDescription className="text-xs">
              Ứng dụng tự động tổng hợp Mẫu Prompt Video đã điền thành văn bản gửi AI writer theo hợp đồng chuẩn.
            </DialogDescription>
          </DialogHeader>

          {aiPreviewData && (
            <div className="space-y-4 py-2 overflow-y-auto flex-1 text-xs pr-1">
              {/* Thông tin mẫu template */}
              <div className="p-3 rounded-lg border bg-muted/20 space-y-1.5">
                <div className="flex items-center justify-between">
                  <span className="font-semibold text-foreground">Mẫu Prompt áp dụng:</span>
                  <Badge variant="secondary" className="text-[10px]">Prompt toàn ứng dụng</Badge>
                </div>
                <div className="text-muted-foreground flex gap-4">
                  <span>Tên: <strong className="text-foreground">{aiPreviewData.templateName}</strong></span>
                </div>
              </div>

              {/* Toàn bộ prompt đã compose gửi AI writer */}
              <div className="space-y-1.5">
                <span className="font-semibold text-foreground">Toàn bộ văn bản gửi AI writer (Đã compose):</span>
                <pre className="p-3 rounded-lg border bg-card text-[11px] font-mono whitespace-pre-wrap max-h-64 overflow-y-auto leading-relaxed border-border">
                  {aiPreviewData.composedPrompt}
                </pre>
              </div>
            </div>
          )}

          <DialogFooter className="border-t pt-3 flex items-center justify-between sm:justify-between">
            <span className="text-[11px] text-muted-foreground">
              MuseAI chỉ nhận prompt chuyển động kết quả, không nhận toàn bộ template.
            </span>
            <Button variant="outline" size="sm" onClick={() => setIsAiPreviewOpen(false)}>
              Đóng
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>


      {/* ============================================================== */}
      {/* DIALOG CHỈNH SỬA KỊCH BẢN & PROMPT VIDEO AI (5 TRƯỜNG SCHEMA)    */}
      {/* ============================================================== */}
      <Dialog
        open={!!editingContentItem}
        onOpenChange={(open) => !open && setEditingContentItem(null)}
      >
        <DialogContent className="max-w-2xl max-h-[90vh] flex flex-col">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <Edit3 className="h-4 w-4 text-primary" />
              Chỉnh sửa kịch bản & Prompt — Cảnh #{editingContentItem?.sequenceOrder}
            </DialogTitle>
            <DialogDescription className="text-xs">
              Điều chỉnh các trường nội dung AI đã sinh. Khi lưu, các trường này sẽ được cập nhật trong CSDL và dùng để đưa vào queue tạo video.
            </DialogDescription>
          </DialogHeader>

          <div className="space-y-3 py-2 overflow-y-auto flex-1 pr-1 text-xs">
            <div className="space-y-1">
              <Label className="font-semibold text-foreground">1. Ý tưởng video (Idea):</Label>
              <Input
                value={editIdea}
                onChange={(e) => setEditIdea(e.target.value)}
                placeholder="Ý tưởng chủ đạo của cảnh..."
                className="text-xs"
              />
            </div>

            <div className="space-y-1">
              <Label className="font-semibold text-foreground">2. Hook (3 giây đầu thu hút):</Label>
              <Input
                value={editHook}
                onChange={(e) => setEditHook(e.target.value)}
                placeholder="Câu hook gây tò mò, kích thích người xem dừng lại..."
                className="text-xs font-medium"
              />
            </div>

            <div className="space-y-1">
              <Label className="font-semibold text-foreground">3. Kịch bản chi tiết (Video Script):</Label>
              <Textarea
                value={editVideoScript}
                onChange={(e) => setEditVideoScript(e.target.value)}
                rows={3}
                placeholder="Lời bình, diễn biến, hành động của người mẫu trong cảnh..."
                className="resize-none text-xs"
              />
            </div>

            <div className="space-y-1">
              <Label className="font-semibold text-foreground">
                4. Prompt tạo video AI (Video Prompt cho MuseAI / Sora):
              </Label>
              <Textarea
                value={editVideoPrompt}
                onChange={(e) => setEditVideoPrompt(e.target.value)}
                rows={4}
                placeholder="Prompt tiếng Anh mô tả chuyển động camera, ánh sáng, góc quay..."
                className="resize-none text-xs font-mono bg-muted/30"
              />
            </div>

            <div className="space-y-1">
              <Label className="font-semibold text-foreground">5. Nội dung đăng TikTok (TikTok Content / Caption & Hashtags):</Label>
              <Textarea
                value={editTikTokContent}
                onChange={(e) => setEditTikTokContent(e.target.value)}
                rows={2}
                placeholder="Caption kèm hashtag TikTok..."
                className="resize-none text-xs"
              />
            </div>
          </div>

          <DialogFooter className="border-t pt-3 flex items-center justify-between sm:justify-between">
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setEditingContentItem(null)}
            >
              Hủy
            </Button>
            <Button
              type="button"
              size="sm"
              onClick={handleSaveContentEdit}
              disabled={isUpdatingContent}
              className="gap-1.5"
            >
              {isUpdatingContent ? (
                <Loader2 className="h-3.5 w-3.5 animate-spin" />
              ) : (
                <CheckCircle2 className="h-3.5 w-3.5" />
              )}
              Lưu thay đổi kịch bản
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Asset Picker Modal */}
      {pickerTarget && (
        <AssetPicker
          projectId={projectId}
          assetType={pickerTarget === "audio" ? "Audio" : "Image"}
          onClose={() => setPickerTarget(null)}
          onSelect={(asset) => handleSelectAsset(asset)}
        />
      )}
    </div>
  );
}
