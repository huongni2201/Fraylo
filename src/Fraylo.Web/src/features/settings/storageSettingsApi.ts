import { bridgeClient } from "../../services/bridgeClient";

export interface SettingsDto {
  documentsPath: string;
  imagesPath: string;
  videosPath: string;
  documentsRootId: string;
  imagesRootId: string;
  videosRootId: string;
  imageProvider: string | null;
  scriptProvider: string | null;
  videoPromptProvider: string | null;
  themePreference: string;
  effectiveTheme: string;
}

export interface SettingsSaveRequest {
  documentsPath: string;
  imagesPath: string;
  videosPath: string;
  imageProvider?: string | null;
  scriptProvider?: string | null;
  videoPromptProvider?: string | null;
}

export interface BrowseFolderRequest {
  target: "documents" | "images" | "videos";
  initialPath?: string | null;
}

export interface BrowseFolderResponse {
  selectedPath: string | null;
}

export interface ValidateRootsRequest {
  documentsPath: string;
  imagesPath: string;
  videosPath: string;
}

export interface ValidateRootsResponse {
  isValid: boolean;
  fieldErrors: Record<string, string[]>;
}

/**
 * Client giao tiếp Bridge cho module Cài đặt & Lưu trữ theo SETTINGS_PLAN.md và contracts §5.
 */
export const storageSettingsApi = {
  /**
   * Lấy cấu hình cài đặt hiện hành từ Host
   */
  async getSettings(): Promise<SettingsDto> {
    return await bridgeClient.invoke<SettingsDto>("settings.get", {});
  },

  /**
   * Xác thực trước ba đường dẫn thư mục lưu trữ độc lập
   */
  async validateRoots(request: ValidateRootsRequest): Promise<ValidateRootsResponse> {
    return await bridgeClient.invoke<ValidateRootsResponse>("settings.validate_roots", request);
  },

  /**
   * Lưu cài đặt mới, Host xác thực và rollback atomic nếu có lỗi
   */
  async saveSettings(request: SettingsSaveRequest): Promise<SettingsDto> {
    return await bridgeClient.invoke<SettingsDto>("settings.save", request);
  },

  /**
   * Mở hộp thoại chọn thư mục Windows an toàn qua Host
   */
  async browseFolder(
    target: "documents" | "images" | "videos",
    initialPath?: string | null,
  ): Promise<string | null> {
    const response = await bridgeClient.invoke<BrowseFolderResponse>("settings.browse_folder", {
      target,
      initialPath: initialPath ?? null,
    });
    return response.selectedPath;
  },
};
