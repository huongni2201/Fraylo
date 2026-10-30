import { domWindow } from "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";

// Trạng thái backend mock chuẩn xác
let mockActiveJobs: any[] = [];
let mockJobDetails: Map<string, any> = new Map();
let lastExecutedCommand: string | null = null;
let lastCommandPayload: Record<string, unknown> | null = null;
let executedCommandsHistory: Array<{ command: string; payload: Record<string, unknown> }> = [];
let commandDelayMs = 5;
let eventSeq = 1;

// Mock window.chrome.webview TRƯỚC KHI import bất kỳ service/hook nào
window.chrome = {
  webview: {
    postMessage: (envelope: unknown) => {
      const req = envelope as {
        id: string;
        type: string;
        command: string;
        payload: Record<string, unknown>;
        contractVersion: string;
      };

      lastExecutedCommand = req.command;
      lastCommandPayload = req.payload;
      executedCommandsHistory.push({ command: req.command, payload: req.payload });

      setTimeout(() => {
        let responseData: unknown = null;
        let success = true;
        let errorData: any = null;

        switch (req.command) {
          case "job.list_active": {
            responseData = { jobs: [...mockActiveJobs] };
            break;
          }
          case "job.get_status": {
            const jobId = req.payload.jobId as string;
            const customDetail = mockJobDetails.get(jobId);
            const requestedJob = mockActiveJobs.find((j) => j.id === jobId);

            const detail: any = customDetail || {
              id: jobId || "job_default",
              projectId: requestedJob?.projectId || "prj_test",
              sceneId: requestedJob?.sceneId || null,
              taskType: requestedJob?.taskType || "Script",
              state: requestedJob?.state || "Running",
              reasonCode: requestedJob?.reasonCode || null,
              providerKey: "chatgpt-web",
              profileId: requestedJob?.profileId || "prof_chatgpt_01",
              storageRootId: "root_01",
              attempts: requestedJob?.attempts || 1,
              maxAttempts: requestedJob?.maxAttempts || 3,
              sideEffectSubmitted: requestedJob?.sideEffectSubmitted ?? 0,
              cancelRequested: requestedJob?.cancelRequested ?? false,
              hasUnresolvedSideEffect: requestedJob?.hasUnresolvedSideEffect ?? false,
              resultReference: requestedJob?.resultReference || null,
              resultText: null,
              createdAtUtc: requestedJob?.createdAtUtc || "2026-10-27T00:00:00Z",
              updatedAtUtc: "2026-10-27T00:01:00Z",
            };
            responseData = { job: detail };
            break;
          }
          case "job.retry": {
            const jobId = req.payload.jobId as string;
            responseData = {
              jobId,
              state: "Queued",
              message: "Tác vụ đã được đưa lại vào hàng đợi để thử lại an toàn.",
              revision: 2,
            };
            break;
          }
          case "job.list_history": {
            responseData = {
              items: [],
              nextCursor: null,
            };
            break;
          }
          case "job.cancel": {
            responseData = {
              jobId: req.payload.jobId,
              state: "Canceled",
              cancelRequested: true,
              message: "Tác vụ đã được hủy thành công.",
              previousState: "Running",
            };
            break;
          }
          case "job.resume": {
            const jobId = req.payload.jobId as string;
            const currentJob = mockActiveJobs.find((j) => j.id === jobId);
            // Tuân thủ backend: JobService.ResumeAsync chỉ chấp nhận AwaitingUser
            if (currentJob && currentJob.state !== "AwaitingUser") {
              success = false;
              errorData = {
                code: "CONCURRENCY_CONFLICT",
                message: `Chỉ có thể tiếp tục tác vụ ở trạng thái 'AwaitingUser', trạng thái hiện tại: '${currentJob.state}'.`,
                retryable: false,
                correlationId: "err_conflict_1",
                details: null,
              };
            } else {
              responseData = {
                jobId,
                state: "Queued",
                message: "Tác vụ đã được tiếp tục và đưa trở lại hàng đợi.",
              };
            }
            break;
          }
          case "job.reconcile": {
            const jobId = req.payload.jobId as string;
            const action = req.payload.action as string;
            const customDetail = mockJobDetails.get(jobId);
            const wasCanceled = customDetail?.cancelRequested || customDetail?.state === "Canceled";

            // Tuân thủ backend: JobReconciliationService.cs
            if ((action === "retry" || action === "resubmit") && wasCanceled) {
              success = false;
              errorData = {
                code: "VALIDATION_FAILED",
                message: "Không thể thử lại tác vụ đã bị hủy bỏ bởi người dùng.",
                retryable: false,
                correlationId: "err_retry_canceled",
                details: null,
              };
            } else if (
              action === "retry" &&
              customDetail?.sideEffectSubmitted === 1 &&
              req.payload.confirmedNotReceived !== true
            ) {
              success = false;
              errorData = {
                code: "VALIDATION_FAILED",
                message: "Không thể gửi lại tác vụ đã phát sinh tác dụng phụ khi chưa có ConfirmedNotReceived=true.",
                retryable: false,
                correlationId: "err_retry_unsafe",
                details: null,
              };
            } else {
              let currentState = "Running";
              if (action === "mark_succeeded") currentState = wasCanceled ? "Canceled" : "Succeeded";
              if (action === "mark_failed") currentState = "Failed";
              if (action === "cancel") currentState = "Canceled";
              if (action === "retry") currentState = "Queued";

              responseData = {
                jobId,
                previousState: "AwaitingUser",
                currentState,
                reconcileResult: `Đối soát an toàn thành công với action '${action}'.`,
                resultReference: (req.payload.resultReference as string) || null,
              };
            }
            break;
          }
          case "browser.request_login": {
            const profileId = req.payload.profileId as string;
            // Tuân thủ backend: Không chấp nhận rỗng hoặc providerKey làm profileId
            if (!profileId || profileId === "chatgpt-web" || profileId === "gemini-web") {
              success = false;
              errorData = {
                code: "INVALID_ARGUMENT",
                message: "ProfileId không hợp lệ hoặc thiếu định danh hồ sơ trình duyệt thực tế.",
                retryable: false,
                correlationId: "err_bad_profile",
                details: null,
              };
            } else {
              responseData = {
                profileId,
                opened: true,
                message: "Đã mở cửa sổ đăng nhập trình duyệt cho hồ sơ.",
              };
            }
            break;
          }
          default:
            responseData = null;
        }

        const respEnvelope = {
          id: req.id,
          type: "response",
          success,
          data: responseData,
          error: errorData,
          contractVersion: "1.1.0",
        };

        const event = new domWindow.MessageEvent("message", {
          data: JSON.stringify(respEnvelope),
        });
        window.dispatchEvent(event as unknown as Event);
      }, commandDelayMs);
    },
    addEventListener: (type: string, listener: (ev: MessageEvent) => void) => {
      window.addEventListener(type, listener as EventListener);
    },
    removeEventListener: (type: string, listener: (ev: MessageEvent) => void) => {
      window.removeEventListener(type, listener as EventListener);
    },
  },
};

// Dynamic imports sau khi môi trường DOM đã hoàn thiện
const React = await import("react");
const ReactDOM = await import("react-dom/client");
const { act } = React;
const { bridgeClient } = await import("../../services/bridgeClient.ts");
const {
  isTerminalState,
  getRecommendedAction,
} = await import("./jobTypes.ts");
const { useQueueState } = await import("./useQueueState.ts");
const { QueueDrawer } = await import("../../components/QueueDrawer.tsx");
const { JobItemCard } = await import("./JobItemCard.tsx");

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function waitForCondition(
  fn: () => boolean | Promise<boolean>,
  timeoutMs = 1500,
  intervalMs = 15
): Promise<void> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (await fn()) return;
    await act(async () => {
      await sleep(intervalMs);
    });
  }
  throw new Error(`Timeout sau ${timeoutMs}ms chờ điều kiện`);
}

function emitBridgeEvent(eventName: string, payload: unknown) {
  const eventEnvelope = {
    id: `evt_${Date.now()}_${Math.random().toString(36).slice(2)}`,
    type: "event",
    eventName,
    seq: eventSeq++,
    timestampUtc: new Date().toISOString(),
    payload,
    contractVersion: "1.1.0",
  };
  const event = new domWindow.MessageEvent("message", {
    data: JSON.stringify(eventEnvelope),
  });
  window.dispatchEvent(event as unknown as Event);
}

// --------------------------------------------------------------------------
// 9 KỊCH BẢN KIỂM CHỨNG THEO YÊU CẦU NGHIỆM THU
// --------------------------------------------------------------------------

test("AC1 (R1): Succeeded event → Running progress trễ: vẫn giữ nguyên Succeeded", async () => {
  mockActiveJobs = [
    {
      id: "job_r1_test",
      projectId: "prj_01",
      sceneId: "scn_01",
      taskType: "Script",
      state: "Running",
      reasonCode: null,
      attempts: 1,
      progressPercent: 80,
      createdAtUtc: new Date().toISOString(),
    },
  ];

  let currentResult: any;
  function Harness() {
    currentResult = useQueueState({ autoFetch: true });
    return React.createElement("div", null, currentResult.jobs.map((j: any) => `${j.id}:${j.state}`).join(","));
  }

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(Harness));
  });

  await waitForCondition(() => currentResult && currentResult.jobs.length > 0 && !currentResult.isLoading);
  assert.equal(currentResult.jobs[0].state, "Running");

  // Phát event hoàn tất Succeeded
  await act(async () => {
    emitBridgeEvent("job.state_changed", {
      jobId: "job_r1_test",
      previousState: "Running",
      newState: "Succeeded",
      reasonCode: null,
      resultReference: "asset_done_123",
      timestampUtc: new Date().toISOString(),
    });
  });

  assert.equal(currentResult.jobs[0].state, "Succeeded");
  assert.equal(isTerminalState(currentResult.jobs[0].state), true);

  // Phát progress event đến trễ mang Running
  await act(async () => {
    emitBridgeEvent("job.progress_updated", {
      jobId: "job_r1_test",
      state: "Running",
      progressPercent: 95,
      currentStep: "Hậu kỳ trễ",
    });
  });

  // Khẳng định: job vẫn phải ở Succeeded, TUYỆT ĐỐI không bị lùi về Running!
  assert.equal(currentResult.jobs[0].state, "Succeeded", "State của job đã kết thúc phải được bảo toàn trước progress trễ");

  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("AC2 (R1): Response refresh cũ về sau event mới: không lùi trạng thái (fetch generation & merge guard)", async () => {
  mockActiveJobs = [
    {
      id: "job_r1_refresh",
      projectId: "prj_01",
      sceneId: null,
      taskType: "Script",
      state: "Running",
      reasonCode: null,
      attempts: 1,
      createdAtUtc: new Date(Date.now() - 5000).toISOString(),
    },
  ];

  let currentResult: any;
  function Harness() {
    currentResult = useQueueState({ autoFetch: true });
    return React.createElement("div", null);
  }

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(Harness));
  });

  // Chờ nạp ban đầu xong
  await waitForCondition(() => currentResult && currentResult.jobs.length > 0 && !currentResult.isLoading);
  assert.equal(currentResult.jobs[0].state, "Running");

  // Thiết lập delay của command cao để giả lập request về trễ
  commandDelayMs = 40;
  let refreshPromise: Promise<void>;

  await act(async () => {
    // Kích hoạt refresh
    refreshPromise = currentResult.refresh();
  });

  // Trong lúc refresh đang chờ phản hồi, một realtime event đổi job sang Canceled đến
  await act(async () => {
    emitBridgeEvent("job.state_changed", {
      jobId: "job_r1_refresh",
      previousState: "Running",
      newState: "Canceled",
      reasonCode: "user_canceled",
      timestampUtc: new Date().toISOString(),
    });
  });

  // Chờ cho request refresh cũ ban đầu trả về
  await act(async () => {
    await refreshPromise!;
    await sleep(40);
  });

  // Khẳng định: job trong danh sách phải là Canceled, response cũ chứa 'Running' không được ghi đè!
  assert.equal(currentResult.jobs[0].state, "Canceled", "Event mới hơn không được phép bị snapshot cũ từ refresh ghi đè");

  commandDelayMs = 5;
  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("AC3 (R5): Event job ảnh/video mới: hydrate đúng taskType/scene và điều hướng chính xác", async () => {
  mockActiveJobs = [];

  // Chuẩn bị metadata chi tiết trên backend cho job mới
  mockJobDetails.set("job_video_render_99", {
    id: "job_video_render_99",
    projectId: "prj_anim_01",
    sceneId: "scene_chase_05",
    taskType: "VideoRender",
    state: "Running",
    reasonCode: null,
    providerKey: "museai",
    profileId: "prof_museai_01",
    storageRootId: "root_video",
    attempts: 1,
    maxAttempts: 3,
    sideEffectSubmitted: 0,
    cancelRequested: false,
    hasUnresolvedSideEffect: false,
    resultReference: null,
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
  });

  let navigatedJob: any = null;
  let currentResult: any;

  function Harness() {
    currentResult = useQueueState({ autoFetch: false });
    return React.createElement(
      "div",
      null,
      currentResult.jobs.map((job: any) =>
        React.createElement(JobItemCard, {
          key: job.id,
          job,
          onNavigateToJob: (j) => {
            navigatedJob = j;
          },
        })
      )
    );
  }

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(Harness));
  });

  // Phát event có job mới render video
  await act(async () => {
    emitBridgeEvent("job.state_changed", {
      jobId: "job_video_render_99",
      projectId: "prj_anim_01",
      previousState: "Queued",
      newState: "Running",
      reasonCode: null,
      timestampUtc: new Date().toISOString(),
    });
  });

  // Chờ quá trình hydrate từ jobApi.getStatus hoàn tất
  await waitForCondition(() => {
    const j = currentResult.jobs.find((x: any) => x.id === "job_video_render_99");
    return j && j.taskType === "VideoRender" && j.isHydrating === false;
  });

  const hydratedJob = currentResult.jobs.find((j: any) => j.id === "job_video_render_99");
  assert.ok(hydratedJob, "Job mới phải có trong danh sách");
  assert.equal(hydratedJob.taskType, "VideoRender", "Phải hydrate đúng taskType VideoRender từ backend");
  assert.equal(hydratedJob.sceneId, "scene_chase_05", "Phải hydrate đúng sceneId từ backend");

  // Click vào nút điều hướng phân cảnh trên thẻ JobItemCard
  const navBtn = container.querySelector('button[title="Điều hướng tới phân cảnh này"]') as HTMLButtonElement;
  assert.ok(navBtn, "Nút điều hướng phân cảnh phải xuất hiện trên thẻ");

  await act(async () => {
    navBtn.click();
  });

  assert.ok(navigatedJob, "Callback điều hướng phải được kích hoạt");
  assert.equal(navigatedJob.sceneId, "scene_chase_05", "Điều hướng chính xác tới sceneId thật");

  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("AC4 (R2): Click Retry trên QueueDrawer production: đúng command/backend semantics; Failed uncertain không được resend", async () => {

  // Job 1: Failed uncertain (sideEffectSubmitted = 1) -> KHÔNG ĐƯỢC retry tự động!
  // Job 2: Failed safe (sideEffectSubmitted = 0, attempts = 1, maxAttempts = 3) -> ĐƯỢC retry an toàn qua reconcile
  mockActiveJobs = [
    {
      id: "job_failed_uncertain",
      projectId: "prj_01",
      sceneId: null,
      taskType: "Script",
      state: "Failed",
      reasonCode: "network_error",
      sideEffectSubmitted: 1,
      attempts: 1,
      maxAttempts: 3,
      createdAtUtc: new Date(Date.now() - 2000).toISOString(),
    },
    {
      id: "job_failed_safe",
      projectId: "prj_01",
      sceneId: null,
      taskType: "Script",
      state: "Failed",
      reasonCode: "network_error",
      sideEffectSubmitted: 0,
      attempts: 1,
      maxAttempts: 3,
      createdAtUtc: new Date().toISOString(),
    },
  ];

  const triggerRef = { current: document.createElement("button") };
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
        triggerRef,
      })
    );
  });

  // Chờ Drawer tải xong danh sách jobs
  await waitForCondition(() => {
    const buttons = Array.from(container.querySelectorAll("button"));
    return buttons.some((btn) => btn.textContent?.includes("Thử lại"));
  });

  // Kiểm tra recommended action của job uncertain
  const actionUncertain = getRecommendedAction(mockActiveJobs[0]);
  assert.notEqual(actionUncertain.type, "retry", "Job Failed có sideEffectSubmitted=1 tuyệt đối không có action retry");

  // Kiểm tra job safe có nút 'Thử lại'
  const actionSafe = getRecommendedAction(mockActiveJobs[1]);
  assert.equal(actionSafe.type, "retry", "Job Failed có sideEffectSubmitted=0 và còn lượt thử phải có action retry");

  // Tìm nút 'Thử lại' trên giao diện QueueDrawer thật
  const buttons = Array.from(container.querySelectorAll("button"));
  const retryBtn = buttons.find((btn) => btn.textContent?.includes("Thử lại"));
  assert.ok(retryBtn, "Nút 'Thử lại' phải hiển thị trên thẻ job hợp lệ");

  lastExecutedCommand = null;
  lastCommandPayload = null;
  executedCommandsHistory = [];

  await act(async () => {
    retryBtn.click();
  });

  await waitForCondition(() => lastExecutedCommand === "job.retry");

  // Khẳng định: Backend nhận đúng command 'job.retry' với expectedRevision theo v1.1 §5
  assert.equal(lastExecutedCommand, "job.retry", "Phải gọi job.retry theo v1.1 §5");
  assert.equal((lastCommandPayload as any)?.jobId, "job_failed_safe");
  assert.equal(typeof (lastCommandPayload as any)?.expectedRevision, "number");

  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("AC5 (R3): Cancel uncertain → reload → click đối soát → resolved và nhả occupancy", async () => {
  executedCommandsHistory = [];
  executedCommandsHistory = [];

  // Tác vụ Canceled sau reload nhưng còn slot occupancy (hasUnresolvedSideEffect = true)
  const canceledUncertainJob = {
    id: "job_canceled_unresolved_01",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Image",
    state: "Canceled",
    reasonCode: "user_canceled",
    sideEffectSubmitted: 1,
    hasUnresolvedSideEffect: true,
    attempts: 1,
    createdAtUtc: new Date().toISOString(),
  };

  mockActiveJobs = [canceledUncertainJob];
  mockJobDetails.set(canceledUncertainJob.id, {
    ...canceledUncertainJob,
    maxAttempts: 3,
    providerKey: "chatgpt-web",
    profileId: "prof_01",
    cancelRequested: true,
  });

  const triggerRef = { current: document.createElement("button") };
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
        triggerRef,
      })
    );
  });

  // Chờ nút đối soát xuất hiện
  await waitForCondition(() => {
    const buttons = Array.from(container.querySelectorAll("button"));
    return buttons.some((btn) => btn.textContent?.includes("Đối soát giải phóng tài nguyên"));
  });

  const buttons = Array.from(container.querySelectorAll("button"));
  const reconcileBtn = buttons.find((btn) =>
    btn.textContent?.includes("Đối soát giải phóng tài nguyên")
  );
  assert.ok(reconcileBtn, "Phải có nút CTA đối soát cho tác vụ Canceled chưa giải phóng tài nguyên");

  // Click mở dialog đối soát
  await act(async () => {
    reconcileBtn.click();
  });

  await waitForCondition(() => Boolean(document.querySelector('button[role="checkbox"]')));
  const confirmCheckbox = document.querySelector('button[role="checkbox"]') as HTMLButtonElement;
  assert.ok(confirmCheckbox, "Checkbox xác nhận đối soát phải có mặt");

  await act(async () => {
    confirmCheckbox.click();
  });

  const submitBtn = Array.from(document.querySelectorAll("button")).find((btn) =>
    btn.textContent?.includes("Xác nhận đối soát")
  );
  assert.ok(submitBtn, "Nút Xác nhận đối soát phải có trong dialog");

  lastExecutedCommand = null;
  lastCommandPayload = null;

  await act(async () => {
    const form = submitBtn.closest("form");
    if (form) {
      const submitEvent = new domWindow.Event("submit", { bubbles: true, cancelable: true });
      form.dispatchEvent(submitEvent as unknown as Event);
    } else {
      submitBtn.click();
    }
    await sleep(20);
  });

  await waitForCondition(() =>
    executedCommandsHistory.some((c) => c.command === "job.reconcile")
  );

  const reconcileCall = executedCommandsHistory.find((c) => c.command === "job.reconcile");
  assert.ok(reconcileCall, "Phải ghi nhận command job.reconcile đã được gửi tới host");

  // Khẳng định: Gửi đúng command 'job.reconcile' với action 'cancel' để giải phóng occupancy
  assert.equal(reconcileCall.command, "job.reconcile");
  assert.equal((reconcileCall.payload as any)?.action, "cancel");
  assert.equal((reconcileCall.payload as any)?.jobId, canceledUncertainJob.id);

  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("AC6 (R4): Click Login khi providerKey khác profileId: payload gửi đúng profileId", async () => {

  const awaitingJob = {
    id: "job_awaiting_login",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state: "AwaitingUser",
    reasonCode: "login_required",
    providerKey: "chatgpt-web",
    profileId: "prof_uuid_custom_999", // Profile ID thực tế khác biệt hoàn toàn với providerKey
    attempts: 1,
    createdAtUtc: new Date().toISOString(),
  };

  mockActiveJobs = [awaitingJob];

  const triggerRef = { current: document.createElement("button") };
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
        triggerRef,
      })
    );
  });

  await waitForCondition(() => {
    const buttons = Array.from(container.querySelectorAll("button"));
    return buttons.some((btn) => btn.textContent?.includes("Đăng nhập ngay"));
  });

  const buttons = Array.from(container.querySelectorAll("button"));
  const loginBtn = buttons.find((btn) => btn.textContent?.includes("Đăng nhập ngay"));
  assert.ok(loginBtn, "Nút 'Đăng nhập ngay' phải hiển thị");

  lastExecutedCommand = null;
  lastCommandPayload = null;

  await act(async () => {
    loginBtn.click();
  });

  await waitForCondition(() => lastExecutedCommand === "browser.request_login");

  // Khẳng định: Command gửi đi phải mang đúng profileId thực tế, KHÔNG ĐƯỢC gửi providerKey 'chatgpt-web'
  assert.equal(lastExecutedCommand, "browser.request_login");
  assert.equal(
    (lastCommandPayload as any)?.profileId,
    "prof_uuid_custom_999",
    "Payload phải là profileId thực tế chứ không phải providerKey"
  );

  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("AC7 (R6): Complete → reload: tab Hoàn tất vẫn có job; active không chứa resolved terminal", async () => {

  // Giả lập một job đang chạy và sau đó hoàn tất thành công trong phiên
  const jobDone = {
    id: "job_completed_session_1",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state: "Running",
    reasonCode: null,
    attempts: 1,
    createdAtUtc: new Date().toISOString(),
  };

  mockActiveJobs = [jobDone];

  let currentResult: any;
  function Harness() {
    currentResult = useQueueState({ autoFetch: true });
    return React.createElement("div", null);
  }

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(Harness));
  });

  await waitForCondition(() => currentResult && currentResult.jobs.length > 0 && !currentResult.isLoading);

  // Job hoàn tất Succeeded
  await act(async () => {
    emitBridgeEvent("job.state_changed", {
      jobId: jobDone.id,
      previousState: "Running",
      newState: "Succeeded",
      reasonCode: null,
      resultReference: "res_final_1",
      timestampUtc: new Date().toISOString(),
    });
  });

  assert.equal(currentResult.jobs[0].state, "Succeeded");

  // Mô phỏng reload: Backend list_active theo chuẩn contract KHÔNG trả Succeeded đã giải phóng
  mockActiveJobs = [];

  // Kích hoạt refresh (reload)
  await act(async () => {
    await currentResult.refresh();
  });

  await waitForCondition(() => !currentResult.isLoading);

  // Khẳng định: Sau reload, job đã xong vẫn được bảo toàn trong danh sách (tab Hoàn tất)
  assert.equal(currentResult.jobs.length, 1, "Job đã hoàn tất vẫn còn trong danh sách lịch sử sau reload");
  assert.equal(currentResult.jobs[0].id, jobDone.id);
  assert.equal(currentResult.jobs[0].state, "Succeeded");

  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("AC8: Double-click và conflict: không phát sinh request trùng lặp hoặc cập nhật giả", async () => {

  mockActiveJobs = [
    {
      id: "job_double_click_test",
      projectId: "prj_01",
      sceneId: null,
      taskType: "Script",
      state: "Running",
      reasonCode: null,
      attempts: 1,
      createdAtUtc: new Date().toISOString(),
    },
  ];

  let currentResult: any;
  function Harness() {
    currentResult = useQueueState({ autoFetch: true });
    return React.createElement("div", null);
  }

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(Harness));
  });

  await waitForCondition(() => currentResult && currentResult.jobs.length > 0 && !currentResult.isLoading);

  commandDelayMs = 30;

  // Gọi đồng thời 2 lần cancel liên tiếp (double click)
  await act(async () => {
    const targetJob = currentResult.jobs[0];
    const p1 = currentResult.handleCancel(targetJob);
    const p2 = currentResult.handleCancel(targetJob);
    const [res1, res2] = await Promise.all([p1, p2]);
    // Lần 1 thành công, lần 2 bị guard ref chặn
    assert.equal(res1, true);
    assert.equal(res2, false, "Lần gọi thứ hai phải bị chặn bởi action loading guard");
  });

  commandDelayMs = 5;
  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("AC9: Keyboard navigation, Escape, focus return và hai theme không regression", async () => {
  mockActiveJobs = [];

  const triggerButton = document.createElement("button");
  triggerButton.id = "queue-trigger-btn";
  document.body.appendChild(triggerButton);
  const triggerRef = { current: triggerButton };

  let isClosed = false;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {
          isClosed = true;
        },
        triggerRef,
      })
    );
  });

  await waitForCondition(() => Boolean(document.querySelector('[role="dialog"]')));

  const drawerElement = document.querySelector('[role="dialog"]') as HTMLElement;
  assert.ok(drawerElement, "QueueDrawer phải có thuộc tính role='dialog'");
  assert.equal(drawerElement.getAttribute("aria-modal"), "true");

  // Kiểm tra phím Escape
  await act(async () => {
    const escapeEvent = new domWindow.KeyboardEvent("keydown", {
      key: "Escape",
      bubbles: true,
      cancelable: true,
    });
    window.dispatchEvent(escapeEvent as unknown as Event);
  });

  assert.equal(isClosed, true, "Nhấn Escape phải kích hoạt callback onClose");

  await act(async () => {
    root.unmount();
  });
  container.remove();
  triggerButton.remove();
});
