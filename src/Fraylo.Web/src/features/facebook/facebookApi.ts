import { bridgeClient } from "../../services/bridgeClient";

export interface FacebookAccount { accountId: string; displayName: string; accountIdentity: string | null; sessionStatus: string; enabled?: boolean }
export interface FacebookGroup { targetId: string; accountId: string; name: string; url: string }
export interface FacebookTask {
  taskId: string; batchId: string; accountId: string; targetId: string; groupName: string; groupUrl: string;
  text: string; state: string; message: string; createdAtUtc: string; updatedAtUtc: string; submittedAtUtc: string | null;
}
export interface FacebookList { accounts: FacebookAccount[]; groups: FacebookGroup[]; tasks: FacebookTask[] }
export interface FacebookSync { groups: FacebookGroup[]; isComplete: boolean; message: string }
export interface FacebookBatchRequest { batchRequestId: string; accountId: string; targetIds: string[]; text: string; imageAssetIds: string[] }
export interface FacebookApi {
  list(): Promise<FacebookList>;
  sync(accountId: string): Promise<FacebookSync>;
  create(request: FacebookBatchRequest): Promise<FacebookTask[]>;
  cancel(taskId: string): Promise<FacebookTask>;
  resume(taskId: string): Promise<FacebookTask>;
}
export const facebookApi: FacebookApi = {
  list: () => bridgeClient.invoke("facebook.list", {}),
  sync: accountId => bridgeClient.invoke("facebook.groups.sync", { accountId }, { timeoutMs: 300000 }),
  create: request => bridgeClient.invoke("facebook.tasks.create", request, { timeoutMs: 60000 }),
  cancel: taskId => bridgeClient.invoke("facebook.tasks.cancel", { taskId }),
  resume: taskId => bridgeClient.invoke("facebook.tasks.resume", { taskId }),
};
export function facebookError(error: unknown): string {
  return error && typeof error === "object" && "message" in error ? String(error.message) : "Không thể xử lý yêu cầu Facebook. Hãy tải lại để kiểm tra.";
}
export const facebookStateLabels: Record<string, string> = {
  Queued: "Đang chờ", Preparing: "Đang chuẩn bị", Submitting: "Đã gửi lệnh đăng", Published: "Đã đăng",
  PendingApproval: "Chờ quản trị viên duyệt", NeedsUser: "Cần bạn xử lý", Failed: "Thất bại",
  Unknown: "Chưa xác định kết quả", Canceled: "Đã hủy", Missed: "Đã lỡ lịch", Scheduled: "Đã hẹn giờ",
};
export const isFacebookTaskPending = (state: string) => ["Queued", "Preparing", "Submitting", "NeedsUser", "Scheduled", "Missed"].includes(state);

/** Chọn không lặp từ toàn bộ danh sách đã tải, không phụ thuộc bộ lọc tìm kiếm. */
export function randomGroupIds(groups: readonly FacebookGroup[], count: number, random = Math.random): string[] {
  const ids = [...new Set(groups.map(group => group.targetId))];
  if (!Number.isSafeInteger(count) || count < 1 || count > ids.length) throw new Error(`Nhập số nguyên từ 1 đến ${ids.length}.`);
  for (let i = ids.length - 1; i > 0; i--) {
    const j = Math.floor(random() * (i + 1));
    [ids[i], ids[j]] = [ids[j], ids[i]];
  }
  return ids.slice(0, count);
}
