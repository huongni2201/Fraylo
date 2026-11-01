import { bridgeClient } from "../../services/bridgeClient";
import type { EventEnvelope } from "../../contracts/bridge";

export type ProfileKind = "SharedAI" | "MuseAI" | "TikTok";
export type VisibilityState = "Hidden" | "VisibleWaiting" | "VisibleActive";
export type SessionStatus = "Connected" | "SessionExpired" | "Unverified";
export type OperationalState = "Stopped" | "Idle" | "Running" | "Quiescing" | "Error";
export type ActiveTabRole = "Script" | "Image" | "VideoPrompt";

export interface BrowserProfileDto {
  profileId: string;
  kind: ProfileKind;
  accountIdentity: string | null;
  visibilityState: VisibilityState;
  sessionStatus: SessionStatus;
  operationalState: OperationalState;
  activeJobCount: number;
  activeTabs: string[];
  providerKey?: string | null;
  displayName?: string | null;
  lifecycle?: string;
}

export interface BrowserListProfilesResponseData {
  profiles: BrowserProfileDto[];
}

export interface BrowserRequestLoginResponseData {
  queued: boolean;
  visibilityState: VisibilityState;
  currentHolder: string | null;
}

export interface BrowserCancelLoginResponseData {
  canceled: boolean;
}

export interface BrowserCheckSessionResponseData {
  sessionStatus: SessionStatus;
  identityMatches: boolean | null;
}

export interface BrowserProfileStateChangedPayload {
  profileId: string;
  visibilityState: VisibilityState;
  sessionStatus: SessionStatus;
  operationalState: OperationalState;
  reasonCode: string | null;
}

/**
 * Các API giao tiếp quản lý trình duyệt AI theo contracts §1.6 B
 */
export const browserManagerApi = {
  /**
   * Lấy danh sách profiles trình duyệt dùng chung toàn ứng dụng.
   */
  async listProfiles(): Promise<BrowserProfileDto[]> {
    const data = await bridgeClient.invoke<BrowserListProfilesResponseData>(
      "browser.list_profiles",
      {},
    );
    return data?.profiles ?? [];
  },

  /**
   * Yêu cầu mở cửa sổ đăng nhập thủ công cho một profile
   */
  async requestLogin(
    profileId: string,
    options?: { providerKey?: string; targetUrl?: string },
  ): Promise<BrowserRequestLoginResponseData> {
    return await bridgeClient.invoke<BrowserRequestLoginResponseData>(
      "browser.request_login",
      {
        profileId,
        providerKey: options?.providerKey ?? null,
        targetUrl: options?.targetUrl ?? null,
      },
    );
  },

  /**
   * Hủy yêu cầu chờ hoặc đóng cửa sổ đăng nhập
   */
  async cancelLogin(profileId: string): Promise<BrowserCancelLoginResponseData> {
    return await bridgeClient.invoke<BrowserCancelLoginResponseData>(
      "browser.cancel_login",
      { profileId },
    );
  },

  /**
   * Kiểm tra tính hợp lệ của phiên đăng nhập
   */
  async checkSession(profileId: string): Promise<BrowserCheckSessionResponseData> {
    return await bridgeClient.invoke<BrowserCheckSessionResponseData>(
      "browser.check_session",
      { profileId },
    );
  },

  /**
   * Lắng nghe sự kiện thay đổi trạng thái profile (browser.profile_state_changed)
   */
  onProfileStateChanged(handler: (event: EventEnvelope<BrowserProfileStateChangedPayload>) => void): () => void {
    return bridgeClient.subscribeEvent<BrowserProfileStateChangedPayload>(
      "browser.profile_state_changed",
      handler,
    );
  },
};
