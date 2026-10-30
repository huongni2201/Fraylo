import { bridgeClient } from "../../services/bridgeClient";

export const WorkflowModes = {
  ModelAndProduct: "ModelAndProduct",
  ReadyImage: "ReadyImage",
  ProductOnly: "ProductOnly",
} as const;

export type WorkflowMode = (typeof WorkflowModes)[keyof typeof WorkflowModes];

/** Chiến lược video của run: mỗi cảnh một clip, hoặc một video nhiều phân cảnh (VIDEO_TWO_FLOW_SPLIT §3.1). */
export const VideoStrategies = {
  MultiVideo: "MultiVideo",
  SingleVideo: "SingleVideo",
} as const;

export type VideoStrategy = (typeof VideoStrategies)[keyof typeof VideoStrategies];

export const AudioOptions = {
  None: "None",
  CustomAudio: "CustomAudio",
} as const;

export type AudioOption = (typeof AudioOptions)[keyof typeof AudioOptions];

export const WorkflowRunState = {
  Idle: "Idle",
  Preparing: "Preparing",
  Running: "Running",
  Paused: "Paused",
  Completed: "Completed",
  Failed: "Failed",
} as const;

export type WorkflowRunState = (typeof WorkflowRunState)[keyof typeof WorkflowRunState];

export const SceneWorkflowStatus = {
  MissingInputs: "MissingInputs",
  PreparingImage: "PreparingImage",
  PreparingPrompt: "PreparingPrompt",
  ReadyForVideo: "ReadyForVideo",
  VideoCompleted: "VideoCompleted",
  NeedsReview: "NeedsReview",
} as const;

export type SceneWorkflowStatus = (typeof SceneWorkflowStatus)[keyof typeof SceneWorkflowStatus];

export interface ProjectWorkflowConfigDto {
  projectId: string;
  workflowMode: WorkflowMode;
  goalPrompt: string;
  modelAssetId: string | null;
  productAssetId: string | null;
  readyImageAssetId: string | null;
  foundationImageAssetId: string | null;
  audioOption: AudioOption;
  audioAssetId: string | null;
  targetSceneCount: number;
  targetDurationPerSceneSeconds: number;
  runState: WorkflowRunState;
  currentRunId: string | null;
  updatedAtUtc: string;
  scriptGuidance?: string | null;
  videoGuidance?: string | null;
  finalVideoAssetId?: string | null;
  groupId?: string | null;
  groupName?: string | null;
  /** Ô tệp đang lấy từ mặc định của nhóm: Model, Product, ReadyImage, Audio. */
  inheritedAssetSlots?: string[] | null;
  videoStrategy?: VideoStrategy | null;
}

export interface SaveProjectWorkflowConfigRequest {
  projectId: string;
  workflowMode: WorkflowMode;
  goalPrompt: string;
  modelAssetId?: string | null;
  productAssetId?: string | null;
  readyImageAssetId?: string | null;
  audioOption: AudioOption;
  audioAssetId?: string | null;
  targetSceneCount: number;
  targetDurationPerSceneSeconds: number;
  scriptGuidance?: string | null;
  videoGuidance?: string | null;
  videoStrategy?: VideoStrategy;
}

export interface SceneWorkflowDetailDto {
  sceneId: string;
  projectId: string;
  sequenceOrder: number;
  name: string;
  targetDurationSeconds: number;
  workflowStatus: SceneWorkflowStatus;
  imageStrategy: string;
  sourceImageAssetId: string | null;
  selectedImageAssetId: string | null;
  motionPromptText: string | null;
  audioOption: AudioOption;
  audioAssetId: string | null;
  videoAssetId: string | null;
  isStale: boolean;
  staleReason: string | null;
  activeJobId: string | null;
  activeJobType: string | null;
  activeJobState: string | null;
  activeJobProgressPercent: number | null;
  videoCandidateHistory?: string[] | null;
  revision: string;
}

export interface StartProjectWorkflowResponseData {
  runId: string;
  runState: string;
  createdSceneCount: number;
  message: string;
}

export interface UpdateSceneWorkflowInputRequest {
  projectId: string;
  sceneId: string;
  imageStrategy?: string;
  selectedImageAssetId?: string | null;
  motionPromptText?: string | null;
  audioOption?: AudioOption;
  audioAssetId?: string | null;
  targetDurationSeconds?: number;
  expectedRevision?: string;
}

export interface PreviewAiPromptRequest {
  projectId: string;
  sceneId?: string | null;
}

export interface PreviewAiPromptResponseData {
  templateId: string;
  templateName: string;
  composedPrompt: string;
  outputContractVersion: number;
}

export interface VideoContentItemDto {
  id: string;
  projectId: string;
  sceneId?: string | null;
  sequenceOrder: number;
  idea: string;
  hook: string;
  videoScript: string;
  videoPrompt: string;
  tiktokContent: string;
  imageAssetId?: string | null;
  videoAssetId?: string | null;
  audioAssetId?: string | null;
  status: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  contentText?: string | null;
  imagePrompt?: string | null;
}

export interface StoryboardContent {
  durationSeconds: number;
  shots: { startSeconds: number; endSeconds: number; description: string; imagePrompt: string; imageAssetId?: string | null }[];
}

export interface UpdateVideoContentItemRequest {
  projectId: string;
  itemId: string;
  idea?: string;
  hook?: string;
  videoScript?: string;
  videoPrompt?: string;
  tiktokContent?: string;
}

export interface CreateSceneVideoRequest {
  projectId: string;
  itemId: string;
}

// Backend (VideoContentItemDto C#) giữ tên snake_case của schema AI cho kịch bản/prompt/caption.
type WireVideoContentItem = Omit<VideoContentItemDto, "videoScript" | "videoPrompt" | "tiktokContent"> & {
  video_script?: string | null;
  video_prompt?: string | null;
  tiktok_content?: string | null;
};

function fromWireContentItem({ video_script, video_prompt, tiktok_content, ...item }: WireVideoContentItem): VideoContentItemDto {
  return { ...item, videoScript: video_script ?? "", videoPrompt: video_prompt ?? "", tiktokContent: tiktok_content ?? "" };
}

export const DEFAULT_SUPPORTED_DURATIONS = [10, 15] as const;

export const videoWorkflowApi = {
  async getStoryboard(projectId: string): Promise<StoryboardContent | null> {
    const data = await bridgeClient.invoke<{ storyboard: StoryboardContent | null }>(
      "workflow.get_storyboard", { projectId },
    );
    return data?.storyboard ?? null;
  },
  async getConfig(projectId: string): Promise<ProjectWorkflowConfigDto> {
    const data = await bridgeClient.invoke<ProjectWorkflowConfigDto>(
      "workflow.get_config",
      { projectId },
    );
    return data!;
  },

  async saveConfig(req: SaveProjectWorkflowConfigRequest): Promise<ProjectWorkflowConfigDto> {
    const data = await bridgeClient.invoke<ProjectWorkflowConfigDto>(
      "workflow.save_config",
      req,
    );
    return data!;
  },

  async startWorkflow(projectId: string): Promise<StartProjectWorkflowResponseData> {
    const data = await bridgeClient.invoke<StartProjectWorkflowResponseData>(
      "workflow.start",
      { projectId, clientRequestId: crypto.randomUUID() },
    );
    return data!;
  },

  async listScenes(projectId: string): Promise<SceneWorkflowDetailDto[]> {
    const res = await bridgeClient.invoke<{ scenes: SceneWorkflowDetailDto[] }>(
      "workflow.list_scenes",
      { projectId },
    );
    return res?.scenes || [];
  },

  async updateSceneInput(req: UpdateSceneWorkflowInputRequest): Promise<SceneWorkflowDetailDto> {
    const data = await bridgeClient.invoke<SceneWorkflowDetailDto>(
      "workflow.update_scene_input",
      req,
    );
    return data!;
  },

  async pauseWorkflow(projectId: string): Promise<ProjectWorkflowConfigDto> {
    const data = await bridgeClient.invoke<ProjectWorkflowConfigDto>(
      "workflow.pause",
      { projectId },
    );
    return data!;
  },

  async resumeWorkflow(projectId: string): Promise<ProjectWorkflowConfigDto> {
    const data = await bridgeClient.invoke<ProjectWorkflowConfigDto>(
      "workflow.resume",
      { projectId },
    );
    return data!;
  },

  async previewAiPrompt(req: PreviewAiPromptRequest): Promise<PreviewAiPromptResponseData> {
    const data = await bridgeClient.invoke<PreviewAiPromptResponseData>(
      "workflow.preview_ai_prompt",
      req,
    );
    return data!;
  },

  async listContentItems(projectId: string): Promise<VideoContentItemDto[]> {
    const items = await bridgeClient.invoke<WireVideoContentItem[]>("workflow.list_content_items", { projectId });
    return (items ?? []).map(fromWireContentItem);
  },

  async updateContentItem(req: UpdateVideoContentItemRequest): Promise<VideoContentItemDto> {
    const data = await bridgeClient.invoke<WireVideoContentItem>("workflow.update_content_item", {
      projectId: req.projectId,
      id: req.itemId,
      idea: req.idea,
      hook: req.hook,
      video_script: req.videoScript,
      video_prompt: req.videoPrompt,
      tiktok_content: req.tiktokContent,
    });
    return fromWireContentItem(data!);
  },

  async createSceneVideo(req: CreateSceneVideoRequest): Promise<unknown> {
    return bridgeClient.invoke<unknown>("workflow.create_scene_video", {
      projectId: req.projectId,
      contentItemId: req.itemId,
    });
  },
};
