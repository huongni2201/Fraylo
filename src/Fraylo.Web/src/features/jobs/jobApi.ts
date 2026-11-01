import { bridgeClient } from "../../services/bridgeClient";
import type { EventEnvelope } from "@/contracts/bridge";
import type {
  JobSummaryDto,
  JobDetailDto,
  JobListActiveRequest,
  JobListActiveResponseData,
  JobGetStatusRequest,
  JobGetStatusResponseData,
  JobCancelRequest,
  JobCancelResponseData,
  JobResumeRequest,
  JobResumeResponseData,
  JobReconcileRequest,
  JobReconcileResponseData,
  JobRecoverOutputRequest,
  JobStateChangedPayload,
  JobProgressUpdatedPayload,
  JobRetryRequest,
  JobRetryResponseData,
  JobListHistoryRequest,
  JobListHistoryResponseData,
} from "./jobTypes";

// Backend chờ video MuseAI tối đa 30 phút rồi mới tải về; timeout phía UI phải dài hơn để không báo lỗi giả khi backend vẫn chạy.
const RECOVER_OUTPUT_TIMEOUT_MS = 35 * 60 * 1000;

export const jobApi = {
  /**
   * Lấy danh sách các tác vụ đang hoạt động toàn app
   * Bao gồm cả tác vụ Canceled/Failed chưa giải quyết (unresolved).
   */
  async listActive(): Promise<JobSummaryDto[]> {
    const payload: JobListActiveRequest = {};
    const res = await bridgeClient.invoke<JobListActiveResponseData>(
      "job.list_active",
      payload,
    );
    return res?.jobs ?? [];
  },

  /**
   * Lấy thông tin trạng thái chi tiết của một tác vụ cụ thể
   */
  async getStatus(jobId: string): Promise<JobDetailDto> {
    const payload: JobGetStatusRequest = { jobId };
    const res = await bridgeClient.invoke<JobGetStatusResponseData>(
      "job.get_status",
      payload,
    );
    return res.job;
  },

  /**
   * Hủy bỏ một tác vụ đang chạy hoặc đang chờ
   */
  async cancel(jobId: string): Promise<JobCancelResponseData> {
    const payload: JobCancelRequest = { jobId };
    return await bridgeClient.invoke<JobCancelResponseData>(
      "job.cancel",
      payload,
    );
  },

  /**
   * Tiếp tục một tác vụ ở trạng thái AwaitingUser sau khi đã đáp ứng điều kiện
   */
  async resume(jobId: string): Promise<JobResumeResponseData> {
    const payload: JobResumeRequest = { jobId };
    return await bridgeClient.invoke<JobResumeResponseData>(
      "job.resume",
      payload,
    );
  },

  /**
   * Đối soát kết quả tác vụ không chắc chắn (reconcile_required)
   */
  async reconcile(payload: JobReconcileRequest): Promise<JobReconcileResponseData> {
    return await bridgeClient.invoke<JobReconcileResponseData>(
      "job.reconcile",
      payload,
    );
  },

  /**
   * Khôi phục video render MuseAI từ side chat đã lưu (không gửi lại prompt), rồi đánh dấu tác vụ thành công
   */
  async recoverOutput(jobId: string): Promise<JobReconcileResponseData> {
    const payload: JobRecoverOutputRequest = { jobId };
    return await bridgeClient.invoke<JobReconcileResponseData>(
      "job.recover_output",
      payload,
      { timeoutMs: RECOVER_OUTPUT_TIMEOUT_MS },
    );
  },

  /**
   * Thử lại an toàn một tác vụ thất bại (C2 - job.retry) với ExpectedRevision (CAS)
   */
  async retry(jobId: string, expectedRevision: number): Promise<JobRetryResponseData> {
    const payload: JobRetryRequest = { jobId, expectedRevision };
    return await bridgeClient.invoke<JobRetryResponseData>(
      "job.retry",
      payload,
    );
  },

  /**
   * Lấy lịch sử các tác vụ đã hoàn tất / kết thúc (C3 - job.list_history)
   */
  async listHistory(req: JobListHistoryRequest = {}): Promise<JobListHistoryResponseData> {
    return await bridgeClient.invoke<JobListHistoryResponseData>(
      "job.list_history",
      req,
    );
  },

  /**
   * Yêu cầu mở trình duyệt đăng nhập thủ công cho profile AI khi gặp login_required
   */
  async requestLogin(profileId: string): Promise<unknown> {
    return await bridgeClient.invoke(
      "browser.request_login",
      { profileId },
    );
  },

  /**
   * Lắng nghe sự kiện chuyển đổi trạng thái tác vụ theo thời gian thực
   */
  onStateChanged(
    handler: (event: EventEnvelope<JobStateChangedPayload>) => void,
  ): () => void {
    return bridgeClient.subscribeEvent<JobStateChangedPayload>(
      "job.state_changed",
      handler,
    );
  },

  /**
   * Lắng nghe sự kiện tiến độ thực tế theo thời gian thực (tỷ lệ % và bước hiện tại)
   */
  onProgressUpdated(
    handler: (event: EventEnvelope<JobProgressUpdatedPayload>) => void,
  ): () => void {
    return bridgeClient.subscribeEvent<JobProgressUpdatedPayload>(
      "job.progress_updated",
      handler,
    );
  },
};
