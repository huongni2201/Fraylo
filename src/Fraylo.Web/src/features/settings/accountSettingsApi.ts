import { bridgeClient } from "../../services/bridgeClient";

export interface ProviderAccountDto {
  id: string;
  providerKey: string;
  profileId: string;
  displayName: string;
  verifiedIdentity?: string | null;
  sessionStatus?: string | null;
  enabled: boolean;
  lifecycle: string;
  maxConcurrentJobs: number;
  maxTabs: number;
  perTaskLimitsJson?: string | null;
  revision: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface CreateProviderAccountPayload {
  providerKey: string;
  displayName: string;
  maxConcurrentJobs?: number;
  maxTabs?: number;
  perTaskLimitsJson?: string | null;
}

export interface UpdateProviderAccountPayload {
  id: string;
  displayName: string;
  enabled: boolean;
  maxConcurrentJobs: number;
  maxTabs: number;
  perTaskLimitsJson?: string | null;
  expectedRevision: number;
}

export interface ProbeSessionResponseData {
  id: string;
  providerKey: string;
  profileId: string;
  sessionStatus: string;
  isVerified: boolean;
  accountIdentity?: string | null;
  errorMessage?: string | null;
  probedAtUtc: string;
}

export const accountSettingsApi = {
  async listAccounts(): Promise<ProviderAccountDto[]> {
    const res = await bridgeClient.invoke<{ accounts: ProviderAccountDto[] }>(
      "provider_account.list",
      {}
    );
    return res?.accounts ?? [];
  },

  async createAccount(payload: CreateProviderAccountPayload): Promise<ProviderAccountDto> {
    const res = await bridgeClient.invoke<{ account: ProviderAccountDto }>(
      "provider_account.create",
      payload
    );
    return res.account;
  },

  async updateAccount(payload: UpdateProviderAccountPayload): Promise<ProviderAccountDto> {
    const res = await bridgeClient.invoke<{ account: ProviderAccountDto }>(
      "provider_account.update",
      payload
    );
    return res.account;
  },

  async probeSession(id: string): Promise<ProbeSessionResponseData> {
    // Probe mở trình duyệt ẩn và đợi trang Provider tải xong, nên lâu hơn timeout mặc định 15s của Bridge
    const res = await bridgeClient.invoke<ProbeSessionResponseData>(
      "provider_account.probe_session",
      { id },
      { timeoutMs: 60000 }
    );
    return res;
  },

  async enableAccount(id: string, enabled: boolean): Promise<ProviderAccountDto> {
    const res = await bridgeClient.invoke<{ account: ProviderAccountDto }>(
      "provider_account.enable",
      { id, enabled }
    );
    return res.account;
  },

  async deleteAccount(id: string): Promise<void> {
    await bridgeClient.invoke(
      "provider_account.delete",
      { id }
    );
  },

  async launchLogin(profileId: string, providerKey?: string, targetUrl?: string): Promise<void> {
    await bridgeClient.invoke(
      "browser.request_login",
      {
        profileId,
        providerKey: providerKey ?? null,
        targetUrl: targetUrl ?? null,
      }
    );
  },
};
