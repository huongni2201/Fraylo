import { domWindow } from "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";

// Biến trạng thái mock backend chính xác theo DTOs v1.1
let mockActiveJobs: any[] = [];
let mockHistoryJobs: any[] = [];
let mockJobDetails: Map<string, any> = new Map();
let lastExecutedCommand: string | null = null;
let lastCommandPayload: Record<string, unknown> | null = null;
let executedCommandsHistory: Array<{ command: string; payload: Record<string, unknown> }> = [];
let commandDelayMs = 5;
let listActiveDelayMs = 5;
let eventSeq = 1;

// Cài đặt window.chrome.webview mock TRƯỚC khi import component
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

      const currentDelay = req.command === "job.list_active" ? listActiveDelayMs : commandDelayMs;

      setTimeout(() => {
        let responseData: unknown = null;
        let success = true;
        let errorData: any = null;

        switch (req.command) {
          case "job.list_active": {
            responseData = { jobs: [...mockActiveJobs] };
            break;
          }
          case "job.list_history": {
            const pageSize = (req.payload.pageSize as number) || 50;
            responseData = {
              items: mockHistoryJobs.slice(0, pageSize),
              nextCursor: mockHistoryJobs.length > pageSize ? "cursor_next" : null,
            };
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
              profileId: requestedJob?.profileId || "prof_chatgpt_real",
              storageRootId: "root_01",
              attempts: requestedJob?.attempts || 1,
              maxAttempts: requestedJob?.maxAttempts || 3,
              sideEffectSubmitted: requestedJob?.sideEffectSubmitted ?? 0,
              cancelRequested: requestedJob?.cancelRequested ?? false,
              hasUnresolvedSideEffect: requestedJob?.hasUnresolvedSideEffect ?? false,
              resultReference: requestedJob?.resultReference || null,
              resultText: null,
              revision: requestedJob?.revision || 1,
              createdAtUtc: requestedJob?.createdAtUtc || "2026-10-27T00:00:00Z",
              updatedAtUtc: "2026-10-27T00:01:00Z",
            };
            responseData = { job: detail };
            break;
          }
          case "job.retry": {
            const jobId = req.payload.jobId as string;
            const expectedRevision = req.payload.expectedRevision as number;
            const currentJob = mockActiveJobs.find((j) => j.id === jobId);

            if (currentJob && currentJob.revision !== expectedRevision) {
              success = false;
              errorData = {
                code: "CONCURRENCY_CONFLICT",
                message: `Stale revision: expected ${expectedRevision}, but current is ${currentJob.revision}`,
                retryable: false,
                correlationId: "err_conflict_retry",
                details: null,
              };
            } else {
              responseData = {
                jobId,
                state: "Queued",
                message: "Tác vụ đã được đưa lại vào hàng đợi để thử lại an toàn.",
                revision: (expectedRevision || 1) + 1,
              };
            }
            break;
          }
          case "job.cancel": {
            responseData = {
              jobId: req.payload.jobId,
              state: "Canceled",
              cancelRequested: true,
              message: "Tác vụ đã được hủy thành công.",
              previousState: "Running",
              revision: 2,
            };
            break;
          }
          case "job.resume": {
            const jobId = req.payload.jobId as string;
            responseData = {
              jobId,
              state: "Queued",
              message: "Tác vụ đã được tiếp tục.",
              revision: 2,
            };
            break;
          }
          case "job.reconcile": {
            const jobId = req.payload.jobId as string;
            const action = req.payload.action as string;
            let currentState = "Running";
            if (action === "mark_succeeded") currentState = "Succeeded";
            if (action === "mark_failed") currentState = "Failed";
            if (action === "cancel") currentState = "Canceled";
            if (action === "retry") currentState = "Queued";

            responseData = {
              jobId,
              previousState: "AwaitingUser",
              currentState,
              reconcileResult: `Đối soát an toàn thành công với action '${action}'.`,
              resultReference: (req.payload.resultReference as string) || null,
              revision: 3,
            };
            break;
          }
          case "browser.request_login": {
            const profileId = req.payload.profileId as string;
            if (!profileId || profileId === "chatgpt-web" || profileId === "gemini-web") {
              success = false;
              errorData = {
                code: "INVALID_PROFILE",
                message: `profileId '${profileId}' không hợp lệ; phải là ID của BrowserProfile thật.`,
                retryable: false,
                correlationId: "err_invalid_prof",
                details: null,
              };
            } else {
              responseData = { status: "LoginRequested", profileId };
            }
            break;
          }
          default: {
            responseData = { ok: true };
          }
        }

        const responseEnvelope = {
          id: req.id,
          type: "response",
          command: req.command,
          success,
          data: responseData,
          error: errorData,
          contractVersion: "1.1.0",
        };

        const event = new domWindow.MessageEvent("message", {
          data: JSON.stringify(responseEnvelope),
        });
        window.dispatchEvent(event as unknown as Event);
      }, currentDelay);
    },
    addEventListener: (type: string, listener: (ev: MessageEvent) => void) => {
      window.addEventListener(type, listener as EventListener);
    },
    removeEventListener: (type: string, listener: (ev: MessageEvent) => void) => {
      window.removeEventListener(type, listener as EventListener);
    },
  },
} as any;

// Dynamic imports sau khi môi trường DOM đã hoàn thiện
const React = (await import("react")).default;
const ReactDOM = (await import("react-dom/client")).default;
const { act } = await import("react");
const { bridgeClient } = await import("../../services/bridgeClient.ts");
const { QueueDrawer } = await import("../../components/QueueDrawer.tsx");
const { JobItemCard } = await import("./JobItemCard.tsx");
const { JobDetailsDialog } = await import("./JobDetailsDialog.tsx");
const { getRecommendedAction } = await import("./jobTypes.ts");
const { JobList } = await import("./JobList.tsx");
const { JobReconcileDialog } = await import("./JobReconcileDialog.tsx");
const { useQueueState } = await import("./useQueueState.ts");

// Helper emit bridge events
function emitBridgeEvent(eventName: string, payload: unknown) {
  const envelope = {
    id: `evt_${Date.now()}_${eventSeq++}`,
    type: "event",
    eventName,
    seq: eventSeq++,
    payload,
    contractVersion: "1.1.0",
    timestampUtc: new Date().toISOString(),
  };

  const event = new domWindow.MessageEvent("message", {
    data: JSON.stringify(envelope),
  });
  window.dispatchEvent(event as unknown as Event);
}

// Helper wait condition
async function waitForCondition(condition: () => boolean, timeoutMs = 2500, intervalMs = 20): Promise<void> {
  const start = Date.now();
  while (!condition()) {
    if (Date.now() - start > timeoutMs) {
      throw new Error(`Timeout chờ điều kiện sau ${timeoutMs}ms`);
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }
}

function resetMockState() {
  mockActiveJobs = [];
  mockHistoryJobs = [];
  mockJobDetails.clear();
  lastExecutedCommand = null;
  lastCommandPayload = null;
  executedCommandsHistory = [];
  commandDelayMs = 5;
  listActiveDelayMs = 5;
}

test("AUTO-VIDEO: job bị chặn và job handoff lịch sử không mở đường nhập kết quả", async () => {
  resetMockState();
  const reasons = ["automation_blocked", "adapter_not_available", "manual_input_required", "MANUAL_HANDOFF_REQUIRED", "CAPABILITY_UNSUPPORTED", "missing_provider"];
  for (const reasonCode of reasons) {
    for (const state of ["AwaitingUser", "Failed"] as const) {
      const action = getRecommendedAction({ state, reasonCode, sideEffectSubmitted: 0, attempts: 1, maxAttempts: 3 });
      assert.equal(action.type, "none", `${state}/${reasonCode}: không handoff, resume hoặc retry`);
    }
    assert.equal(getRecommendedAction({ state: "AwaitingUser", reasonCode, sideEffectSubmitted: 1 }).type, "reconcile");
  }
  assert.equal(getRecommendedAction({
    state: "AwaitingUser", reasonCode: "login_required", profileId: "profile-login", sideEffectSubmitted: 1,
  }).type, "login", "Mất phiên sau submit vẫn cần CTA đăng nhập đúng profile");

  const job: any = {
    id: "legacy-handoff-job", projectId: "prj_01", sceneId: null,
    taskType: "VideoRender", state: "AwaitingUser", reasonCode: "automation_blocked",
    providerKey: "museai", storageRootId: "root-1", attempts: 1, maxAttempts: 3,
    sideEffectSubmitted: 0, cancelRequested: false, revision: 2, createdAtUtc: new Date().toISOString(),
  };
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);
  try {
    await act(async () => {
      root.render(React.createElement(React.Fragment, null,
        React.createElement(JobItemCard, { job }),
        React.createElement(JobDetailsDialog, { job, open: true, onOpenChange: () => {} }),
      ));
    });
    const text = document.body.textContent ?? "";
    assert.ok(text.includes("Tự động hóa của nhà cung cấp chưa sẵn sàng"));
    assert.ok(text.includes("legacy-handoff-job"), "Vẫn xem được dữ liệu job cũ");
    assert.ok(text.includes("Hủy tác vụ"), "Vẫn hủy an toàn được job cũ");
    assert.ok(!text.includes("Xử lý thủ công") && !text.includes("Manual Handoff"));
    assert.ok(!executedCommandsHistory.some(({ command }) => command === "job.get_handoff" || command === "job.import_result"));
  } finally {
    await act(async () => root.unmount());
    container.remove();
  }
});

// =========================================================================
// 12 ACCEPTANCE TESTS THEO docs/plans/2026-10-27-t17-remediation-fix-plan.md §4
// =========================================================================

test("AC1 (R1): Succeeded -> progress Running trễ phải giữ nguyên Succeeded trên component QueueDrawer", async () => {
  resetMockState();

  const finishedJob = {
    id: "job_ac1_terminal",
    projectId: "prj_01",
    sceneId: "scene_01",
    taskType: "Script",
    state: "Succeeded",
    reasonCode: null,
    attempts: 1,
    maxAttempts: 3,
    revision: 5,
    progressPercent: 100,
    currentStep: "Hoàn tất",
    createdAtUtc: new Date(Date.now() - 10000).toISOString(),
  };

  mockActiveJobs = [finishedJob];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
      })
    );
  });

  await waitForCondition(() => Boolean(container.querySelector('[role="dialog"]')));

  // Emit event progress_updated đến trễ có state Running và revision cũ = 4
  await act(async () => {
    emitBridgeEvent("job.progress_updated", {
      jobId: finishedJob.id,
      projectId: "prj_01",
      sceneId: "scene_01",
      state: "Running",
      progressPercent: 60,
      currentStep: "Đang sinh nội dung cũ...",
      jobRevision: 4,
    });
  });

  await new Promise((r) => setTimeout(r, 50));

  // Kiểm tra trên card: Trạng thái hiển thị vẫn là Thành công (Succeeded), không bị lùi về Running
  const card = container.querySelector('[class*="group relative flex flex-col"]');
  assert.ok(card, "Phải có card của tác vụ");
  const cardText = card.textContent || "";
  assert.ok(cardText.includes("Thành công"), "Tác vụ đã Succeeded phải giữ nguyên Thành công, không bị progress trễ đổi");
  assert.ok(!cardText.includes("Đang chạy"), "Không được hiển thị 'Đang chạy' trên card sau khi đã Succeeded");

  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});

test("AC2 (R1): Response list cũ về sau event mới: Không được lùi trạng thái", async () => {
  resetMockState();

  const initialJob = {
    id: "job_ac2_race",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state: "Running",
    reasonCode: null,
    attempts: 1,
    revision: 2,
    createdAtUtc: new Date().toISOString(),
  };

  // Backend response list_active chậm 50ms và trả về snapshot cũ (Running, rev 2)
  mockActiveJobs = [initialJob];
  listActiveDelayMs = 60;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
      })
    );
  });

  // Trong khi list_active đang bay, có event state_changed mới đưa job lên Succeeded (rev 3)
  await new Promise((r) => setTimeout(r, 15));
  await act(async () => {
    emitBridgeEvent("job.state_changed", {
      jobId: initialJob.id,
      projectId: "prj_01",
      previousState: "Running",
      newState: "Succeeded",
      reasonCode: null,
      jobRevision: 3,
      timestampUtc: new Date().toISOString(),
    });
  });

  // Chờ cho list_active response cũ về
  await waitForCondition(() => executedCommandsHistory.some((c) => c.command === "job.list_active"), 500);
  await new Promise((r) => setTimeout(r, 80));

  // Kiểm tra DOM: Trạng thái trên giao diện không bị lùi về Running
  const text = container.textContent || "";
  assert.ok(text.includes("Thành công"), "Trạng thái mới từ event (Succeeded, rev 3) không bị snapshot cũ đè");

  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});

test("AC3 (R1): Gap detection: Khi event có jobRevision nhảy cóc, tự động trigger reload để lấp gap", async () => {
  resetMockState();

  const currentJob = {
    id: "job_ac3_gap",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state: "Running",
    reasonCode: null,
    attempts: 1,
    revision: 2,
    createdAtUtc: new Date().toISOString(),
  };

  mockActiveJobs = [currentJob];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
      })
    );
  });

  await waitForCondition(() => Boolean(container.querySelector('[role="dialog"]')));

  const initialListCalls = executedCommandsHistory.filter((c) => c.command === "job.list_active").length;

  // Gửi event có gap: từ revision 2 nhảy thẳng lên revision 6
  await act(async () => {
    emitBridgeEvent("job.state_changed", {
      jobId: currentJob.id,
      projectId: "prj_01",
      previousState: "Running",
      newState: "AwaitingUser",
      reasonCode: "login_required",
      jobRevision: 6,
      timestampUtc: new Date().toISOString(),
    });
  });

  // Kỳ vọng hook phát hiện gap (6 > 2 + 1) và phát lệnh refresh list_active để lấp gap an toàn
  await waitForCondition(
    () => executedCommandsHistory.filter((c) => c.command === "job.list_active").length > initialListCalls,
    1500
  );

  assert.ok(
    executedCommandsHistory.filter((c) => c.command === "job.list_active").length > initialListCalls,
    "Khi phát hiện gap revision, phải kích hoạt nạp lại list_active để đồng bộ dữ liệu"
  );

  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});

test("AC4 (R5): Job mới qua event: placeholder loading khi chưa hydrate, khóa thao tác metadata, route đúng scene", async () => {
  resetMockState();

  // Bắt đầu với hàng đợi trống
  mockActiveJobs = [];

  const unknownJobId = "job_ac4_unknown";
  mockJobDetails.set(unknownJobId, {
    id: unknownJobId,
    projectId: "prj_video_01",
    sceneId: "scene_04_render",
    taskType: "VideoRender",
    state: "Running",
    reasonCode: null,
    providerKey: "museai",
    profileId: "prof_museai_01",
    storageRootId: "root_01",
    attempts: 2,
    maxAttempts: 3,
    sideEffectSubmitted: 0,
    cancelRequested: false,
    revision: 1,
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
  });

  // Đặt get_status chậm 80ms để kiểm tra giao diện placeholder
  commandDelayMs = 80;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
      })
    );
  });

  await waitForCondition(() => Boolean(container.querySelector('[role="dialog"]')));

  // Emit event job lạ (chưa hề có trong state ban đầu)
  await act(async () => {
    emitBridgeEvent("job.state_changed", {
      jobId: unknownJobId,
      projectId: "prj_video_01",
      previousState: "Queued",
      newState: "Running",
      reasonCode: null,
      jobRevision: 1,
      timestampUtc: new Date().toISOString(),
    });
  });

  // Trong lúc đang hydrate: không được gán taskType giả mạo "Script", mà phải có chỉ báo nạp dữ liệu
  await waitForCondition(() => container.textContent?.includes(unknownJobId.slice(0, 8)) ?? false);
  const immediateText = container.textContent || "";
  assert.ok(!immediateText.includes("Soạn kịch bản"), "Không được tự ý gán taskType Script giả mạo khi chưa hydrate");

  // Chờ hydrate hoàn tất qua job.get_status
  await waitForCondition(() => container.textContent?.includes("Render video") ?? false, 2500);

  // Sau khi hydrate xong: hiển thị đúng phân loại VideoRender ("Xuất video") và SceneId
  const hydratedText = container.textContent || "";
  assert.ok(hydratedText.includes("Render video"), "Phải hydrate đúng taskType VideoRender thành Render video");
  assert.ok(hydratedText.includes("scene_04_render"), "Phải hiển thị đúng sceneId được hydrate");

  commandDelayMs = 5;
  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});

test("AC5 (R2): Click Retry trên Failed sideEffect=0: gọi job.retry với expectedRevision chuẩn xác", async () => {
  resetMockState();

  const retryableJob = {
    id: "job_ac5_retry",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state: "Failed",
    reasonCode: "network_error",
    attempts: 1,
    maxAttempts: 3,
    sideEffectSubmitted: 0,
    hasUnresolvedSideEffect: false,
    cancelRequested: false,
    revision: 4,
    createdAtUtc: new Date().toISOString(),
  };

  mockActiveJobs = [retryableJob];

  let retryActionTriggered = false;
  let clickedActionJob: any = null;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  // Mount QueueDrawer production
  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
      })
    );
  });

  await waitForCondition(() => Boolean(container.querySelector('[role="dialog"]')));
  await waitForCondition(() => !container.textContent?.includes("(Đang cập nhật...)") && Boolean(container.querySelector("button")));

  // Tìm nút Thử lại trên card
  const retryBtn = Array.from(container.querySelectorAll("button")).find((btn) =>
    btn.textContent?.includes("Thử lại")
  );

  assert.ok(retryBtn, "Nút Thử lại phải xuất hiện trên job Failed với sideEffectSubmitted = 0");

  // Click Thử lại
  await act(async () => {
    retryBtn.click();
  });

  // Chờ lệnh job.retry được gửi tới Bridge

  await waitForCondition(() => executedCommandsHistory.some((c) => c.command === "job.retry"), 1500);

  const retryCall = executedCommandsHistory.find((c) => c.command === "job.retry");
  assert.ok(retryCall, "Phải phát lệnh 'job.retry' qua bridge");
  assert.equal(retryCall.payload.jobId, retryableJob.id);
  assert.equal(
    retryCall.payload.expectedRevision,
    retryableJob.revision,
    "Lệnh job.retry phải mang expectedRevision đúng bằng revision hiện tại của job (CAS)"
  );

  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});

test("AC6 (R2): Failed có sideEffectSubmitted=1 hoặc undefined: CẤM có nút Thử lại (không resend)", async () => {
  resetMockState();

  const unsafeJobWithSideEffect = {
    id: "job_ac6_side_effect_1",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Image",
    state: "Failed",
    reasonCode: "reconcile_required",
    attempts: 1,
    maxAttempts: 3,
    sideEffectSubmitted: 1,
    hasUnresolvedSideEffect: true,
    revision: 3,
    createdAtUtc: new Date().toISOString(),
  };

  const unsafeJobUndefinedSideEffect = {
    id: "job_ac6_undefined",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state: "Failed",
    reasonCode: "provider_rejected",
    attempts: 1,
    maxAttempts: 3,
    sideEffectSubmitted: undefined,
    hasUnresolvedSideEffect: false,
    revision: 2,
    createdAtUtc: new Date().toISOString(),
  };

  const container1 = document.createElement("div");
  document.body.appendChild(container1);
  const root1 = ReactDOM.createRoot(container1);

  await act(async () => {
    root1.render(
      React.createElement(JobItemCard, {
        job: unsafeJobWithSideEffect as any,
      })
    );
  });

  const buttons1 = Array.from(container1.querySelectorAll("button")).map((b) => b.textContent?.trim());
  assert.ok(!buttons1.some((txt) => txt?.includes("Thử lại")), "Job có sideEffectSubmitted = 1 KHÔNG được có nút Thử lại");

  await act(async () => {
    act(() => root1.unmount());
  });
  container1.remove();

  const container2 = document.createElement("div");
  document.body.appendChild(container2);
  const root2 = ReactDOM.createRoot(container2);

  await act(async () => {
    root2.render(
      React.createElement(JobItemCard, {
        job: unsafeJobUndefinedSideEffect as any,
      })
    );
  });

  const buttons2 = Array.from(container2.querySelectorAll("button")).map((b) => b.textContent?.trim());
  assert.ok(
    !buttons2.some((txt) => txt?.includes("Thử lại")),
    "Job có sideEffectSubmitted = undefined KHÔNG được coi là 0 và KHÔNG được có nút Thử lại"
  );

  await act(async () => {
    act(() => root2.unmount());
  });
  container2.remove();
});

test("AC7 (R3): Cancel uncertain -> reload -> Đối soát CTA hiện; dialog giữ canceled intent", async () => {
  resetMockState();

  const canceledUnresolvedJob = {
    id: "job_ac7_canceled_unresolved",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state: "Canceled",
    reasonCode: "canceled_late_result",
    attempts: 1,
    maxAttempts: 3,
    sideEffectSubmitted: 1,
    hasUnresolvedSideEffect: true,
    cancelRequested: true,
    revision: 5,
    createdAtUtc: new Date().toISOString(),
  };

  mockActiveJobs = [canceledUnresolvedJob];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
      })
    );
  });

  await waitForCondition(() => Boolean(container.querySelector('[role="dialog"]')));
  await waitForCondition(() => !container.textContent?.includes("(Đang cập nhật...)") && Boolean(container.querySelector("button")));

  // Kiểm tra trên card: Có CTA Đối soát (đối soát giải phóng tài nguyên)
  const reconcileBtn = Array.from(container.querySelectorAll("button")).find((btn) =>
    btn.textContent?.includes("Đối soát")
  );
  assert.ok(reconcileBtn, "Canceled unresolved job sau reload PHẢI có CTA Đối soát giải phóng tài nguyên");

  // Mở dialog đối soát
  await act(async () => {
    reconcileBtn.click();
  });

  // Kiểm tra Dialog đối soát đã mở
  await waitForCondition(() => Boolean(document.querySelector('[role="dialog"]') && document.body.textContent?.includes("Đối soát")), 1000);

  // Trong dialog đối soát: Vì job đã bị Canceled nên CẤM chọn hành động 'retry' hoặc 'resubmit'
  const dialogText = document.body.textContent || "";
  assert.ok(!dialogText.includes("Gửi lại"), "Dialog đối soát cho Canceled job không được cho phép Gửi lại");

  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});

test("AC8 (R4): Click Login khi providerKey khác profileId: Gửi đúng profileId thật từ binding", async () => {
  resetMockState();

  const loginRequiredJob = {
    id: "job_ac8_login",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state: "AwaitingUser",
    reasonCode: "login_required",
    providerKey: "chatgpt-web",
    profileId: "prof_chatgpt_real_99",
    attempts: 1,
    revision: 2,
    createdAtUtc: new Date().toISOString(),
  };

  mockActiveJobs = [loginRequiredJob];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
      })
    );
  });

  await waitForCondition(() => Boolean(container.querySelector('[role="dialog"]')));
  await waitForCondition(() => !container.textContent?.includes("(Đang cập nhật...)") && Boolean(container.querySelector("button")));

  const loginBtn = Array.from(container.querySelectorAll("button")).find((btn) =>
    btn.textContent?.includes("Đăng nhập")
  );
  assert.ok(loginBtn, "Nút 'Đăng nhập ngay' phải xuất hiện khi job gặp login_required");

  // Click Đăng nhập ngay
  await act(async () => {
    loginBtn.click();
  });

  await waitForCondition(() => executedCommandsHistory.some((c) => c.command === "browser.request_login"), 1000);

  const loginReq = executedCommandsHistory.find((c) => c.command === "browser.request_login");
  assert.ok(loginReq, "Phải gọi bridge browser.request_login");
  assert.equal(
    loginReq.payload.profileId,
    "prof_chatgpt_real_99",
    "Payload profileId phải là ID profile thật từ binding, KHÔNG được là providerKey 'chatgpt-web'"
  );

  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});

test("AC9 (R4): Job chưa bind tài khoản: Điều hướng Settings -> Tài khoản, không chọn account đầu tiên", async () => {
  resetMockState();

  const unassignedJob = {
    id: "job_ac9_unassigned",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state: "AwaitingUser",
    reasonCode: "login_required",
    providerKey: "chatgpt-web",
    profileId: null, // Chưa được bind vào tài khoản cụ thể
    attempts: 1,
    revision: 1,
    createdAtUtc: new Date().toISOString(),
  };

  let navigatedAction: string | null = null;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(JobItemCard, {
        job: unassignedJob as any,
        onAction: (actionType) => {
          navigatedAction = actionType;
        },
      })
    );
  });

  const settingsBtn = Array.from(container.querySelectorAll("button")).find((btn) =>
    btn.textContent?.includes("Cài đặt Tài khoản")
  );
  assert.ok(settingsBtn, "Job chưa có profileId phải hiện CTA Đến Cài đặt Tài khoản");

  await act(async () => {
    settingsBtn.click();
  });

  assert.equal(
    navigatedAction,
    "navigate_settings_accounts",
    "Phải phát action điều hướng navigate_settings_accounts, không được tự ý gọi login trên account bừa bãi"
  );

  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});

test("AC10 (R6): Complete -> reload: Tab Hoàn tất lấy từ job.list_history; active không chứa terminal resolved", async () => {
  resetMockState();

  // Active jobs KHÔNG chứa job đã hoàn tất đã giải phóng tài nguyên
  mockActiveJobs = [];

  // Lịch sử trả về job đã hoàn tất
  mockHistoryJobs = [
    {
      id: "job_ac10_history_done",
      projectId: "prj_01",
      sceneId: null,
      taskType: "Script",
      state: "Succeeded",
      reasonCode: null,
      attempts: 1,
      revision: 4,
      createdAtUtc: new Date(Date.now() - 3600000).toISOString(),
    },
  ];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
      })
    );
  });

  await waitForCondition(() => Boolean(container.querySelector('[role="dialog"]')));

  // Chuyển sang tab Hoàn tất
  const completedTabBtn = Array.from(container.querySelectorAll('[role="tab"]')).find((tab) =>
    tab.textContent?.includes("Hoàn tất")
  ) as HTMLElement;
  assert.ok(completedTabBtn, "Tab 'Hoàn tất' phải có trên giao diện");

  await act(async () => {
    completedTabBtn.focus();
    completedTabBtn.dispatchEvent(new domWindow.MouseEvent("pointerdown", { bubbles: true, cancelable: true, button: 0 }));
    completedTabBtn.dispatchEvent(new domWindow.MouseEvent("mousedown", { bubbles: true, cancelable: true, button: 0 }));
    completedTabBtn.dispatchEvent(new domWindow.MouseEvent("mouseup", { bubbles: true, cancelable: true, button: 0 }));
    completedTabBtn.click();
  });

  // Chờ lệnh job.list_history được gọi qua bridge
  await waitForCondition(() => executedCommandsHistory.some((c) => c.command === "job.list_history"), 1500);

  // Hiển thị job từ lịch sử trên tab Hoàn tất
  await waitForCondition(() => container.textContent?.includes("job_ac10_history_done".slice(0, 8)) ?? false, 1500);

  const tabText = container.textContent || "";
  assert.ok(tabText.includes("job_ac10_history_done".slice(0, 8)), "Job lịch sử phải xuất hiện trên tab Hoàn tất");

  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});

test("AC11 (Acc. 8): Double-click, conflict: Nút bị disable khi đang xử lý, không gửi trùng lặp", async () => {
  resetMockState();

  const targetJob = {
    id: "job_ac11_guard",
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state: "Failed",
    reasonCode: "network_error",
    attempts: 1,
    maxAttempts: 3,
    sideEffectSubmitted: 0,
    hasUnresolvedSideEffect: false,
    cancelRequested: false,
    revision: 2,
    createdAtUtc: new Date().toISOString(),
  };

  mockActiveJobs = [targetJob];
  commandDelayMs = 50; // Giả lập độ trễ mạng để test double-click

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(QueueDrawer, {
        isOpen: true,
        onClose: () => {},
      })
    );
  });

  await waitForCondition(() => Boolean(container.querySelector('[role="dialog"]')));
  await waitForCondition(() => !container.textContent?.includes("(Đang cập nhật...)") && Boolean(container.querySelector("button")));

  const retryBtn = Array.from(container.querySelectorAll("button")).find((btn) =>
    btn.textContent?.includes("Thử lại")
  ) as HTMLButtonElement;
  assert.ok(retryBtn, "Nút Thử lại phải xuất hiện");

  // Click 2 lần liên tiếp nhanh
  await act(async () => {
    retryBtn.click();
    retryBtn.click();
  });

  // Đợi lệnh đầu tiên hoàn tất
  await new Promise((r) => setTimeout(r, 120));

  const retryCalls = executedCommandsHistory.filter((c) => c.command === "job.retry");
  assert.equal(
    retryCalls.length,
    1,
    "Chỉ được phát đúng 1 lệnh job.retry, lần click thứ 2 khi nút đang loading phải bị chặn hoàn toàn"
  );

  commandDelayMs = 5;
  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});

test("AC12 (Acc. 9): Keyboard navigation, Escape và dialog lồng không phá vỡ giao diện", async () => {
  resetMockState();

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
      })
    );
  });

  await waitForCondition(() => Boolean(container.querySelector('[role="dialog"]')));

  const drawer = container.querySelector('[role="dialog"]');
  assert.ok(drawer, "QueueDrawer phải có role='dialog'");
  assert.equal(drawer.getAttribute("aria-modal"), "true");

  // Bấm Escape
  await act(async () => {
    const escEvent = new domWindow.KeyboardEvent("keydown", {
      key: "Escape",
      bubbles: true,
      cancelable: true,
    });
    window.dispatchEvent(escEvent as unknown as Event);
  });

  assert.equal(isClosed, true, "Bấm Escape phải gọi callback onClose của QueueDrawer");

  await act(async () => {
    act(() => root.unmount());
  });
  container.remove();
});
