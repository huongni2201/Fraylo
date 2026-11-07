import { bridgeClient } from "../../services/bridgeClient";

export interface FacebookAccount { accountId: string; displayName: string; accountIdentity: string | null; sessionStatus: string; enabled?: boolean }
export interface FacebookGroup { targetId: string; accountId: string; name: string; url: string }
export interface FacebookActivityEntry { atUtc: string; code: string; message: string; reasonCode?: string | null }
export interface FacebookTask {
  taskId: string; batchId: string; accountId: string; targetId: string; groupName: string; groupUrl: string;
  text: string; state: string; message: string; createdAtUtc: string; updatedAtUtc: string; submittedAtUtc: string | null;
  reasonCode?: string | null; dueAtUtc?: string | null; imageAssetIds?: string[]; activityLog?: FacebookActivityEntry[];
}
export interface FacebookDraft { draftId: string; name: string; accountId: string; text: string; targetIds: string[]; imageAssetIds: string[]; updatedAtUtc: string; dueAtUtc?: string | null; rewriteContent?: boolean }
export interface FacebookList { accounts: FacebookAccount[]; groups: FacebookGroup[]; tasks: FacebookTask[]; drafts?: FacebookDraft[]; posts?: FacebookPost[]; rewriteProvider?: string | null }
export interface FacebookSync { groups: FacebookGroup[]; isComplete: boolean; message: string }
export interface FacebookBatchRequest { batchRequestId: string; accountId: string; targetIds: string[]; text: string; imageAssetIds: string[]; dueAtUtc?: string; name?: string; targetKind?: "Group" | "Profile"; rewriteContent?: boolean }
export interface FacebookDraftInput { draftId?: string; name: string; accountId: string; text: string; targetIds: string[]; imageAssetIds: string[]; dueAtUtc?: string; rewriteContent?: boolean }
export interface FacebookRewriteChunk { chunkId: string; ordinal: number; count: number; state: string; reason: string | null; applied: boolean; canRetry: boolean; uncertain: boolean; jobId?: string | null }
export interface FacebookPost { batchId: string; name: string; accountId: string; paused: boolean; revision: number; createdAtUtc: string; tasks: FacebookTask[]; originalText?: string | null; rewriteChunks?: FacebookRewriteChunk[] | null }
export interface FacebookApi {
  retryRewrite?(batchId: string, chunkId: string, revision: number): Promise<void>;
  postAction?(batchId: string, revision: number, action: string, dueAtUtc?: string): Promise<FacebookPost>;
  list(): Promise<FacebookList>;
  sync(accountId: string): Promise<FacebookSync>;
  create(request: FacebookBatchRequest): Promise<FacebookTask[]>;
  cancel(taskId: string): Promise<FacebookTask>;
  resume(taskId: string): Promise<FacebookTask>;
  postNow(taskId: string): Promise<FacebookTask>;
  reschedule(taskId: string, dueAtUtc: string): Promise<FacebookTask>;
  saveDraft(draft: FacebookDraftInput): Promise<FacebookDraft>;
  deleteDraft(draftId: string): Promise<void>;
}
export const facebookApi: FacebookApi = {
  retryRewrite: (batchId, chunkId, revision) => bridgeClient.invoke("facebook.rewrite.retry", { batchId, chunkId, revision }),
  postAction: (batchId, revision, action, dueAtUtc) => bridgeClient.invoke("facebook.posts.action", { batchId, revision, action, dueAtUtc }),
  list: () => bridgeClient.invoke("facebook.list", {}),
  sync: accountId => bridgeClient.invoke("facebook.groups.sync", { accountId }, { timeoutMs: 300000 }),
  create: request => bridgeClient.invoke("facebook.tasks.create", request, { timeoutMs: 60000 }),
  cancel: taskId => bridgeClient.invoke("facebook.tasks.cancel", { taskId }),
  resume: taskId => bridgeClient.invoke("facebook.tasks.resume", { taskId }),
  postNow: taskId => bridgeClient.invoke("facebook.tasks.post_now", { taskId }),
  reschedule: (taskId, dueAtUtc) => bridgeClient.invoke("facebook.tasks.reschedule", { taskId, dueAtUtc }),
  saveDraft: draft => bridgeClient.invoke("facebook.drafts.save", draft),
  deleteDraft: async draftId => { await bridgeClient.invoke("facebook.drafts.delete", { draftId }); },
};
export function facebookError(error: unknown): string {
  return error && typeof error === "object" && "message" in error ? String(error.message) : "Không thể xử lý yêu cầu Facebook. Hãy tải lại để kiểm tra.";
}
export function rewriteReasonMessage(reason: string): string {
  const messages: Record<string, string> = {
    InvalidRewriteResult: "AI trả nội dung chưa đúng định dạng, độ dài hoặc dữ kiện. Nội dung nhóm chưa được thay.",
    RewriteDuplicateResult: "Có nội dung trùng với lượt AI đã lưu. Hãy thử lại lượt này.",
    RewriteTaskChanged: "Bài hoặc nhóm đã đổi trạng thái; hãy kiểm tra trước khi tiếp tục.",
    RewriteSaveFailed: "Chưa lưu được nội dung. Kết quả AI đã được giữ; thử lại sẽ lưu từ kết quả này, không gửi AI lần nữa.",
    RewriteRestarted: "Ứng dụng đã khởi động lại. Chọn tiếp tục khi bạn sẵn sàng.",
    provider_account_required: "Chưa có tài khoản AI sẵn sàng. Kiểm tra tài khoản trong Cài đặt.",
    reconcile_required: "Chưa xác định được kết quả gửi AI. Kiểm tra tác vụ trong Tiến trình.",
    global_job_budget_reached: "Đang chờ chỗ trống trong giới hạn tác vụ đồng thời.",
    user_canceled: "Lượt AI đã hủy; chỉ thử lại nếu bạn vẫn muốn xử lý bài này.",
  };
  return messages[reason] ?? "Lượt AI cần được kiểm tra trong Tiến trình hoặc Cài đặt trước khi tiếp tục.";
}
export const facebookStateLabels: Record<string, string> = {
  Queued: "Đang chờ", Preparing: "Đang chuẩn bị", Submitting: "Đã gửi lệnh đăng", Published: "Đã đăng",
  PendingApproval: "Chờ quản trị viên duyệt", NeedsUser: "Cần bạn xử lý", Failed: "Thất bại",
  Unknown: "Chưa xác định kết quả", Canceled: "Đã hủy", Missed: "Đã lỡ lịch", Scheduled: "Đã hẹn giờ",
};
export const isFacebookTaskPending = (state: string) => ["Queued", "Preparing", "Submitting", "NeedsUser", "Scheduled", "Missed"].includes(state);

/** Trạng thái hiển thị: hẹn giờ và lỡ lịch dùng chung trạng thái hàng đợi ở backend, phân biệt qua giờ hẹn/mã lý do. */
export function facebookDisplayState(task: Pick<FacebookTask, "state" | "reasonCode" | "dueAtUtc">, now = Date.now()): string {
  if (task.state === "Queued" && task.dueAtUtc && Date.parse(task.dueAtUtc) > now) return "Scheduled";
  if (task.state === "NeedsUser" && task.reasonCode === "schedule_missed") return "Missed";
  return task.state;
}

/** Giá trị input datetime-local (giờ máy) → ISO UTC; trống hoặc sai định dạng thì ném lỗi. */
export function localInputToUtc(value: string): string {
  const time = new Date(value).getTime();
  if (!value || Number.isNaN(time)) throw new Error("Chọn giờ hẹn hợp lệ.");
  return new Date(time).toISOString();
}

/** ISO UTC → giá trị input datetime-local theo giờ máy, làm tròn xuống phút. */
export function utcToLocalInput(iso: string): string {
  const date = new Date(iso);
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

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
