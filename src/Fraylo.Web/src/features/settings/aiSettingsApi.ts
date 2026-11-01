import { bridgeClient } from "../../services/bridgeClient";

export interface SettingsDto {
  documentsPath?: string;
  imagesPath?: string;
  videosPath?: string;
  documentsRootId?: string;
  imagesRootId?: string;
  videosRootId?: string;
  imageProvider: string | null;
  scriptProvider: string | null;
  videoPromptProvider: string | null;
  themePreference?: string;
  effectiveTheme?: string;
  revision: number;
  maxOpenBrowsers: number;
  maxConcurrentJobs: number;
}

export interface AiSettingsSaveRequest {
  expectedRevision: number;
  clientRequestId?: string;
  imageProvider?: string | null;
  scriptProvider?: string | null;
  videoPromptProvider?: string | null;
  maxOpenBrowsers: number;
  maxConcurrentJobs: number;
  documentsPath?: string;
  imagesPath?: string;
  videosPath?: string;
}

export interface ProviderCapabilityDto {
  providerKey: string;
  displayName: string;
  supportedTasks: string[];
  verificationStatus: "VERIFIED" | "NOT_VERIFIED" | string;
  sessionStatus?: string;
}

export interface ProviderAccountSummaryDto {
  providerAccountId?: string;
  id?: string;
  profileId?: string;
  providerKey: string;
  displayName?: string;
  verifiedIdentity?: string | null;
  sessionStatus?: string;
  isVerified?: boolean;
  enabled?: boolean;
  mode?: string;
  capacity?: {
    maxConcurrentJobs?: number;
    maxTabs?: number;
  };
}

export interface TaskCapabilityDto {
  taskType: string;
  providerKey?: string;
  verificationStatus: "VERIFIED" | "NOT_VERIFIED" | string;
  automationSupported: boolean;
  manualSupported: boolean;
  requiresReferenceImage?: boolean;
  inputLimits?: Record<string, unknown>;
  evidenceId?: string | null;
  date?: string;
  configVersion?: string;
  supportedDurations?: number[];
  supportsMultiShot?: boolean;
}

export interface ProviderListResponse {
  providers?: ProviderCapabilityDto[];
  tasks?: TaskCapabilityDto[];
  accounts?: ProviderAccountSummaryDto[];
}

/**
 * Client giao tiếp Bridge cho module Cài đặt AI & Hạn mức hệ thống theo
 * SETTINGS_PLAN.md, QUEUE_EXECUTION_SETTINGS_PLAN.md và execution-contract-v1.1.md.
 */
export const aiSettingsApi = {
  /**
   * Lấy cấu hình cài đặt hiện hành từ Host (settings.get)
   */
  async getSettings(): Promise<SettingsDto> {
    return await bridgeClient.invoke<SettingsDto>("settings.get", {});
  },

  /**
   * Lưu cài đặt AI và hạn mức đồng thời toàn app (settings.save)
   * Yêu cầu gửi kèm expectedRevision và clientRequestId để đảm bảo an toàn tranh chấp phiên.
   */
  async saveSettings(request: AiSettingsSaveRequest): Promise<SettingsDto> {
    const payload = {
      ...request,
      clientRequestId:
        request.clientRequestId ??
        (typeof crypto !== "undefined" && crypto.randomUUID
          ? crypto.randomUUID()
          : `req_${Date.now()}`),
    };
    return await bridgeClient.invoke<SettingsDto>("settings.save", payload);
  },

  /**
   * Lấy danh sách task capabilities và accounts từ Host (provider.list)
   */
  async listProviders(): Promise<ProviderListResponse> {
    return await bridgeClient.invoke<ProviderListResponse>("provider.list", {});
  },
};
