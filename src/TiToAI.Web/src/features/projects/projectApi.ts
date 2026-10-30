import { bridgeClient } from "../../services/bridgeClient";

export interface ProjectSummaryDto {
  projectId: string;
  name: string;
  description: string | null;
  status: string;
  sceneCount: number;
  hasVideo: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
  groupId?: string | null;
  groupName?: string | null;
  /** Video của cảnh đầu tiên đã có video, dùng làm ảnh xem trước 9:16. */
  previewVideoAssetId?: string | null;
}

export const STATUS_LABEL: Record<string, string> = {
  Draft: "Nháp",
  InProgress: "Đang sản xuất",
  Completed: "Hoàn thành",
};

export interface ProjectDetailDto {
  projectId: string;
  name: string;
  description: string | null;
  status: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  groupId?: string | null;
  groupName?: string | null;
}

export interface ProjectListResponseData {
  projects: ProjectSummaryDto[];
}

export interface ProjectGetResponseData {
  project: ProjectDetailDto;
}

export interface ProjectCreateRequest {
  name: string;
  description?: string | null;
  groupId?: string | null;
}

export interface ProjectCreateResponseData {
  projectId: string;
}

/** groupId: undefined giữ nguyên nhóm, chuỗi rỗng bỏ khỏi nhóm. */
export interface ProjectUpdateRequest {
  projectId: string;
  name?: string | null;
  description?: string | null;
  status?: string | null;
  groupId?: string | null;
}

/** Nhóm dự án: hướng dẫn prompt và biến dùng chung cho các Dự án trong nhóm. */
export interface ProjectGroupDto {
  groupId: string;
  name: string;
  description: string;
  scriptGuidance: string;
  videoGuidance: string;
  /** Tệp dùng chung của nhóm; dự án để trống ô sẽ dùng tệp này. */
  modelAssetId?: string | null;
  productAssetId?: string | null;
  readyImageAssetId?: string | null;
  audioAssetId?: string | null;
  projectCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface ProjectGroupSaveRequest {
  groupId?: string | null;
  name: string;
  description?: string | null;
  scriptGuidance?: string | null;
  videoGuidance?: string | null;
  modelAssetId?: string | null;
  productAssetId?: string | null;
  readyImageAssetId?: string | null;
  audioAssetId?: string | null;
}

export interface ProjectUpdateResponseData {
  project: ProjectDetailDto;
}

export interface ProjectDeleteRequest {
  projectId: string;
}

export interface ProjectDeleteResponseData {
  projectId: string;
  success: boolean;
}

export interface SceneDto {
  sceneId: string;
  projectId: string;
  name: string;
  sceneSequence: number;
  targetDurationSeconds: number;
  promptText: string | null;
  status: string;
  audioAssetId: string | null;
  imageAssetId: string | null;
  videoAssetId: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  revision: string;
}

export interface SceneListResponseData {
  scenes: SceneDto[];
}

export interface SceneGetResponseData {
  scene: SceneDto;
}

export interface SceneCreateRequest {
  projectId: string;
  name?: string | null;
  promptText?: string | null;
  targetDurationSeconds?: number | null;
  audioAssetId?: string | null;
  imageAssetId?: string | null;
  videoAssetId?: string | null;
}

export interface SceneCreateResponseData {
  sceneId: string;
  sceneSequence: number;
}

export interface SceneUpdateRequest {
  projectId: string;
  sceneId: string;
  expectedRevision?: string | null;
  name?: string | null;
  promptText?: string | null;
  targetDurationSeconds?: number | null;
  status?: string | null;
  audioAssetId?: string | null;
  imageAssetId?: string | null;
  videoAssetId?: string | null;
}

export interface SceneUpdateResponseData {
  scene: SceneDto;
}

export interface SceneDeleteRequest {
  projectId: string;
  sceneId: string;
}

export interface SceneDeleteResponseData {
  sceneId: string;
  success: boolean;
}

export interface SceneReorderRequest {
  projectId: string;
  sceneIds: string[];
  expectedProjectRevision?: string | null;
  expectedSceneRevisions?: Record<string, string> | null;
}

export interface SceneReorderResponseData {
  reordered: boolean;
  scenes: SceneDto[];
}

/**
 * Typed Bridge Client cho nhóm dự án (projectgroup.*).
 */
export const projectGroupApi = {
  /** Liệt kê nhóm kèm số Dự án trong mỗi nhóm. */
  async listGroups(): Promise<ProjectGroupDto[]> {
    const data = await bridgeClient.invoke<{ groups: ProjectGroupDto[] }>(
      "projectgroup.list",
      {},
    );
    return data?.groups ?? [];
  },

  /** Tạo mới (không có groupId) hoặc cập nhật nhóm. */
  async saveGroup(payload: ProjectGroupSaveRequest): Promise<ProjectGroupDto> {
    const data = await bridgeClient.invoke<{ group: ProjectGroupDto }>(
      "projectgroup.save",
      payload,
    );
    return data.group;
  },

  /** Xóa nhóm; các Dự án trong nhóm được giữ lại và chuyển về không nhóm. */
  async deleteGroup(groupId: string): Promise<boolean> {
    const data = await bridgeClient.invoke<{ groupId: string; success: boolean }>(
      "projectgroup.delete",
      { groupId },
    );
    return data?.success ?? true;
  },
};

/**
 * Typed Bridge Client cho các thao tác Quản lý Dự án (Project) và Cảnh (Scene) theo contracts v1.0.4.
 */
export const projectApi = {
  /** Lists every project for the project-first shell. */
  async listAll(): Promise<ProjectSummaryDto[]> {
    const data = await bridgeClient.invoke<ProjectListResponseData>(
      "project.list",
      {},
    );
    return data?.projects ?? [];
  },

  /**
   * Lấy chi tiết một Dự án
   */
  async getProject(projectId: string): Promise<ProjectDetailDto> {
    const data = await bridgeClient.invoke<ProjectGetResponseData>(
      "project.get",
      { projectId },
    );
    return data.project;
  },

  /**
   * Tạo mới Dự án
   */
  async createProject(payload: ProjectCreateRequest): Promise<string> {
    const data = await bridgeClient.invoke<ProjectCreateResponseData>(
      "project.create",
      payload,
    );
    return data.projectId;
  },

  /**
   * Cập nhật thông tin Dự án
   */
  async updateProject(payload: ProjectUpdateRequest): Promise<ProjectDetailDto> {
    const data = await bridgeClient.invoke<ProjectUpdateResponseData>(
      "project.update",
      payload,
    );
    return data.project;
  },

  /**
   * Xóa một Dự án (kèm kiểm tra an toàn active jobs)
   */
  async deleteProject(projectId: string): Promise<boolean> {
    const data = await bridgeClient.invoke<ProjectDeleteResponseData>(
      "project.delete",
      { projectId },
    );
    return data?.success ?? true;
  },

  /**
   * Lấy danh sách toàn bộ Cảnh trong Dự án (được sắp xếp theo sceneSequence)
   */
  async listScenes(projectId: string): Promise<SceneDto[]> {
    const data = await bridgeClient.invoke<SceneListResponseData>(
      "scene.list_by_project",
      { projectId },
    );
    return data?.scenes ?? [];
  },

  /**
   * Lấy thông tin chi tiết một Cảnh
   */
  async getScene(projectId: string, sceneId: string): Promise<SceneDto> {
    const data = await bridgeClient.invoke<SceneGetResponseData>(
      "scene.get",
      { projectId, sceneId },
    );
    return data.scene;
  },

  /**
   * Tạo mới một Cảnh trong Dự án
   */
  async createScene(payload: SceneCreateRequest): Promise<SceneCreateResponseData> {
    return await bridgeClient.invoke<SceneCreateResponseData>(
      "scene.create",
      payload,
    );
  },

  /**
   * Cập nhật thông tin Cảnh với kiểm soát xung đột phiên (Optimistic Concurrency Control).
   * Bắt buộc gửi kèm expectedRevision từ SceneDto ban đầu.
   */
  async updateScene(payload: SceneUpdateRequest): Promise<SceneDto> {
    const data = await bridgeClient.invoke<SceneUpdateResponseData>(
      "scene.update",
      payload,
    );
    return data.scene;
  },

  /**
   * Xóa một Cảnh khỏi Dự án
   */
  async deleteScene(projectId: string, sceneId: string): Promise<boolean> {
    const data = await bridgeClient.invoke<SceneDeleteResponseData>(
      "scene.delete",
      { projectId, sceneId },
    );
    return data?.success ?? true;
  },

  /**
   * Sắp xếp lại thứ tự danh sách Cảnh trong Dự án.
   * Gửi danh sách các sceneId theo thứ tự mới kèm theo expectedSceneRevisions để chống xung đột ghi đè.
   */
  async reorderScenes(payload: SceneReorderRequest): Promise<SceneDto[]> {
    const data = await bridgeClient.invoke<SceneReorderResponseData>(
      "scene.reorder",
      payload,
    );
    return data?.scenes ?? [];
  },
};
