import type { ComponentType } from "react";
import {
  Clock,
  Loader2,
  AlertCircle,
  CheckCircle2,
  Ban,
  FileText,
  ImageIcon,
  Film,
  Sparkles,
  HelpCircle,
  LogIn,
  SearchCheck,
  Play,
  RotateCcw,
  Settings,
} from "lucide-react";

export type JobState =
  | "Queued"
  | "Reserved"
  | "Running"
  | "AwaitingUser"
  | "Succeeded"
  | "Failed"
  | "Canceled";

export const TERMINAL_STATES: readonly JobState[] = ["Succeeded", "Failed", "Canceled"];

export function isTerminalState(state: JobState): boolean {
  return TERMINAL_STATES.includes(state);
}

export type JobTaskType =
  | "Script"
  | "Image"
  | "VideoPrompt"
  | "VideoRender";

export type JobReasonCode =
  | "login_required"
  | "session_expired"
  | "wrong_account"
  | "automation_blocked"
  | "network_error"
  | "auth_closed"
  | "reconcile_required"
  | "max_attempts_exceeded"
  | "lease_expired_max_attempts"
  | "storage_root_unavailable"
  | "provider_rejected"
  | "user_canceled"
  | "canceled_late_result"
  | "tab_capacity_unavailable"
  | "waiting_for_capacity"
  | "waiting_for_reconcile"
  | "global_job_budget_reached"
  | "lease_expired_retry"
  | "reconcile_rejected"
  | "render_timeout"
  | "render_interrupted"
  | "adapter_not_available"
  | string;

export interface JobSummaryDto {
  id: string;
  projectId: string | null;
  sceneId: string | null;
  taskType: JobTaskType | string;
  state: JobState;
  reasonCode: JobReasonCode | null;
  attempts: number;
  maxAttempts?: number;
  sideEffectSubmitted?: number;
  cancelRequested?: boolean;
  hasUnresolvedSideEffect?: boolean;
  profileId?: string | null;
  isHydrating?: boolean;
  resultReference?: string | null;
  resultText?: string | null;
  progressPercent: number | null;
  currentStep: string | null;
  revision?: number;
  createdAtUtc: string;
}

export interface JobDetailDto {
  id: string;
  projectId: string | null;
  sceneId: string | null;
  taskType: JobTaskType | string;
  state: JobState;
  reasonCode: JobReasonCode | null;
  providerKey: string;
  profileId?: string | null;
  storageRootId: string;
  attempts: number;
  maxAttempts: number;
  sideEffectSubmitted: number;
  cancelRequested: boolean;
  hasUnresolvedSideEffect?: boolean;
  resultReference: string | null;
  resultText: string | null;
  revision: number;
  createdAtUtc: string;
  updatedAtUtc: string;
  activity?: JobActivityEntryDto[];
}

export interface JobActivityEntryDto {
  atUtc: string;
  step: string;
  message: string;
}

export type JobListActiveRequest = Record<string, never>;

export interface JobListActiveResponseData {
  jobs: JobSummaryDto[];
}

export interface JobGetStatusRequest {
  jobId: string;
}

export interface JobGetStatusResponseData {
  job: JobDetailDto;
}

export interface JobCancelRequest {
  jobId: string;
}

export interface JobCancelResponseData {
  jobId: string;
  state: JobState;
  cancelRequested: boolean;
  message: string;
  previousState?: JobState | null;
}

export interface JobResumeRequest {
  jobId: string;
}

export interface JobResumeResponseData {
  jobId: string;
  state: JobState;
  message: string;
}

export interface JobReconcileRequest {
  jobId: string;
  action: "mark_succeeded" | "mark_failed" | "cancel" | string;
  confirmedNotReceived?: boolean;
  resultText?: string;
  resultReference?: string;
}

export interface JobReconcileResponseData {
  jobId: string;
  previousState: JobState;
  currentState: JobState;
  reconcileResult: string;
  resultReference: string | null;
  revision?: number;
}

export interface JobRecoverOutputRequest {
  jobId: string;
}

export interface JobRetryRequest {
  jobId: string;
  expectedRevision: number;
}

export interface JobRetryResponseData {
  jobId: string;
  state: JobState;
  message: string;
  revision: number;
}

export interface JobListHistoryRequest {
  projectId?: string;
  cursor?: string | null;
  pageSize?: number;
}

export interface JobListHistoryResponseData {
  items: JobSummaryDto[];
  nextCursor: string | null;
}

export interface JobProgressUpdatedPayload {
  jobId: string;
  projectId: string | null;
  sceneId: string | null;
  state: JobState;
  progressPercent: number;
  currentStep: string;
  jobRevision?: number;
}

export interface JobStateChangedPayload {
  jobId: string;
  projectId: string | null;
  previousState: JobState;
  newState: JobState;
  reasonCode: JobReasonCode | null;
  resultReference: string | null;
  timestampUtc: string;
  jobRevision?: number;
}

export type QueueTabFilter = "all" | "running" | "awaiting" | "failed" | "completed";

export interface TaskTypeMetadata {
  label: string;
  icon: ComponentType<{ className?: string }>;
  description: string;
}

export function getTaskTypeInfo(taskType: string): TaskTypeMetadata {
  switch (taskType) {
    case "FacebookContentRewrite":
      return { label: "AI viết lại bài nhóm", icon: FileText, description: "Tối đa 10 phiên bản cho mỗi lượt" };
    case "AnalyticsAnalysis":
      return { label: "AI phân tích số liệu", icon: HelpCircle, description: "Báo cáo Analytics" };
    case "Script":
      return { label: "Kịch bản", icon: FileText, description: "Tạo cấu trúc kịch bản và phân cảnh" };
    case "Image":
      return { label: "Tạo ảnh", icon: ImageIcon, description: "Sinh hình ảnh trực quan cho phân cảnh" };
    case "VideoPrompt":
      return { label: "Prompt video", icon: Sparkles, description: "Tối ưu hóa câu lệnh chuyển động" };
    case "VideoRender":
      return { label: "Render video", icon: Film, description: "Kết xuất video thông qua nhà cung cấp AI" };
    default:
      return { label: taskType, icon: HelpCircle, description: "Tác vụ hệ thống" };
  }
}

export interface StateBadgeMetadata {
  label: string;
  variant: "default" | "secondary" | "destructive" | "outline" | "warning" | "info" | "success";
  icon: ComponentType<{ className?: string }>;
  animate?: boolean;
  badgeClass?: string;
}

export function getStateBadgeInfo(state: JobState, cancelRequested?: boolean): StateBadgeMetadata {
  if (cancelRequested && state !== "Canceled") {
    return {
      label: "Đang yêu cầu hủy",
      variant: "warning",
      icon: Clock,
      animate: true,
      badgeClass: "border-warning/50 text-warning dark:text-warning bg-warning/10",
    };
  }

  switch (state) {
    case "Queued":
      return {
        label: "Đang chờ",
        variant: "warning",
        icon: Clock,
        badgeClass: "border-warning/30 text-warning dark:text-warning bg-warning/10",
      };
    case "Reserved":
      return {
        label: "Đã giữ chỗ",
        variant: "info",
        icon: Clock,
        badgeClass: "border-info/30 text-info dark:text-info bg-info/10",
      };
    case "Running":
      return {
        label: "Đang chạy",
        variant: "default",
        icon: Loader2,
        animate: true,
        badgeClass: "bg-primary/20 text-primary border-primary/40",
      };
    case "AwaitingUser":
      return {
        label: "Cần xử lý",
        variant: "info",
        icon: AlertCircle,
        badgeClass: "border-warning/50 text-warning dark:text-warning bg-warning/10",
      };
    case "Succeeded":
      return {
        label: "Thành công",
        variant: "success",
        icon: CheckCircle2,
        badgeClass: "border-success/30 text-success dark:text-success bg-success/10",
      };
    case "Failed":
      return {
        label: "Thất bại",
        variant: "destructive",
        icon: AlertCircle,
        badgeClass: "border-destructive/30 text-destructive bg-destructive/10",
      };
    case "Canceled":
      return {
        label: "Đã hủy",
        variant: "outline",
        icon: Ban,
        badgeClass: "border-muted-foreground/30 text-muted-foreground bg-muted/40",
      };
    default:
      return {
        label: state,
        variant: "secondary",
        icon: HelpCircle,
      };
  }
}

export function formatReasonCode(reasonCode?: string | null): string {
  if (!reasonCode) return "";
  switch (reasonCode.toLowerCase()) {
    case "provider_account_required":
      return "Chưa có tài khoản AI đủ điều kiện cho nhà cung cấp của tác vụ. Trong Cài đặt → Tài khoản, hãy thêm tài khoản nếu cần, xác minh phiên đăng nhập và chọn Bật hoạt động.";
    case "analysisrestarted":
      return "Fraylo đã khởi động lại. Bấm Tiếp tục để đưa lượt phân tích trở lại hàng đợi.";
    case "login_required":
      return "Cần đăng nhập tài khoản provider để tiếp tục.";
    case "session_expired":
      return "Phiên đăng nhập đã hết hạn. Vui lòng đăng nhập lại.";
    case "wrong_account":
      return "Tài khoản trong trình duyệt không khớp với tài khoản đã liên kết.";
    case "automation_blocked":
    case "manual_input_required":
    case "manual_handoff_required":
      return "Tự động hóa của nhà cung cấp chưa sẵn sàng. Luồng tạo video chỉ chạy tự động.";
    case "capability_unsupported":
      return "Nhà cung cấp không hỗ trợ tác vụ này.";
    case "missing_provider":
      return "Tác vụ chưa có nhà cung cấp AI trong cấu hình đã lưu.";
    case "network_error":
      return "Lỗi kết nối mạng trong quá trình thực thi.";
    case "auth_closed":
      return "Cửa sổ đăng nhập đã bị đóng trước khi xác minh danh tính.";
    case "reconcile_required":
      return "Tác vụ có thể đã được gửi tới AI. Cần đối soát an toàn trước khi chạy tiếp.";
    case "max_attempts_exceeded":
      return "Đã vượt quá số lần thử lại tối đa cho phép.";
    case "lease_expired_max_attempts":
      return "Tác vụ bị gián đoạn quá lâu và đã hết lượt thử lại.";
    case "storage_root_unavailable":
      return "Thư mục lưu trữ đích không khả dụng hoặc bị ngắt kết nối.";
    case "provider_rejected":
      return "Nhà cung cấp AI từ chối nội dung hoặc yêu cầu.";
    case "user_canceled":
      return "Người dùng đã chủ động hủy bỏ tác vụ.";
    case "canceled_late_result":
      return "Tác vụ đã hủy nhận được kết quả muộn (đã bảo toàn an toàn dạng Unlinked).";
    case "tab_capacity_unavailable":
      return "Tài khoản hiện đang bận hết tab đồng thời. Đang chờ tab rảnh.";
    case "waiting_for_capacity":
      return "Đang chờ tài khoản AI khả dụng hoặc giải phóng hạn ngạch.";
    case "waiting_for_reconcile":
      return "Đang chờ tác vụ cần đối soát trên cùng tài khoản AI được xử lý xong.";
    case "global_job_budget_reached":
      return "Đạt giới hạn tác vụ đồng thời toàn ứng dụng.";
    case "lease_expired_retry":
      return "Tiến trình trước bị gián đoạn, đang tự động xếp hàng thử lại.";
    case "reconcile_rejected":
      return "Kết quả đối soát bị từ chối do không hợp lệ.";
    case "render_timeout":
      return "Quá thời gian chờ AI tạo video. Tác vụ đã dừng và giải phóng tài nguyên; có thể thử lại từ bước chờ.";
    case "render_interrupted":
      return "Việc tạo video bị gián đoạn sau khi đã gửi yêu cầu. Tác vụ đã dừng và giải phóng tài nguyên; có thể thử lại từ bước chờ.";
    case "adapter_not_available":
      return "Bộ điều hợp của nhà cung cấp AI này chưa sẵn sàng trong phiên bản hiện tại.";
    default:
      return reasonCode;
  }
}

function isAutomationUnavailable(reasonCode?: string | null): boolean {
  return [
    "automation_blocked",
    "adapter_not_available",
    "missing_provider",
    "capability_unsupported",
    "manual_input_required",
    "manual_handoff_required",
  ].includes(reasonCode?.toLowerCase() ?? "");
}

export function getRecommendedAction(
  job: Pick<JobSummaryDto, "state" | "reasonCode"> & {
    cancelRequested?: boolean;
    sideEffectSubmitted?: number;
    hasUnresolvedSideEffect?: boolean;
    attempts?: number;
    maxAttempts?: number;
    profileId?: string | null;
    isHydrating?: boolean;
  }
): {
  type: "login" | "navigate_settings_accounts" | "reconcile" | "resume" | "retry" | "cancel" | "none";
  label: string;
  icon: ComponentType<{ className?: string }>;
  description?: string;
  variant?: "default" | "outline" | "destructive" | "secondary";
} {
  // Đang nạp metadata từ backend: khóa tương tác để tránh thao tác sai lệch
  if (job.isHydrating) {
    return {
      type: "none",
      label: "Đang nạp dữ liệu...",
      icon: Loader2,
      description: "Đang đồng bộ thông tin chi tiết tác vụ từ máy chủ.",
    };
  }

  // Đang chờ xử lý hủy an toàn từ backend/worker
  if (job.cancelRequested && job.state !== "Canceled") {
    return {
      type: "none",
      label: "Đang chờ hủy...",
      icon: Clock,
      description: "Hệ thống đang đối soát với provider để hủy an toàn.",
    };
  }

  // T17-R3: Terminal unresolved (Canceled hoặc Failed còn unresolved occupancy / side-effect)
  const isTerminalUnresolved =
    (job.state === "Canceled" || job.state === "Failed") &&
    (job.hasUnresolvedSideEffect === true ||
      job.sideEffectSubmitted === 1 ||
      job.reasonCode === "reconcile_required");

  if (isTerminalUnresolved) {
    return {
      type: "reconcile",
      label: "Đối soát giải phóng tài nguyên",
      icon: SearchCheck,
      variant: "default",
      description: "Tác vụ còn giữ chỗ tài nguyên trình duyệt. Đối soát để giải phóng an toàn.",
    };
  }

  if (
    job.state === "AwaitingUser" &&
    isAutomationUnavailable(job.reasonCode) &&
    (job.hasUnresolvedSideEffect === true || job.sideEffectSubmitted === 1)
  ) {
    return {
      type: "reconcile",
      label: "Đối soát tác vụ",
      icon: SearchCheck,
      variant: "default",
      description: "Kiểm tra kết quả đã gửi tới provider trước khi chạy tiếp.",
    };
  }

  if (isAutomationUnavailable(job.reasonCode)) {
    return {
      type: "none",
      label: "",
      icon: AlertCircle,
      description: "Tự động hóa chưa sẵn sàng. Kiểm tra nhà cung cấp trước khi tạo yêu cầu mới.",
    };
  }

  if (job.state === "Queued" && job.reasonCode === "provider_account_required") {
    return {
      type: "navigate_settings_accounts",
      label: "Đến Cài đặt Tài khoản",
      icon: Settings,
      variant: "default",
    };
  }

  // AwaitingUser state
  if (job.state === "AwaitingUser") {
    if (
      job.reasonCode === "login_required" ||
      job.reasonCode === "session_expired" ||
      job.reasonCode === "auth_closed"
    ) {
      // T17-R4: Phân biệt có profileId thật hay chưa bind tài khoản
      if (job.profileId && job.profileId.trim() !== "") {
        return {
          type: "login",
          label: "Đăng nhập ngay",
          icon: LogIn,
          variant: "default",
          description: "Mở trình duyệt để đăng nhập thủ công vào profile AI.",
        };
      } else {
        return {
          type: "navigate_settings_accounts",
          label: "Đến Cài đặt Tài khoản",
          icon: Settings,
          variant: "default",
          description: "Tác vụ chưa liên kết profile trình duyệt. Mở trang Cài đặt → Tài khoản để cấu hình.",
        };
      }
    }

    if (job.reasonCode === "reconcile_required") {
      return {
        type: "reconcile",
        label: "Đối soát tác vụ",
        icon: SearchCheck,
        variant: "default",
        description: "Kiểm tra xem tác vụ đã sinh kết quả bên ngoài chưa.",
      };
    }

    return {
      type: "resume",
      label: "Tiếp tục",
      icon: Play,
      variant: "default",
      description: "Tiếp tục thực thi sau khi đã kiểm tra điều kiện.",
    };
  }

  // T17-R2: Failed state: chỉ cho phép retry khi chắc chắn sideEffectSubmitted === 0, hasUnresolvedSideEffect !== true, attempts < maxAttempts và lý do hợp lệ
  if (job.state === "Failed") {
    // Nếu job đã bị hủy hoặc có cờ hủy: cấm retry
    if (job.cancelRequested) {
      return {
        type: "none",
        label: "",
        icon: HelpCircle,
      };
    }

    // Nếu còn tồn tại unresolved side effect: cấm retry tự động
    if (job.hasUnresolvedSideEffect === true) {
      return {
        type: "none",
        label: "",
        icon: HelpCircle,
      };
    }

    // Lý do cấm retry theo v1.1 §5
    const NON_RETRYABLE_REASONS = [
      "user_canceled",
      "canceled_late_result",
      "login_required",
      "session_expired",
      "auth_closed",
      "wrong_account",
      "storage_root_unavailable",
      "adapter_not_available",
    ];
    if (job.reasonCode && NON_RETRYABLE_REASONS.includes(job.reasonCode)) {
      return {
        type: "none",
        label: "",
        icon: HelpCircle,
      };
    }

    // Chỉ khi sideEffectSubmitted xác nhận rõ ràng bằng 0, hoặc render MuseAI hết giờ (chỉ tiếp tục chờ side chat đã lưu): mới cho retry
    const resumableRender = job.reasonCode === "render_timeout" || job.reasonCode === "render_interrupted";
    if (job.sideEffectSubmitted === 0 || (resumableRender && job.state === "Failed")) {
      return {
        type: "retry",
        label: "Thử lại",
        icon: RotateCcw,
        variant: "outline",
        description: "Thử lại tác vụ nếu còn lượt.",
      };
    }

    // Nếu sideEffectSubmitted chưa xác định (undefined) hoặc bằng 1: không được phép coi là safe retry!
    return {
      type: "none",
      label: "",
      icon: HelpCircle,
    };
  }

  return {
    type: "none",
    label: "",
    icon: HelpCircle,
  };
}
