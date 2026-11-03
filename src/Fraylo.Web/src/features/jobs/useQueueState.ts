import { useState, useEffect, useCallback, useMemo, useRef } from "react";
import type { ErrorDto } from "@/contracts/bridge";
import {
  type JobSummaryDto,
  type JobDetailDto,
  type JobState,
  type JobReconcileRequest,
  type QueueTabFilter,
  isTerminalState,
} from "./jobTypes";
import { jobApi } from "./jobApi";

export type JobItem = JobSummaryDto;

export interface UseQueueStateOptions {
  autoFetch?: boolean;
  isOpen?: boolean;
}

export interface UseQueueStateReturn {
  jobs: JobItem[];
  filteredJobs: JobItem[];
  isLoading: boolean;
  error: ErrorDto | null;
  activeTab: QueueTabFilter;
  searchQuery: string;
  selectedJobId: string | null;
  selectedJobDetail: JobDetailDto | null;
  reconcileJob: JobItem | null;
  isActionLoading: Record<string, boolean>;
  tabCounts: Record<QueueTabFilter, number>;

  // State setters
  setActiveTab: (tab: QueueTabFilter) => void;
  setSearchQuery: (query: string) => void;
  setSelectedJobId: (jobId: string | null) => void;
  setReconcileJob: (job: JobItem | null) => void;
  clearError: () => void;

  // Actions
  refresh: () => Promise<void>;
  handleCancel: (job: JobSummaryDto | JobDetailDto) => Promise<boolean>;
  handleResume: (job: JobSummaryDto | JobDetailDto) => Promise<boolean>;
  handleReconcile: (request: JobReconcileRequest) => Promise<boolean>;
  handleRequestLogin: (profileId: string) => Promise<boolean>;
  handleRetry: (job: JobSummaryDto | JobDetailDto) => Promise<boolean>;
  fetchJobDetail: (jobId: string) => Promise<JobDetailDto | null>;
}

export function matchesTab(state: JobState, tab: QueueTabFilter): boolean {
  switch (tab) {
    case "all":
      return true;
    case "running":
      return state === "Running" || state === "Reserved";
    case "awaiting":
      return state === "AwaitingUser" || state === "Queued";
    case "failed":
      return state === "Failed";
    case "completed":
      return state === "Succeeded" || state === "Canceled";
    default:
      return true;
  }
}

const ACTIVE_STATES: readonly JobState[] = ["Queued", "Reserved", "Running", "AwaitingUser"];

function createErrorDto(code: string, message: string): ErrorDto {
  return {
    code,
    message,
    retryable: false,
    correlationId: `err_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`,
    details: null,
  };
}

export function useQueueState(options: UseQueueStateOptions = {}): UseQueueStateReturn {
  const { autoFetch = true, isOpen } = options;

  const [jobs, setJobs] = useState<JobItem[]>([]);
  const [historyJobs, setHistoryJobs] = useState<JobItem[]>([]);
  const [isLoading, setIsLoading] = useState<boolean>(false);
  const [error, setError] = useState<ErrorDto | null>(null);
  const [activeTab, setActiveTab] = useState<QueueTabFilter>("all");
  const [searchQuery, setSearchQuery] = useState<string>("");
  const [selectedJobId, setSelectedJobId] = useState<string | null>(null);
  const [selectedJobDetail, setSelectedJobDetail] = useState<JobDetailDto | null>(null);
  const [reconcileJob, setReconcileJob] = useState<JobItem | null>(null);
  const [isActionLoading, setIsActionLoading] = useState<Record<string, boolean>>({});

  // Ref đồng bộ chặn race condition / double-click trên cùng jobId
  const actionLoadingRef = useRef<Set<string>>(new Set());

  // T17-R1: Lưu revision logic lớn nhất theo jobId
  const eventRevisionsRef = useRef<Map<string, number>>(new Map());

  // T17-R1: Generation counter để discard response cũ khi có nhiều refresh đồng thời
  const fetchGenerationRef = useRef<number>(0);

  // Lưu trữ lịch sử các job đã hoàn tất trong phiên
  const completedHistoryRef = useRef<Map<string, JobItem>>(new Map());

  const clearError = useCallback(() => {
    setError(null);
  }, []);

  // T17-R6: Tải lịch sử hoàn tất từ backend qua job.list_history
  const loadHistory = useCallback(async () => {
    try {
      const res = await jobApi.listHistory({ pageSize: 50 });
      if (res?.items) {
        setHistoryJobs(res.items);
        for (const item of res.items) {
          completedHistoryRef.current.set(item.id, item);
        }
      }
    } catch (err) {
      console.warn("[Fraylo Queue] Failed to load history jobs:", err);
    }
  }, []);

  // T17-R1 & T17-R6: Refresh có kiểm soát generation và merge snapshot an toàn theo logical revision
  const refresh = useCallback(async () => {
    const currentGen = ++fetchGenerationRef.current;

    setIsLoading(true);
    setError(null);

    try {
      const activeJobs = await jobApi.listActive();

      // Nếu đã có một request refresh mới hơn: discard response cũ
      if (currentGen !== fetchGenerationRef.current) {
        return;
      }

      setJobs((prevJobs) => {
        const prevMap = new Map<string, JobItem>(prevJobs.map((j) => [j.id, j]));
        const mergedJobs: JobItem[] = [];
        const seenIds = new Set<string>();

        // 1. Duyệt qua các active jobs từ backend response
        for (const rawJob of activeJobs) {
          seenIds.add(rawJob.id);
          const localJob = prevMap.get(rawJob.id);
          const rawRev = rawJob.revision ?? 1;

          if (localJob) {
            const localRev = localJob.revision ?? 1;
            const isLocalTerminal = isTerminalState(localJob.state);
            const isRemoteActive = !isTerminalState(rawJob.state);

            // T17-R1: Nếu localJob có revision mới hơn response HOẶC localJob đã terminal mà remote vẫn active: GIỮ LOCAL!
            if (localRev > rawRev || (isLocalTerminal && isRemoteActive)) {
              mergedJobs.push(localJob);
              continue;
            }
          }

          // Cập nhật job từ snapshot backend
          const jobToAdd: JobItem = {
            ...rawJob,
            revision: Math.max(rawRev, localJob?.revision ?? 1),
            cancelRequested: localJob?.cancelRequested ?? rawJob.cancelRequested,
            profileId: localJob?.profileId ?? rawJob.profileId,
            sideEffectSubmitted: localJob?.sideEffectSubmitted ?? rawJob.sideEffectSubmitted,
            hasUnresolvedSideEffect: localJob?.hasUnresolvedSideEffect ?? rawJob.hasUnresolvedSideEffect,
            maxAttempts: localJob?.maxAttempts ?? rawJob.maxAttempts,
          };

          if (isTerminalState(jobToAdd.state)) {
            completedHistoryRef.current.set(jobToAdd.id, jobToAdd);
          }

          mergedJobs.push(jobToAdd);
        }

        // 2. Bổ sung các job hoàn tất từ lịch sử phiên nếu có
        for (const [historyId, historyJob] of completedHistoryRef.current.entries()) {
          if (!seenIds.has(historyId)) {
            mergedJobs.push(historyJob);
            seenIds.add(historyId);
          }
        }

        mergedJobs.sort(
          (a, b) => new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime()
        );

        return mergedJobs;
      });

      // Nếu đang mở tab completed, nạp cả history
      if (activeTab === "completed") {
        void loadHistory();
      }
    } catch (err) {
      if (currentGen === fetchGenerationRef.current) {
        setError(err as ErrorDto);
      }
    } finally {
      if (currentGen === fetchGenerationRef.current) {
        setIsLoading(false);
      }
    }
  }, [activeTab, loadHistory]);

  // Tự động tải dữ liệu ban đầu hoặc khi mở drawer
  useEffect(() => {
    if (autoFetch) {
      void refresh();
    }
  }, [refresh, autoFetch]);

  useEffect(() => {
    if (isOpen) {
      void refresh();
    }
  }, [isOpen, refresh]);

  // T17-R6: Tải lịch sử khi người dùng chuyển sang tab completed
  useEffect(() => {
    if (activeTab === "completed") {
      void loadHistory();
    }
  }, [activeTab, loadHistory]);

  // T17-R5: Hàm hydrate chi tiết tác vụ bất đồng bộ từ backend
  const hydrateJob = useCallback(async (jobId: string) => {
    try {
      const detail = await jobApi.getStatus(jobId);
      setJobs((prevJobs) =>
        prevJobs.map((j) => {
          if (j.id !== jobId) return j;
          return {
            ...j,
            taskType: detail.taskType,
            projectId: detail.projectId,
            sceneId: detail.sceneId,
            providerKey: detail.providerKey,
            profileId: detail.profileId ?? null,
            attempts: detail.attempts,
            maxAttempts: detail.maxAttempts,
            sideEffectSubmitted: detail.sideEffectSubmitted,
            cancelRequested: detail.cancelRequested,
            hasUnresolvedSideEffect: detail.hasUnresolvedSideEffect,
            resultReference: detail.resultReference ?? j.resultReference,
            revision: Math.max(detail.revision ?? 1, j.revision ?? 1),
            isHydrating: false,
          };
        })
      );
    } catch (err) {
      console.warn(`[Fraylo Queue] Hydrate job ${jobId} failed:`, err);
      setJobs((prevJobs) =>
        prevJobs.map((j) => (j.id === jobId ? { ...j, isHydrating: false } : j))
      );
    }
  }, []);

  // Lắng nghe realtime events từ Bridge
  useEffect(() => {
    const unsubState = jobApi.onStateChanged((envelope) => {
      const payload = envelope.payload;
      if (!payload?.jobId) return;

      const jobRev = payload.jobRevision;
      const lastRev = eventRevisionsRef.current.get(payload.jobId) ?? 0;

      // T17-R1: Kiểm tra revision để bỏ qua stale event
      if (jobRev !== undefined && jobRev < lastRev) {
        return;
      }
      if (jobRev !== undefined) {
        eventRevisionsRef.current.set(payload.jobId, jobRev);
      }

      setJobs((prevJobs) => {
        const existingIdx = prevJobs.findIndex((j) => j.id === payload.jobId);
        if (existingIdx >= 0) {
          const updated = [...prevJobs];
          const existing = updated[existingIdx];
          const currentRev = existing.revision ?? 1;

          // T17-R1: Gap detection: nếu revision nhảy cóc -> kích hoạt reload để lấp gap
          if (jobRev !== undefined && jobRev > currentRev + 1) {
            queueMicrotask(() => {
              void refresh();
            });
          }

          if (jobRev !== undefined && jobRev < currentRev) {
            return prevJobs;
          }

          const updatedJob: JobItem = {
            ...existing,
            state: payload.newState,
            reasonCode: payload.reasonCode ?? null,
            resultReference: payload.resultReference ?? existing.resultReference,
            revision: jobRev ?? currentRev + 1,
            cancelRequested:
              payload.newState === "Canceled" ? false : existing.cancelRequested,
          };
          updated[existingIdx] = updatedJob;

          if (isTerminalState(payload.newState)) {
            completedHistoryRef.current.set(payload.jobId, updatedJob);
          }

          return updated;
        }

        // T17-R5: Job mới qua event -> Tạo placeholder an toàn và kích hoạt hydrate
        const isTerminal = isTerminalState(payload.newState);
        const newJob: JobItem = {
          id: payload.jobId,
          projectId: payload.projectId ?? "",
          sceneId: null,
          // Placeholder loading khi chưa hydrate, KHÔNG gán Script hay attempts=1 giả
          taskType: isTerminal ? "Tác vụ hoàn tất" : "Đang nạp dữ liệu...",
          state: payload.newState,
          reasonCode: payload.reasonCode ?? null,
          attempts: 1,
          progressPercent: null,
          currentStep: null,
          createdAtUtc: payload.timestampUtc || new Date().toISOString(),
          resultReference: payload.resultReference ?? null,
          revision: jobRev ?? 1,
          isHydrating: !isTerminal,
        };

        if (isTerminal) {
          completedHistoryRef.current.set(payload.jobId, newJob);
        } else if (ACTIVE_STATES.includes(payload.newState)) {
          void hydrateJob(payload.jobId);
        }

        const newList = [newJob, ...prevJobs];
        newList.sort(
          (a, b) => new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime()
        );
        return newList;
      });
    });

    // T17-R1: onProgressUpdated bảo vệ tuyệt đối terminal state
    const unsubProgress = jobApi.onProgressUpdated((envelope) => {
      const payload = envelope.payload;
      if (!payload?.jobId) return;

      setJobs((prevJobs) => {
        const idx = prevJobs.findIndex((j) => j.id === payload.jobId);
        if (idx === -1) return prevJobs;

        const existing = prevJobs[idx];

        // T17-R1: NẾU JOB ĐÃ Ở TRẠNG THÁI TERMINAL: CẤM ĐỔI VỀ RUNNING HAY QUEUED!
        if (isTerminalState(existing.state)) {
          return prevJobs;
        }

        // Nếu progress event có revision cũ hơn revision hiện tại của job -> bỏ qua
        if (payload.jobRevision !== undefined && payload.jobRevision < (existing.revision ?? 1)) {
          return prevJobs;
        }

        const updated = [...prevJobs];
        updated[idx] = {
          ...existing,
          progressPercent: payload.progressPercent,
          currentStep: payload.currentStep,
          state: payload.state || existing.state,
          revision: Math.max(existing.revision ?? 1, payload.jobRevision ?? 1),
        };
        return updated;
      });
    });

    return () => {
      unsubState();
      unsubProgress();
    };
  }, [hydrateJob, refresh]);

  // Chi tiết job khi người dùng chọn
  const fetchJobDetail = useCallback(
    async (jobId: string): Promise<JobDetailDto | null> => {
      try {
        const detail = await jobApi.getStatus(jobId);
        setSelectedJobDetail(detail);

        setJobs((prevJobs) =>
          prevJobs.map((j) =>
            j.id === jobId
              ? {
                  ...j,
                  profileId: detail.profileId ?? j.profileId,
                  sideEffectSubmitted: detail.sideEffectSubmitted,
                  maxAttempts: detail.maxAttempts,
                  cancelRequested: detail.cancelRequested,
                  hasUnresolvedSideEffect: detail.hasUnresolvedSideEffect,
                  revision: Math.max(detail.revision ?? 1, j.revision ?? 1),
                }
              : j
          )
        );

        return detail;
      } catch (err) {
        setError(err as ErrorDto);
        return null;
      }
    },
    []
  );

  useEffect(() => {
    if (!selectedJobId) {
      setSelectedJobDetail(null);
      return;
    }
    const currentJob = jobs.find((j) => j.id === selectedJobId);
    if (currentJob) {
      void fetchJobDetail(currentJob.id);
    }
  }, [selectedJobId, jobs, fetchJobDetail]);

  // Hành động Hủy tác vụ an toàn
  const handleCancel = useCallback(
    async (job: JobSummaryDto | JobDetailDto): Promise<boolean> => {
      if (actionLoadingRef.current.has(job.id)) {
        return false;
      }
      actionLoadingRef.current.add(job.id);
      setIsActionLoading((prev) => ({ ...prev, [job.id]: true }));
      try {
        const res = await jobApi.cancel(job.id);
        setJobs((prevJobs) =>
          prevJobs.map((j) => {
            if (j.id !== job.id) return j;
            const updated: JobItem = {
              ...j,
              state: res.state,
              cancelRequested: res.cancelRequested ?? true,
              revision: (j.revision ?? 1) + 1,
            };
            if (isTerminalState(res.state)) {
              completedHistoryRef.current.set(job.id, updated);
            }
            return updated;
          })
        );
        return true;
      } catch (err) {
        setError(err as ErrorDto);
        return false;
      } finally {
        actionLoadingRef.current.delete(job.id);
        setIsActionLoading((prev) => ({ ...prev, [job.id]: false }));
      }
    },
    []
  );

  // Hành động Tiếp tục tác vụ (chỉ áp dụng cho AwaitingUser)
  const handleResume = useCallback(
    async (job: JobSummaryDto | JobDetailDto): Promise<boolean> => {
      if (job.state !== "AwaitingUser") {
        setError(
          createErrorDto(
            "INVALID_STATE",
            `Chỉ có thể tiếp tục (resume) tác vụ ở trạng thái 'AwaitingUser' (trạng thái hiện tại: ${job.state}).`
          )
        );
        return false;
      }

      if (actionLoadingRef.current.has(job.id)) {
        return false;
      }
      actionLoadingRef.current.add(job.id);
      setIsActionLoading((prev) => ({ ...prev, [job.id]: true }));
      try {
        const res = await jobApi.resume(job.id);
        setJobs((prevJobs) =>
          prevJobs.map((j) =>
            j.id === job.id ? { ...j, state: res.state, reasonCode: null, revision: (j.revision ?? 1) + 1 } : j
          )
        );
        return true;
      } catch (err) {
        setError(err as ErrorDto);
        return false;
      } finally {
        actionLoadingRef.current.delete(job.id);
        setIsActionLoading((prev) => ({ ...prev, [job.id]: false }));
      }
    },
    []
  );

  // Hành động Đối soát an toàn (reconcile)
  const handleReconcile = useCallback(
    async (request: JobReconcileRequest): Promise<boolean> => {
      if (actionLoadingRef.current.has(request.jobId)) {
        return false;
      }
      actionLoadingRef.current.add(request.jobId);
      setIsActionLoading((prev) => ({ ...prev, [request.jobId]: true }));
      try {
        const res = await jobApi.reconcile(request);
        setJobs((prevJobs) =>
          prevJobs.map((j) => {
            if (j.id !== request.jobId) return j;
            const updated: JobItem = {
              ...j,
              state: res.currentState,
              resultReference: res.resultReference ?? j.resultReference,
              reasonCode: null,
              hasUnresolvedSideEffect: false,
              revision: res.revision ?? (j.revision ?? 1) + 1,
            };
            if (isTerminalState(res.currentState)) {
              completedHistoryRef.current.set(request.jobId, updated);
            }
            return updated;
          })
        );
        setReconcileJob(null);
        return true;
      } catch (err) {
        setError(err as ErrorDto);
        return false;
      } finally {
        actionLoadingRef.current.delete(request.jobId);
        setIsActionLoading((prev) => ({ ...prev, [request.jobId]: false }));
      }
    },
    []
  );

  // T17-R4: Yêu cầu mở trình duyệt đăng nhập thủ công cho profileId thực sự
  const handleRequestLogin = useCallback(
    async (profileId: string): Promise<boolean> => {
      if (!profileId || profileId.trim() === "") {
        setError(
          createErrorDto(
            "MISSING_PROFILE",
            "Tác vụ chưa được liên kết với hồ sơ trình duyệt (profileId). Vui lòng cấu hình tài khoản trong Cài đặt."
          )
        );
        return false;
      }

      try {
        await jobApi.requestLogin(profileId);
        return true;
      } catch (err) {
        setError(err as ErrorDto);
        return false;
      }
    },
    []
  );

  // T17-R2 & C2: Thử lại an toàn qua lệnh job.retry với ExpectedRevision (CAS)
  const handleRetry = useCallback(
    async (job: JobSummaryDto | JobDetailDto): Promise<boolean> => {
      // 1. Nếu tác vụ ở AwaitingUser: dùng resume
      if (job.state === "AwaitingUser") {
        return await handleResume(job);
      }

      // 2. Nếu tác vụ không phải Failed: không hỗ trợ retry trực tiếp
      if (job.state !== "Failed") {
        setError(
          createErrorDto(
            "INVALID_STATE",
            `Không thể thử lại tác vụ ở trạng thái '${job.state}'.`
          )
        );
        return false;
      }

      // 3. Hydrate metadata nếu summary hiện tại chưa có đầy đủ thông tin an toàn
      let sideEffect = job.sideEffectSubmitted;
      let cancelRequested = job.cancelRequested;
      let hasUnresolved = job.hasUnresolvedSideEffect;
      let currentRevision = job.revision;

      if (sideEffect === undefined || currentRevision === undefined) {
        try {
          const detail = await jobApi.getStatus(job.id);
          sideEffect = detail.sideEffectSubmitted;
          cancelRequested = detail.cancelRequested;
          hasUnresolved = detail.hasUnresolvedSideEffect;
          currentRevision = detail.revision;
        } catch (fetchErr) {
          setError(fetchErr as ErrorDto);
          return false;
        }
      }

      // T17-R2: Kiểm tra các điều kiện an toàn trước khi gửi retry
      if (cancelRequested) {
        setError(
          createErrorDto(
            "CANNOT_RETRY_CANCELED",
            "Không thể thử lại tác vụ đã bị hủy bỏ bởi người dùng."
          )
        );
        return false;
      }

      // Render MuseAI hết giờ chỉ tiếp tục chờ side chat đã lưu ở backend, không gửi lại nên được phép retry
      const resumableRender = job.reasonCode === "render_timeout" || job.reasonCode === "render_interrupted";
      if ((sideEffect === 1 || hasUnresolved) && !resumableRender) {
        setError(
          createErrorDto(
            "UNCERTAIN_RETRY_BLOCKED",
            "Tác vụ có tác dụng phụ chưa xác định (side-effect). Vui lòng sử dụng chức năng Đối soát để xử lý an toàn."
          )
        );
        return false;
      }

      if (actionLoadingRef.current.has(job.id)) {
        return false;
      }
      actionLoadingRef.current.add(job.id);
      setIsActionLoading((prev) => ({ ...prev, [job.id]: true }));

      try {
        // C2: Gọi job.retry với expectedRevision chuẩn xác
        const expectedRev = currentRevision ?? 1;
        const res = await jobApi.retry(job.id, expectedRev);

        setJobs((prevJobs) =>
          prevJobs.map((j) =>
            j.id === job.id
              ? {
                  ...j,
                  state: res.state,
                  reasonCode: null,
                  revision: res.revision,
                }
              : j
          )
        );
        return true;
      } catch (err) {
        setError(err as ErrorDto);
        return false;
      } finally {
        actionLoadingRef.current.delete(job.id);
        setIsActionLoading((prev) => ({ ...prev, [job.id]: false }));
      }
    },
    [handleResume]
  );

  // Thống kê số lượng theo tab (kết hợp active và history)
  const tabCounts = useMemo<Record<QueueTabFilter, number>>(() => {
    const counts: Record<QueueTabFilter, number> = {
      all: jobs.length,
      running: 0,
      awaiting: 0,
      failed: 0,
      completed: 0,
    };
    for (const job of jobs) {
      if (job.state === "Running" || job.state === "Reserved") {
        counts.running++;
      } else if (job.state === "AwaitingUser" || job.state === "Queued") {
        counts.awaiting++;
      } else if (job.state === "Failed") {
        counts.failed++;
      } else if (job.state === "Succeeded" || job.state === "Canceled") {
        counts.completed++;
      }
    }
    // Bổ sung số lượng từ historyJobs nếu chưa có trong counts.completed
    if (historyJobs.length > 0) {
      const activeIds = new Set(jobs.map((j) => j.id));
      for (const hj of historyJobs) {
        if (!activeIds.has(hj.id)) {
          counts.completed++;
        }
      }
    }
    return counts;
  }, [jobs, historyJobs]);

  // Danh sách đã lọc theo activeTab và searchQuery (T17-R6: tab completed lấy từ historyJobs)
  const filteredJobs = useMemo(() => {
    const query = searchQuery.trim().toLowerCase();
    let baseList: JobItem[];

    if (activeTab === "completed") {
      const map = new Map<string, JobItem>();
      for (const h of historyJobs) {
        map.set(h.id, h);
      }
      for (const j of jobs) {
        if (j.state === "Succeeded" || j.state === "Canceled") {
          map.set(j.id, j);
        }
      }
      baseList = Array.from(map.values()).sort(
        (a, b) => new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime()
      );
    } else {
      baseList = jobs;
    }

    return baseList.filter((job) => {
      if (!matchesTab(job.state, activeTab)) {
        return false;
      }
      if (!query) {
        return true;
      }
      const matchId = job.id.toLowerCase().includes(query);
      const matchType = job.taskType.toLowerCase().includes(query);
      const matchStep = job.currentStep?.toLowerCase().includes(query) ?? false;
      const matchReason = job.reasonCode?.toLowerCase().includes(query) ?? false;
      const matchState = job.state.toLowerCase().includes(query);
      return matchId || matchType || matchStep || matchReason || matchState;
    });
  }, [jobs, historyJobs, activeTab, searchQuery]);

  return {
    jobs,
    filteredJobs,
    isLoading,
    error,
    activeTab,
    searchQuery,
    selectedJobId,
    selectedJobDetail,
    reconcileJob,
    isActionLoading,
    tabCounts,
    setActiveTab,
    setSearchQuery,
    setSelectedJobId,
    setReconcileJob,
    clearError,
    refresh,
    handleCancel,
    handleResume,
    handleReconcile,
    handleRequestLogin,
    handleRetry,
    fetchJobDetail,
  };
}
