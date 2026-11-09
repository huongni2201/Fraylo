import { listTikTokAnalyticsAccounts, type TikTokAnalyticsAccount } from "../analytics/analyticsApi";

export type PublishState = "Preparing" | "Submitted" | "Published" | "Failed" | "Uncertain";

export interface PublishVideo {
  projectId: string;
  projectName: string;
  assetId: string;
  fileName: string;
  byteSize: number;
  caption: string;
  createdAtUtc: string;
}

export interface PublishRequest {
  clientRequestId: string;
  projectId: string;
  assetId: string;
  accountId: string;
  caption: string;
  /** Tên sound tìm trong trình chỉnh sửa của TikTok; bỏ trống thì không thêm sound. */
  soundName?: string;
  favoriteSoundKey?: string;
  retryAfterAttemptId?: string;
}

export interface PublishAttempt {
  attemptId: string;
  projectId: string;
  assetId: string;
  accountId: string;
  accountIdentity: string;
  caption: string;
  state: PublishState;
  message: string;
  externalPostUrl: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface PublishingList { videos: PublishVideo[]; attempts: PublishAttempt[] }
export interface FavoriteSound { key: string; title: string; author: string; duration: string }
export interface FavoriteSoundsRequest { projectId: string; assetId: string; accountId: string }
export interface FavoriteSoundsResponse { accountId: string; sounds: FavoriteSound[]; isComplete: boolean }
export type PublishingCommand = "publishing.list" | "publishing.publish" | "publishing.favorite_sounds";

// Host truyền bridge đã có allow-list sau khi coordinator hoàn tất tích hợp.
export type PublishingInvoke = <T>(command: PublishingCommand, payload: unknown, options: { timeoutMs: number }) => Promise<T>;
export interface PublishingApi {
  list(): Promise<PublishingList>;
  listAccounts(): Promise<TikTokAnalyticsAccount[]>;
  publish(request: PublishRequest): Promise<PublishAttempt>;
  favoriteSounds(request: FavoriteSoundsRequest): Promise<FavoriteSoundsResponse>;
}

export function createPublishingApi(invoke: PublishingInvoke): PublishingApi {
  return {
    list: () => invoke("publishing.list", {}, { timeoutMs: 30000 }),
    listAccounts: listTikTokAnalyticsAccounts,
    favoriteSounds: request => invoke("publishing.favorite_sounds", request, { timeoutMs: 180000 }),
    publish: (request) => invoke("publishing.publish", request, { timeoutMs: 300000 }),
  };
}

/** Cùng nội dung thì giữ cùng mã yêu cầu (replay không gửi lại); đổi nội dung hoặc thất bại thì tạo mã mới. */
export function requestIdFor(store: Map<string, { signature: string; id: string }>, key: string, signature: string): string {
  let entry = store.get(key);
  if (entry?.signature !== signature) { entry = { signature, id: crypto.randomUUID() }; store.set(key, entry); }
  return entry.id;
}

export function blocksAnotherPublish(state: PublishState): boolean {
  return state !== "Failed" && state !== "Uncertain";
}

export function confirmUncertainPublishRetry(attempt: PublishAttempt): boolean {
  return window.confirm(`Kiểm tra bài đăng trên TikTok của ${attempt.accountIdentity}. Bạn xác nhận video chưa xuất hiện và muốn gửi lại?`);
}

export function publishingError(error: unknown): string {
  return typeof error === "object" && error !== null && "message" in error
    ? String(error.message) : "Không thể xử lý đăng video. Hãy tải lại lịch sử trước khi thử lại.";
}
