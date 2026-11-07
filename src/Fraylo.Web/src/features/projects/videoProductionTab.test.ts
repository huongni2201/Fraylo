import { domWindow } from "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
let lastBridgeCommand: string | null = null;
let lastBridgePayload: Record<string, unknown> | null = null;
let lastWorkflowConfigPayload: Record<string, unknown> | null = null;
const workflowReadPayloads = new Map<string, { payload: Record<string, unknown> }>();
let executedCommands: string[] = [];
const retryStepPayloads: Array<Record<string, unknown>> = [];
let mockRetryFailure: { code: string; message: string } | null = null;
let mockUploadedAsset: Record<string, unknown> | null = null;

let mockConfig: Record<string, any> = {
  projectId: "prj_test_01",
  workflowMode: "ModelAndProduct",
  goalPrompt: "Trang phục dạo phố lụa mềm",
  modelAssetId: "ast_model_01",
  productAssetId: "ast_product_01",
  readyImageAssetId: null,
  foundationImageAssetId: null,
  audioOption: "None",
  audioAssetId: null,
  targetSceneCount: 1,
  targetDurationPerSceneSeconds: 15,
  runState: "Idle",
  currentRunId: null,
  updatedAtUtc: "2026-10-27T01:00:00Z",
};

let mockScenes: Array<Record<string, unknown>> = [];
let mockContentItems: Array<Record<string, unknown>> = [];
let mockStoryboard: unknown = null;
let mockPromptTemplate: Record<string, any> | null = null;

window.chrome = {
  webview: {
    postMessage: (envelope: unknown) => {
      const req = (typeof envelope === "string" ? JSON.parse(envelope) : envelope) as {
        id: string;
        type: string;
        command: string;
        payload: Record<string, unknown>;
        contractVersion: string;
      };

      lastBridgeCommand = req.command;
      lastBridgePayload = req.payload;
      if (req.command === "workflow.get_config") {
        lastWorkflowConfigPayload = req.payload;
      }
      if (["workflow.get_config", "workflow.list_scenes", "workflow.list_content_items"].includes(req.command)) {
        workflowReadPayloads.set(req.command, { payload: req.payload });
      }
      executedCommands.push(req.command);

      setTimeout(() => {
        let responseData: unknown = null;
        let success = true;
        let responseError: unknown = null;

        switch (req.command) {
          case "asset.list":
            responseData = { assets: [], totalCount: 0, page: 1, pageSize: 20 };
            break;
          case "prompt.get_template":
            responseData = mockPromptTemplate ?? {
              template: {
                templateId: "tpl_script_custom",
                purpose: "Script",
                createdAtUtc: "2026-10-27T00:00:00Z",
                updatedAtUtc: "2026-10-27T00:00:00Z",
              },
              content: {
                templateId: "tpl_script_custom",
                templateText: "Kịch bản video TikTok.",
                variableDefinitionsJson: "[]",
                createdAtUtc: "2026-10-27T00:00:00Z",
              },
            };
            break;
          case "project.get":
            responseData = { project: { projectId: mockConfig.projectId, name: "Dự án timeout", status: "InProgress", description: null } };
            break;
          case "workflow.get_config":
            responseData = mockConfig;
            break;
          case "workflow.list_scenes":
            responseData = { scenes: mockScenes };
            break;
          case "workflow.list_content_items":
            responseData = mockContentItems;
            break;
          case "workflow.get_storyboard":
            responseData = { storyboard: mockStoryboard };
            break;
          case "workflow.save_config":
            mockConfig = { ...mockConfig, ...req.payload };
            responseData = mockConfig;
            break;
          case "workflow.start":
            mockConfig = { ...mockConfig, runState: "Running" };
            mockScenes = [
              {
                sceneId: "scn_01",
                projectId: "prj_test_01",
                sequenceOrder: 1,
                name: "Clip 1",
                targetDurationSeconds: mockConfig.targetDurationPerSceneSeconds || 15,
                workflowStatus: "PreparingPrompt",
                imageStrategy: "ReuseFoundation",
                sourceImageAssetId: "ast_model_01",
                selectedImageAssetId: "ast_model_01",
                motionPromptText: "Cinematic shot",
                audioOption: "None",
                audioAssetId: null,
                videoAssetId: null,
                isStale: false,
                staleReason: null,
                revision: "rev_01",
              },
            ];
            responseData = {
              runId: "run_01",
              runState: "Running",
              createdSceneCount: 1,
              message: "Đã bắt đầu quy trình tạo video.",
            };
            break;
          case "workflow.preview_ai_prompt":
            responseData = {
              templateId: "tpl_video_prompt_default",
              templateName: "Mẫu Prompt Video Chuẩn",

              composedPrompt: `[HƯỚNG DẪN TỪ TEMPLATE VIDEO PROMPT ĐÃ CHỌN]\nViết prompt chuyển động...\n\n[NHIỆM VỤ]\nViết prompt cho clip_1...\n\n[DỮ LIỆU LẦN TẠO — do app serialize]\nCLIP_CONTEXT: clip_1, thời lượng 15s\n\n[OUTPUT_CONTRACT]\nChỉ trả về prompt chuyển động camera`,
              outputContractVersion: 1,
            };
            break;
          case "asset.upload":
            responseData = {
              asset: mockUploadedAsset ?? {
                assetId: "ast_uploaded_direct",
                projectId: req.payload?.projectId,
                assetType: req.payload?.assetType || "Image",
                relativePath: `media/${req.payload?.assetType || "image"}.png`,
                byteSize: 1024,
                sha256Hash: "hash123",
                status: "Linked",
                createdAtUtc: new Date().toISOString(),
              },
              canceled: false,
            };
            break;
          case "workflow.retry_step":
            retryStepPayloads.push(req.payload);
            if (mockRetryFailure) {
              success = false;
              responseError = { code: mockRetryFailure.code, message: mockRetryFailure.message, retryable: false };
            } else {
              // Backend nhận lệnh: bước chuyển Chờ chạy, run chạy lại.
              mockConfig = {
                ...mockConfig,
                runState: "Running",
                steps: (mockConfig.steps ?? []).map((step: Record<string, unknown>) =>
                  step.jobId === req.payload.jobId ? { ...step, outcome: "Pending", canRetry: false, reasonMessage: null, attemptCount: 2 } : step),
              };
              responseData = { jobId: "job-retried", jobRevision: 1, runState: "Running" };
            }
            break;
          default:
            responseData = null;
        }

        const respEnvelope = {
          id: req.id,
          type: "response",
          success,
          data: responseData,
          error: responseError,
          serverTimestampUtc: new Date().toISOString(),
          contractVersion: req.contractVersion || "1.1.0",
        };

        const event = new domWindow.MessageEvent("message", {
          data: JSON.stringify(respEnvelope),
        });
        window.dispatchEvent(event as unknown as Event);
      }, 5);
    },
    addEventListener: (type: string, listener: (ev: MessageEvent) => void) => {
      window.addEventListener(type, listener as EventListener);
    },
    removeEventListener: (type: string, listener: (ev: MessageEvent) => void) => {
      window.removeEventListener(type, listener as EventListener);
    },
  },
} as any;

const React = await import("react");
const ReactDOMClient = await import("react-dom/client");
const { VideoProductionTab } = await import("./VideoProductionTab");

test("Bảng kịch bản cho mở và sao chép toàn bộ nội dung TikTok đã lưu", async () => {
  const caption = "Outfit 04 🖤\nĐen - nâu cho một ngày dạo phố\n\nPhối cùng túi nâu.\nLưu lại để thử nhé!\n#outfit #ootd #phoido";
  mockContentItems = [
    { id: "caption", sequenceOrder: 1, tiktok_content: caption },
    { id: "empty", sequenceOrder: 2, tiktok_content: "" },
  ];
  const copied: string[] = [];
  Object.defineProperty(navigator, "clipboard", {
    configurable: true,
    value: { writeText: async (value: string) => { copied.push(value); } },
  });
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);
  try {
    await React.act(async () => { root.render(React.createElement(VideoProductionTab, { projectId: "prj_test_01" })); });
    await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 100)); });

    assert.doesNotMatch(container.textContent ?? "", /cảnh đã lưu CSDL/);
    const [captionRow, emptyRow] = container.querySelectorAll("tbody tr");
    const captionCell = captionRow.querySelectorAll("td")[2];
    const emptyCell = emptyRow.querySelectorAll("td")[2];
    assert.match(emptyCell.textContent ?? "", /Chưa có nội dung đăng TikTok/);
    assert.equal(emptyCell.querySelector("button"), null);

    const copyButton = captionCell.querySelector<HTMLButtonElement>('button[aria-label="Sao chép"]');
    const expandButton = captionCell.querySelector<HTMLButtonElement>('button[aria-label="Xem chi tiết"]');
    assert.ok(copyButton);
    assert.ok(expandButton);
    await React.act(async () => copyButton.click());
    await React.act(async () => expandButton.click());

    const dialog = document.querySelector('[role="dialog"]');
    assert.match(dialog?.textContent ?? "", /Nội dung đăng TikTok cảnh 1/);
    assert.equal(dialog?.querySelector("pre")?.textContent, caption);
    const dialogCopy = Array.from(dialog!.querySelectorAll("button")).find(button => button.textContent?.includes("sao chép"));
    assert.ok(dialogCopy);
    await React.act(async () => dialogCopy.click());
    assert.deepEqual(copied, [caption, caption]);
  } finally {
    await React.act(async () => root.unmount());
    container.remove();
    Reflect.deleteProperty(navigator, "clipboard");
    mockContentItems = [];
  }
});

test("Prompt đã sẵn sàng hiển thị trạng thái chờ tác vụ video", async () => {
  mockConfig = { ...mockConfig, runState: "Running" };
  mockScenes = [{ sceneId: "s-ready", projectId: "prj_test_01", name: "Cảnh 1", workflowStatus: "ReadyForVideo", motionPromptText: "Camera pan", activeJobId: null, videoAssetId: null }];
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);
  await React.act(async () => { root.render(React.createElement(VideoProductionTab, { projectId: "prj_test_01" })); });
  await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 100)); });
  assert.match(container.textContent ?? "", /Ảnh và prompt đã sẵn sàng, đang chờ tác vụ tạo video/);
  assert.doesNotMatch(container.textContent ?? "", /Đang tạo video/);
  await React.act(async () => root.unmount());
  container.remove();
  mockConfig = { ...mockConfig, runState: "Idle" };
  mockScenes = [];
});

test("Job render đang chạy hiển thị đúng trạng thái tạo video", async () => {
  const originalConfig = mockConfig;
  const originalScenes = mockScenes;
  mockConfig = { ...mockConfig, runState: "Running" };
  mockScenes = [{ sceneId: "s-render", projectId: "prj_test_01", name: "Cảnh 1", workflowStatus: "ReadyForVideo", motionPromptText: "Camera pan", activeJobId: "render-job", activeJobType: "VideoRender", activeJobState: "Running", videoAssetId: null }];
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);
  try {
    await React.act(async () => { root.render(React.createElement(VideoProductionTab, { projectId: "prj_test_01" })); });
    await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 100)); });
    assert.match(container.textContent ?? "", /Đang tạo video/);
    assert.doesNotMatch(container.textContent ?? "", /Chờ tạo video|nhà cung cấp được kiểm chứng/);
  } finally {
    await React.act(async () => root.unmount());
    container.remove();
    mockConfig = originalConfig;
    mockScenes = originalScenes;
  }
});

test("Tiến độ tự đồng bộ khi video hoàn tất sau sự kiện job", async () => {
  const originalConfig = mockConfig;
  const originalScenes = mockScenes;
  mockConfig = { ...mockConfig, runState: "Running", videoStrategy: "SingleVideo" };
  mockScenes = [{ sceneId: "s-sync", projectId: "prj_test_01", name: "Cảnh 1", workflowStatus: "ReadyForVideo", motionPromptText: "Camera pan", activeJobId: null, videoAssetId: null }];
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);
  try {
    await React.act(async () => { root.render(React.createElement(VideoProductionTab, { projectId: "prj_test_01" })); });
    await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 100)); });
    assert.match(container.textContent ?? "", /Chờ tạo video/);
    // Host đã gửi sự kiện job trước khi kết quả được ghi vào workflow; không còn sự kiện tiếp theo.
    mockConfig = { ...mockConfig, runState: "Completed", finalVideoAssetId: "video-sync" };
    mockScenes = [{ ...mockScenes[0], workflowStatus: "VideoCompleted", videoAssetId: "video-sync" }];
    await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 3300)); });
    assert.match(container.textContent ?? "", /Chuẩn bị đăng TikTok/);
    assert.doesNotMatch(container.textContent ?? "", /Chờ tạo video/);
  } finally {
    await React.act(async () => root.unmount());
    container.remove();
    mockConfig = originalConfig;
    mockScenes = originalScenes;
  }
});

test("Hiển thị storyboard đã lưu theo thứ tự và trạng thái chưa có kịch bản", async () => {
  const container = document.createElement("div"); document.body.append(container);
  const root = ReactDOMClient.createRoot(container);
  mockStoryboard = { durationSeconds: 15, shots: [
    { startSeconds: 0, endSeconds: 5, description: "Giới thiệu sản phẩm", imagePrompt: "Góc toàn thân" },
    { startSeconds: 5, endSeconds: 15, description: "Cận chất liệu", imagePrompt: "Chi tiết áo" },
  ] };
  await React.act(async () => { root.render(React.createElement(VideoProductionTab, { projectId: "prj_test_01" })); });
  await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 100)); });
  await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 100)); });
  assert.match(container.textContent || "", /0–5 giây/);
  assert.match(container.textContent || "", /Cận chất liệu/);
  assert.match(container.textContent || "", /Chi tiết áo/);
  mockStoryboard = null;
  const refresh = Array.from(container.querySelectorAll("button")).find(button => button.textContent?.includes("Làm mới kịch bản"));
  assert(refresh);
  await React.act(async () => { refresh.click(); });
  await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 60)); });
  assert.match(container.textContent || "", /Chưa có kịch bản nhiều phân cảnh/);
  await React.act(async () => root.unmount()); container.remove();
});

test("Video hoàn tất chuyển đúng Project sang Đăng video; stale bị khóa", async () => {
  mockConfig = { ...mockConfig, projectId: "prj_publish", runState: "Completed", targetSceneCount: 1, targetDurationPerSceneSeconds: 15 };
  mockScenes = [{ sceneId: "scene_publish", projectId: "prj_publish", name: "Video hoàn tất", sequenceOrder: 1,
    targetDurationSeconds: 15, videoAssetId: "ast_video", workflowStatus: "VideoCompleted", isStale: false }];
  const container = document.createElement("div"); document.body.append(container);
  const root = ReactDOMClient.createRoot(container);
  let selectedProject = "";
  const render = () => React.createElement(VideoProductionTab, { projectId: "prj_publish", onOpenPublishing: (id: string) => { selectedProject = id; } });
  await React.act(async () => { root.render(render()); await new Promise(resolve => setTimeout(resolve, 60)); });
  await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 60)); });
  assert.deepEqual(lastWorkflowConfigPayload, { projectId: "prj_publish" });
  assert.deepEqual(workflowReadPayloads.get("workflow.list_scenes"), { payload: { projectId: "prj_publish" } });
  assert.deepEqual(workflowReadPayloads.get("workflow.list_content_items"), { payload: { projectId: "prj_publish" } });
  const publishButton = () => Array.from(container.querySelectorAll("button")).find(b => b.textContent?.trim() === "Đăng video")!;
  assert(publishButton(), "Có CTA Đăng video trong kết quả.");
  assert.equal(publishButton().disabled, false);
  await React.act(async () => publishButton().click());
  assert.equal(selectedProject, "prj_publish");
  await React.act(async () => root.unmount()); container.remove();
  mockScenes = [{ ...mockScenes[0], isStale: true }];
  const staleContainer = document.createElement("div"); document.body.append(staleContainer);
  const staleRoot = ReactDOMClient.createRoot(staleContainer);
  await React.act(async () => { staleRoot.render(render()); });
  await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 60)); });
  assert.equal(Array.from(staleContainer.querySelectorAll("button")).find(b => b.textContent?.trim() === "Đăng video")!.disabled, true);
  await React.act(async () => staleRoot.unmount()); staleContainer.remove();
});
test("Nhiều video: đủ clip mọi cảnh thì hiện màn hoàn tất và cho đăng", async () => {
  const previousConfig = mockConfig;
  mockConfig = { ...mockConfig, projectId: "prj_multi", runState: "Completed", videoStrategy: "MultiVideo", targetSceneCount: 2, finalVideoAssetId: null };
  mockScenes = [1, 2].map(n => ({ sceneId: `scn_${n}`, projectId: "prj_multi", name: `Cảnh ${n}`, sequenceOrder: n,
    targetDurationSeconds: 15, videoAssetId: `ast_clip_${n}`, workflowStatus: "VideoCompleted", isStale: false }));
  const container = document.createElement("div"); document.body.append(container);
  const root = ReactDOMClient.createRoot(container);
  await React.act(async () => { root.render(React.createElement(VideoProductionTab, { projectId: "prj_multi", onOpenPublishing: () => {} })); });
  await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 100)); });
  assert.match(container.textContent ?? "", /2 clip đã sẵn sàng/);
  assert.equal(Array.from(container.querySelectorAll("button")).find(b => b.textContent?.trim() === "Đăng video")!.disabled, false);
  await React.act(async () => root.unmount()); container.remove();
  mockConfig = previousConfig;
  mockScenes = [];
});

test("VideoProductionTab: Một video đầu ra, duration theo capability & tóm tắt", async () => {
  executedCommands = [];
  mockConfig = {
    projectId: "prj_test_01",
    workflowMode: "ModelAndProduct",
    goalPrompt: "BST Váy lụa cao cấp",
    modelAssetId: "ast_model_01",
    productAssetId: "ast_product_01",
    readyImageAssetId: null,
    foundationImageAssetId: null,
    audioOption: "None",
    audioAssetId: null,
    targetSceneCount: 1, // CD01: default 1 clip cho cấu hình mới
    targetDurationPerSceneSeconds: 15, // CD01: ưu tiên 15s nếu hỗ trợ
    runState: "Idle",
    currentRunId: null,
    updatedAtUtc: "2026-10-27T01:00:00Z",
    videoStrategy: "SingleVideo",
  };
  mockScenes = [];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);

  await React.act(async () => {
    root.render(
      React.createElement(VideoProductionTab, {
        projectId: "prj_test_01",
      })
    );
  });

  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  const fullText = container.textContent || "";

  // Luồng một video: chọn chiến lược trên dropdown, không hiện ô số cảnh.
  const strategySelect = container.querySelector("#video-strategy") as HTMLSelectElement;
  assert(strategySelect?.value === "SingleVideo", "Chiến lược phải hiển thị 'Một video nhiều phân cảnh'.");
  assert(!container.querySelector("#scene-count"), "Luồng một video không có ô số cảnh.");

  // CD02: Input số tự do của duration bị thay thế bằng select
  const durationSelect = container.querySelector("#target-duration") as HTMLSelectElement;
  assert(durationSelect, "Phải có dropdown select cho '#target-duration'.");
  assert(durationSelect.tagName.toLowerCase() === "select", "Thời lượng phải là thẻ <select>, không phải <input type='number'>");

  const options = Array.from(durationSelect.querySelectorAll("option")).map((o) => o.value);
  assert(options.includes("15"), "Bộ chọn phải có option 15s.");
  assert(options.includes("10"), "Bộ chọn phải có option 10s.");
  assert(options.includes("30"), "Bộ chọn phải có option 30s (người dùng yêu cầu cho phép tới 30 giây).");

  // Tóm tắt phải khớp với video duy nhất và thời lượng mục tiêu.
  assert(fullText.includes("Tóm tắt: Tạo 1 video, mục tiêu 15 giây"), "Phải tóm tắt video đầu ra và thời lượng mục tiêu.");

  // Click Bắt đầu tạo video khi duration và brief hợp lệ
  const startButton = Array.from(container.querySelectorAll("button")).find((b) =>
    b.textContent?.includes("Bắt đầu tạo video")
  );
  assert(startButton, "Phải có nút 'Bắt đầu tạo video'.");

  await React.act(async () => {
    startButton.click();
  });
  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 80));
  });

  assert(executedCommands.includes("workflow.start"), "Duration 15s hợp lệ phải trigger 'workflow.start'.");

  React.act(() => root.unmount());
  container.remove();
});

test("VideoProductionTab: nhiều video có ô số cảnh và tóm tắt theo số cảnh", async () => {
  executedCommands = [];
  mockConfig = {
    projectId: "prj_test_01",
    workflowMode: "ModelAndProduct",
    goalPrompt: "BST Váy lụa cao cấp",
    modelAssetId: "ast_model_01",
    productAssetId: "ast_product_01",
    readyImageAssetId: null,
    foundationImageAssetId: null,
    audioOption: "None",
    audioAssetId: null,
    targetSceneCount: 3,
    targetDurationPerSceneSeconds: 15,
    runState: "Idle",
    currentRunId: null,
    updatedAtUtc: "2026-10-27T01:00:00Z",
    videoStrategy: "MultiVideo",
  };
  mockScenes = [];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);

  await React.act(async () => {
    root.render(React.createElement(VideoProductionTab, { projectId: "prj_test_01" }));
  });
  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  const strategySelect = container.querySelector("#video-strategy") as HTMLSelectElement;
  assert(strategySelect?.value === "MultiVideo", "Nhiều video phải được chọn trên dropdown chiến lược.");
  const sceneCountInput = container.querySelector("#scene-count") as HTMLInputElement;
  assert(sceneCountInput?.value === "3", "Ô số cảnh phải hiển thị số cảnh đã lưu.");
  assert((container.textContent || "").includes("Tóm tắt: Tạo 3 video, mỗi video 15 giây"), "Tóm tắt phải theo số cảnh và thời lượng mỗi clip.");

  root.unmount();
  container.remove();
});

test("VideoProductionTab: CD04 & CD08 - Dữ liệu cũ 5s hiển thị cảnh báo và bị chặn trước khi Bắt đầu", async () => {
  executedCommands = [];
  mockConfig = {
    projectId: "prj_legacy_01",
    workflowMode: "ModelAndProduct",
    goalPrompt: "Dự án cũ 3 clip 5s",
    modelAssetId: "ast_model_01",
    productAssetId: "ast_product_01",
    readyImageAssetId: null,
    foundationImageAssetId: null,
    audioOption: "None",
    audioAssetId: null,
    targetSceneCount: 3,
    targetDurationPerSceneSeconds: 5, // CD08: Dữ liệu cũ 5s
    runState: "Idle",
    currentRunId: null,
    updatedAtUtc: "2026-10-27T01:00:00Z",
  };
  mockScenes = [];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);

  await React.act(async () => {
    root.render(
      React.createElement(VideoProductionTab, {
        projectId: "prj_legacy_01",
      })
    );
  });

  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  const fullText = container.textContent || "";
  // Phải bảo toàn hiển thị 3 clip x 5s cũ, kèm cảnh báo
  assert(fullText.includes("5 giây"), "Dữ liệu cũ 5s phải mở được và hiển thị trong form.");
  assert(
    fullText.includes("chưa được nhà cung cấp hỗ trợ") || fullText.includes("Cần chọn lại thời lượng"),
    "Phải hiển thị cảnh báo yêu cầu chọn lại thời lượng được hỗ trợ."
  );

  // Click Bắt đầu phải bị chặn, không gửi workflow.start
  const startButton = Array.from(container.querySelectorAll("button")).find((b) =>
    b.textContent?.includes("Bắt đầu tạo video")
  );
  assert(startButton);

  await React.act(async () => {
    startButton.click();
  });
  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  assert(!executedCommands.includes("workflow.start"), "CD04: Thời lượng 5s không được hỗ trợ phải bị chặn chạy.");
  const errorText = container.textContent || "";
  assert(errorText.includes("không được nhà cung cấp video hỗ trợ"), "Phải có thông báo lỗi cụ thể.");

  React.act(() => root.unmount());
  container.remove();
});

test("VideoProductionTab: không bắt buộc brief khi template không có biến", async () => {
  localStorage.removeItem("Fraylo_workflow_prompt_values_prj_test_02");
  mockPromptTemplate = { template: { templateId: "tpl_no_vars", purpose: "Script", createdAtUtc: "2026-10-27T00:00:00Z", updatedAtUtc: "2026-10-27T00:00:00Z" }, content: { templateId: "tpl_no_vars", templateText: "Tạo một video ngắn.", variableDefinitionsJson: "[]", createdAtUtc: "2026-10-27T00:00:00Z" } };
  executedCommands = [];
  mockConfig = {
    projectId: "prj_test_02",
    workflowMode: "ModelAndProduct",
    goalPrompt: "   ", // Brief trống sau trim
    modelAssetId: "ast_model_01",
    productAssetId: "ast_product_01",
    readyImageAssetId: null,
    foundationImageAssetId: null,
    audioOption: "None",
    audioAssetId: null,
    targetSceneCount: 1,
    targetDurationPerSceneSeconds: 15,
    runState: "Idle",
    currentRunId: null,
    updatedAtUtc: "2026-10-27T01:00:00Z",
  };
  mockScenes = [];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);

  await React.act(async () => {
    root.render(
      React.createElement(VideoProductionTab, {
        projectId: "prj_test_02",
      })
    );
  });

  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  const startButton = Array.from(container.querySelectorAll("button")).find((b) =>
    b.textContent?.includes("Bắt đầu tạo video")
  );
  assert(startButton);

  await React.act(async () => {
    startButton.click();
  });
  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  assert(executedCommands.includes("workflow.start"), "Template không có biến phải có thể bắt đầu mà không cần brief.");

  React.act(() => root.unmount());
  container.remove();
});

test("VideoProductionTab: CD11 & CD13 - Xem trước yêu cầu gửi AI writer (Preview AI Prompt Modal)", async () => {
  executedCommands = [];
  mockConfig = {
    projectId: "prj_test_03",
    workflowMode: "ModelAndProduct",
    goalPrompt: "UNIQUE_BRIEF_TOKEN_9988 Áo vest công sở lịch lãm",
    modelAssetId: "ast_model_01",
    productAssetId: "ast_product_01",
    readyImageAssetId: null,
    foundationImageAssetId: null,
    audioOption: "None",
    audioAssetId: null,
    targetSceneCount: 1,
    targetDurationPerSceneSeconds: 15,
    runState: "Idle",
    currentRunId: null,
    updatedAtUtc: "2026-10-27T01:00:00Z",
  };
  mockScenes = [];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);

  await React.act(async () => {
    root.render(
      React.createElement(VideoProductionTab, {
        projectId: "prj_test_03",
      })
    );
  });

  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  // Tìm nút 'Xem yêu cầu gửi AI'
  const previewButton = Array.from(container.querySelectorAll("button")).find((b) =>
    b.textContent?.includes("Xem yêu cầu gửi AI")
  );
  assert(previewButton, "Phải có nút 'Xem yêu cầu gửi AI'.");

  await React.act(async () => {
    previewButton.click();
  });
  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  assert(executedCommands.includes("workflow.preview_ai_prompt"), "Click nút preview phải gọi bridge 'workflow.preview_ai_prompt'.");

  // Kiểm tra nội dung Dialog hiển thị
  const bodyText = document.body.textContent || "";
  assert(bodyText.includes("Xem yêu cầu gửi AI viết prompt chuyển động"), "Dialog tiêu đề xem trước phải xuất hiện.");
  assert(bodyText.includes("Mẫu Prompt Video Chuẩn"), "Dialog phải hiển thị tên template.");
  assert(!bodyText.includes("User Brief"), "Preview không hiển thị trường brief đã bỏ.");

  React.act(() => root.unmount());
  container.remove();
});

test("VideoProductionTab: prompt áp dụng theo thứ tự Dự án > mẫu toàn cục, không còn biến", async () => {
  executedCommands = [];
  mockPromptTemplate = {
    template: {
      templateId: "tpl_script_fashion",
      purpose: "Script",
      createdAtUtc: "2026-10-27T00:00:00Z",
      updatedAtUtc: "2026-10-27T00:00:00Z",
    },
    content: {
      templateId: "tpl_script_fashion",
      templateText: "Tạo kịch bản video TikTok.",
      variableDefinitionsJson: "[]",
      createdAtUtc: "2026-10-27T00:00:00Z",
    },
  };
  mockConfig = {
    projectId: "prj_test_dyn_vars",
    workflowMode: "ModelAndProduct",
    goalPrompt: "",
    modelAssetId: "ast_model_01",
    productAssetId: "ast_product_01",
    readyImageAssetId: null,
    foundationImageAssetId: null,
    audioOption: "None",
    audioAssetId: null,
    targetSceneCount: 1,
    targetDurationPerSceneSeconds: 15,
    runState: "Idle",
    currentRunId: null,
    updatedAtUtc: "2026-10-27T01:00:00Z",
    scriptGuidance: null,
    videoGuidance: "Prompt video của dự án",
  };
  mockScenes = [];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);

  await React.act(async () => {
    root.render(
      React.createElement(VideoProductionTab, {
        projectId: "prj_test_dyn_vars",
      })
    );
  });

  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 60));
  });

  // 1. Kiểm tra badge/thông báo mẫu prompt áp dụng
  const badgeArea = container.textContent || "";
  assert(badgeArea.includes("Mẫu prompt áp dụng"), "Phải có thông báo mẫu prompt áp dụng.");
  // Kịch bản: dự án trống nên dùng mẫu toàn cục; Video: dự án có prompt nên dùng dự án.
  assert(badgeArea.includes("Video: Dự án"), "Phải hiển thị nguồn prompt đang áp dụng theo thứ tự ưu tiên.");
  assert(badgeArea.includes("Prompt tùy chỉnh"), "Có prompt dự án thì không phải mẫu toàn cục.");

  // 2. Không còn ô nhập biến.
  assert(!container.querySelector("[id^='var-input-']"), "Không còn ô nhập biến theo mẫu prompt ở dự án.");
  assert(!badgeArea.includes("Biến số theo Mẫu Prompt"), "Không còn khối biến số theo mẫu prompt.");

  // 3. Bắt đầu: lưu cấu hình rồi khởi chạy
  const startButton = Array.from(container.querySelectorAll("button")).find((b) =>
    b.textContent?.includes("Bắt đầu tạo video")
  );
  assert(startButton, "Phải có nút 'Bắt đầu tạo video'.");

  await React.act(async () => {
    startButton.click();
  });

  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  assert(executedCommands.includes("workflow.save_config"), "Bắt đầu phải lưu cấu hình trước.");
  assert(executedCommands.includes("workflow.start"), "Bấm bắt đầu phải kích hoạt 'workflow.start'.");
  assert.strictEqual("promptVariables" in mockConfig, false, "Payload không còn gửi biến.");
  assert.strictEqual(mockConfig.scriptGuidance, "", "Prompt kịch bản riêng để trống thì gửi chuỗi rỗng.");
  assert.strictEqual(mockConfig.videoGuidance, "Prompt video của dự án", "Prompt video riêng của dự án được lưu nguyên văn.");
  assert.strictEqual("scriptPromptText" in mockConfig, false, "Payload không còn gửi text mẫu đã render.");

  React.act(() => root.unmount());
  container.remove();
  mockPromptTemplate = null;

  React.act(() => root.unmount());
  container.remove();
});

test("VideoProductionTab: bật ảnh storyboard 9 ô giữ thời lượng đã chọn và lưu referenceImageMode", async () => {
  executedCommands = [];
  mockConfig = {
    projectId: "prj_test_01",
    workflowMode: "ReadyImage",
    goalPrompt: "",
    modelAssetId: null,
    productAssetId: null,
    readyImageAssetId: "ast_ready_01",
    foundationImageAssetId: null,
    audioOption: "None",
    audioAssetId: null,
    targetSceneCount: 1,
    targetDurationPerSceneSeconds: 10,
    runState: "Idle",
    currentRunId: null,
    updatedAtUtc: "2026-10-30T01:00:00Z",
    videoStrategy: "SingleVideo",
  };
  mockScenes = [];

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);
  await React.act(async () => {
    root.render(React.createElement(VideoProductionTab, { projectId: "prj_test_01" }));
  });
  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  const grid = container.querySelector("#grid-mode") as HTMLInputElement;
  assert(grid && !grid.checked, "Một video có tùy chọn ảnh 9 ô, mặc định tắt (cấu hình cũ là Direct).");
  await React.act(async () => grid.click());

  const duration = container.querySelector("#target-duration") as HTMLSelectElement;
  assert.equal(duration.value, "10", "Bật ảnh 9 ô giữ nguyên thời lượng đã chọn.");
  assert.equal(duration.disabled, false, "Thời lượng vẫn chọn được khi bật ảnh 9 ô.");

  const saveButton = Array.from(container.querySelectorAll("button")).find((b) => b.textContent?.includes("Lưu cấu hình"))!;
  await React.act(async () => saveButton.click());
  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 80));
  });
  assert.equal(mockConfig.referenceImageMode, "StoryboardGrid9");
  assert.equal(mockConfig.targetDurationPerSceneSeconds, 10);

  // Đổi sang nhiều video thì tùy chọn biến mất và cấu hình trở về Direct.
  const strategySelect = container.querySelector("#video-strategy") as HTMLSelectElement;
  await React.act(async () => {
    const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value")!.set!;
    setter.call(strategySelect, "MultiVideo");
    strategySelect.dispatchEvent(new Event("change", { bubbles: true }));
  });
  assert(!container.querySelector("#grid-mode"), "Nhiều video không có tùy chọn ảnh 9 ô.");

  await React.act(async () => root.unmount());
  container.remove();
});

test("Ảnh 9 ô: mốc tiến trình suy ra từ cảnh thật và màn đang chạy hiện ảnh đầu vào, ảnh ghép, ảnh 3×3", async () => {
  const { gridMilestones } = await import("./StoryboardGridProgress");
  const config = { ...mockConfig, workflowMode: "ModelAndProduct", videoStrategy: "SingleVideo", referenceImageMode: "StoryboardGrid9", runState: "Running" } as any;
  const keys = (m: ReturnType<typeof gridMilestones>) => m.map((x) => `${x.key}:${x.status}`).join(",");

  assert.equal(keys(gridMilestones(config, undefined)), "script:active,compose:pending,grid:pending,prompt:pending,video:pending");
  const afterScript = { sceneId: "scn_g", projectId: "prj_grid", name: "Video" } as any;
  assert.equal(keys(gridMilestones(config, afterScript)), "script:done,compose:active,grid:pending,prompt:pending,video:pending");
  const composed = { ...afterScript, composedImageAssetId: "ast_composed" };
  assert.equal(keys(gridMilestones(config, composed)), "script:done,compose:done,grid:active,prompt:pending,video:pending");
  const grid = { ...composed, gridImageAssetId: "ast_grid" };
  assert.equal(keys(gridMilestones(config, grid)), "script:done,compose:done,grid:done,prompt:active,video:pending");
  assert.equal(keys(gridMilestones({ ...config, runState: "Failed" }, composed)), "script:done,compose:done,grid:failed,prompt:pending,video:pending");
  const ready = { ...grid, motionPromptText: "Camera pan" };
  assert.equal(gridMilestones(config, ready).at(-1)?.status, "pending", "Chưa có job render thì không báo đang xử lý.");
  const rendering = { ...ready, activeJobId: "render", activeJobType: "VideoRender", activeJobState: "Running" };
  assert.equal(gridMilestones(config, rendering).at(-1)?.status, "active");
  assert.equal(gridMilestones(config, { ...rendering, activeJobState: "Queued" }).at(-1)?.status, "pending");
  assert.equal(gridMilestones({ ...config, runState: "Paused" }, rendering).at(-1)?.status, "pending");
  assert.equal(gridMilestones(config, { ...rendering, videoAssetId: "video" }).at(-1)?.status, "done");
  // Ảnh sẵn sàng không có bước ghép.
  assert.equal(keys(gridMilestones({ ...config, workflowMode: "ReadyImage" }, afterScript)), "script:done,grid:active,prompt:pending,video:pending");

  mockConfig = { ...config, projectId: "prj_grid", readyImageAssetId: null, productAssetId: "ast_product", modelAssetId: "ast_model" };
  mockScenes = [{ ...composed, projectId: "prj_grid", sequenceOrder: 1, targetDurationSeconds: 15, workflowStatus: "PreparingImage", isStale: false }];
  const container = document.createElement("div"); document.body.append(container);
  const root = ReactDOMClient.createRoot(container);
  await React.act(async () => { root.render(React.createElement(VideoProductionTab, { projectId: "prj_grid" })); });
  await React.act(async () => { await new Promise((resolve) => setTimeout(resolve, 100)); });
  const text = container.textContent ?? "";
  assert.match(text, /Ghép ảnh mẫu \+ sản phẩm/);
  assert.match(text, /Ảnh ghép \(trung gian\)/);
  assert.match(text, /Ảnh storyboard 3×3 \(gửi render\)/);
  assert.equal(container.querySelectorAll('img[alt="Ảnh ghép (trung gian)"]').length, 1, "Ảnh ghép trung gian được hiển thị.");
  assert(!container.querySelector('img[alt="Ảnh storyboard 3×3 (gửi render)"]'), "Chưa có ảnh 3×3 thì không hiện ảnh giả.");
  await React.act(async () => root.unmount()); container.remove();
  mockScenes = [];
});

test("Script timeout: polling dừng spinner và đồng bộ badge dự án mà không cần sự kiện", async () => {
  const { ProjectPage } = await import("./ProjectPage");
  mockConfig = { ...mockConfig, projectId: "prj_timeout", workflowMode: "ReadyImage", readyImageAssetId: "ready",
    videoStrategy: "SingleVideo", referenceImageMode: "StoryboardGrid9", runState: "Running", currentRunId: "run_timeout" };
  mockScenes = [];
  const originalInterval = window.setInterval;
  let poll: (() => Promise<void>) | undefined;
  window.setInterval = ((callback: () => Promise<void>, delay: number) => {
    if (delay === 3000) { poll = callback; return 0; }
    return originalInterval(callback, delay);
  }) as typeof window.setInterval;
  const container = document.createElement("div");
  document.body.append(container);
  const root = ReactDOMClient.createRoot(container);
  try {
    await React.act(async () => {
      root.render(React.createElement(ProjectPage, { projectId: "prj_timeout", onBack: () => {} }));
      await new Promise((resolve) => setTimeout(resolve, 80));
    });
    await React.act(async () => { await new Promise((resolve) => setTimeout(resolve, 80)); });
    for (let attempt = 0; attempt < 10 && !container.textContent?.includes("Viết 9 cảnh"); attempt++) {
      await React.act(async () => { await new Promise((resolve) => setTimeout(resolve, 30)); });
    }
    assert.match(container.textContent ?? "", /Viết 9 cảnh/);
    assert(poll, "Workflow đang chạy phải có polling.");
    mockConfig = { ...mockConfig, runState: "Failed" };
    await React.act(async () => { await poll!(); });
    await React.act(async () => { await new Promise((resolve) => setTimeout(resolve, 50)); });
    assert.match(container.textContent ?? "", /Lượt chạy trước đã dừng vì lỗi/);
    assert.doesNotMatch(container.textContent ?? "", /Viết 9 cảnh.*Đang xử lý/);
    assert.match(container.querySelector("header")?.textContent ?? "", /Thất bại/);
    assert.doesNotMatch(container.querySelector("header")?.textContent ?? "", /Đang sản xuất/);
    assert(!container.querySelector(".animate-spin"), "Không còn spinner khi Script đã thất bại.");
  } finally {
    await React.act(async () => root.unmount());
    container.remove();
    window.setInterval = originalInterval;
  }
});
// --- Thử lại đúng bước lỗi (docs/plans/20261102-retry-failed-video-step.md) ---

const gridStoppedConfig = () => ({
  ...mockConfig,
  workflowMode: "ReadyImage",
  readyImageAssetId: "ast_ready",
  videoStrategy: "SingleVideo",
  referenceImageMode: "StoryboardGrid9",
  runState: "Failed",
  currentRunId: "run_01",
  steps: [
    { kind: "script", sceneId: null, jobId: "job-script", jobRevision: 2, outcome: "Succeeded", reasonCode: null, reasonMessage: null, canRetry: false, blockedReason: null, attemptCount: 1 },
    { kind: "grid", sceneId: "scn_01", jobId: "job-grid", jobRevision: 2, outcome: "Succeeded", reasonCode: null, reasonMessage: null, canRetry: false, blockedReason: null, attemptCount: 1 },
    { kind: "prompt", sceneId: "scn_01", jobId: "job-prompt", jobRevision: 3, outcome: "Rejected", reasonCode: null, reasonMessage: "Phản hồi chưa có prompt video hợp lệ.", canRetry: true, blockedReason: null, attemptCount: 1 },
  ],
});

const gridStoppedScene = {
  sceneId: "scn_01", projectId: "prj_test_01", name: "Clip 1", workflowStatus: "NeedsReview",
  gridImageAssetId: "ast_grid", composedImageAssetId: null, motionPromptText: "Prompt cũ còn trong bảng",
  isStale: true, activeJobId: null, videoAssetId: null,
};

test("Thay đúng Chỉ sản phẩm bằng video theo mẫu; giữ ảnh hoàn thiện và chặn capability chưa đạt", async () => {
  const originalConfig = mockConfig;
  const originalScenes = mockScenes;
  mockConfig = { ...originalConfig, projectId: "prj_test_01", workflowMode: "ReadyImage", runState: "Idle", readyImageAssetId: "ready-existing", referenceVideoAssetId: null, referenceVideoAvailable: false };
  mockScenes = [];
  const { container, unmount } = await renderTab();
  try {
    const mode = Array.from(container.querySelectorAll('[role="button"]')).find(e => e.textContent?.includes("Làm theo video mẫu")) as HTMLElement;
    assert(mode);
    assert(container.querySelector('#video-strategy'), "Ảnh hoàn thiện giữ các tùy chọn video cũ.");
    assert(!Array.from(container.querySelectorAll("span")).some(e => e.textContent === "Chỉ sản phẩm"));
    await React.act(async () => mode.click());
    assert(container.querySelector('[aria-label="Dữ liệu video theo mẫu"]'));
    assert.equal(container.querySelector('#video-strategy'), null);
    assert.equal(container.querySelector('#target-duration'), null);
    assert.equal(container.querySelector('input[name="audioOption"]'), null);
    assert.match(container.textContent ?? "", /Chuyển động gần đúng/);
    assert.match(container.textContent ?? "", /tự tách, ghép và co giãn theo độ dài video mới/);
    assert.match(container.textContent ?? "", /Thời lượng có thể khác video mẫu/);
    const start = Array.from(container.querySelectorAll("button")).find(b => b.textContent?.includes("Tạo video theo mẫu"))!;
    assert(start.disabled, "Chưa có video/capability thì không tạo.");
    const oldMode = Array.from(container.querySelectorAll("span")).find(e => e.textContent === "Ảnh đã hoàn thiện")!;
    await React.act(async () => (oldMode.parentElement!.parentElement as HTMLElement).click());
    assert(container.querySelector('#video-strategy'));
    assert(container.querySelector('img[alt="Ảnh đã hoàn thiện"]'), "Ảnh đã hoàn thiện cũ vẫn được giữ.");
  } finally { await unmount(); mockConfig = originalConfig; mockScenes = originalScenes; }
});

test("Video theo mẫu nhập bằng host Video, lưu nháp và mở lại đủ hai đầu vào, không gửi render", async () => {
  const originalConfig = mockConfig;
  const originalScenes = mockScenes;
  mockConfig = { ...originalConfig, projectId: "prj_test_01", workflowMode: "ReferenceVideo", runState: "Idle", videoStrategy: "SingleVideo", referenceImageMode: "Direct", readyImageAssetId: "photo", referenceVideoAssetId: null, referenceVideoAvailable: false };
  mockScenes = [];
  mockUploadedAsset = { assetId: "sample-video", assetType: "Video", projectId: mockConfig.projectId, relativePath: "library/videos/sample.mp4" };
  executedCommands = [];
  const first = await renderTab();
  try {
    const upload = Array.from(first.container.querySelectorAll("button")).find(b => b.textContent === "Tải video từ máy")!;
    await React.act(async () => upload.click());
    await React.act(async () => { await new Promise(r => setTimeout(r, 80)); });
    assert.equal(lastBridgeCommand, "asset.upload");
    assert.equal(lastBridgePayload!.assetType, "Video");
    assert(!lastBridgePayload!.base64Data);
    assert(first.container.querySelector('video[aria-label="Video mẫu chuyển động và âm thanh"]'));
    const save = Array.from(first.container.querySelectorAll("button")).find(b => b.textContent === "Lưu cấu hình")!;
    await React.act(async () => save.click());
    await React.act(async () => { await new Promise(r => setTimeout(r, 80)); });
    assert.equal(mockConfig.referenceVideoAssetId, "sample-video");
    assert.equal(mockConfig.readyImageAssetId, "photo");
    assert.equal(mockConfig.audioOption, "None", "Không lẫn audio riêng với âm thanh trong video mẫu.");
    assert(!executedCommands.includes("workflow.start"));
    await first.unmount();
    const reopened = await renderTab();
    try {
      assert(reopened.container.querySelector('video[aria-label="Video mẫu chuyển động và âm thanh"]'));
      assert(reopened.container.querySelector('img[alt="Ảnh người mẫu mặc sản phẩm"]'));
    } finally { await reopened.unmount(); }
  } finally { if (first.container.isConnected) await first.unmount(); mockUploadedAsset = null; mockConfig = originalConfig; mockScenes = originalScenes; }
});

async function renderTab(): Promise<{ container: HTMLElement; unmount: () => Promise<void> }> {
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);
  await React.act(async () => { root.render(React.createElement(VideoProductionTab, { projectId: "prj_test_01" })); });
  await React.act(async () => { await new Promise((resolve) => setTimeout(resolve, 100)); });
  return { container, unmount: async () => { await React.act(async () => root.unmount()); container.remove(); } };
}

const retryButtons = (container: HTMLElement) =>
  Array.from(container.querySelectorAll("button")).filter((button) => button.textContent === "Thử lại bước này");

test("Prompt bị loại: giữ tiến trình, mốc lỗi đúng bước, retry một bước rồi chạy lại", async () => {
  const originalConfig = mockConfig;
  const originalScenes = mockScenes;
  mockConfig = gridStoppedConfig();
  mockScenes = [gridStoppedScene];
  mockRetryFailure = null;
  retryStepPayloads.length = 0;
  const { container, unmount } = await renderTab();
  try {
    const text = container.textContent ?? "";
    assert.match(text, /Đã dừng vì lỗi/);
    assert.doesNotMatch(text, /Lượt chạy trước đã dừng vì lỗi/, "Không đưa người dùng về form chuẩn bị.");
    assert.match(text, /Tạo ảnh storyboard 3×3\s*· Hoàn tất/);
    assert.match(text, /Viết prompt chuyển động\s*· Lỗi, xem nhật ký/, "Prompt cũ còn trong bảng không làm mốc hiện Hoàn tất.");
    assert.match(text, /Phản hồi chưa có prompt video hợp lệ\./);
    assert(!container.querySelector(".animate-spin"), "Run đã dừng thì không còn spinner.");
    assert.equal(retryButtons(container).length, 1, "Chỉ có nút thử lại ở đúng bước lỗi.");

    await React.act(async () => { retryButtons(container)[0].click(); });
    await React.act(async () => { await new Promise((resolve) => setTimeout(resolve, 100)); });
    assert.deepEqual(retryStepPayloads, [{ projectId: "prj_test_01", jobId: "job-prompt", expectedJobRevision: 3 }]);
    assert.equal(retryButtons(container).length, 0, "Sau khi backend nhận, bước chuyển Chờ chạy và không còn nút.");
    assert.doesNotMatch(container.textContent ?? "", /Đã dừng vì lỗi/);
    assert.match(container.textContent ?? "", /Viết prompt chuyển động\s*· Đang xử lý/, "Prompt cũ còn trong bảng không làm mốc hiện Hoàn tất khi đang thử lại.");
  } finally {
    await unmount();
    mockConfig = originalConfig;
    mockScenes = originalScenes;
  }
});

test("Retry lỗi: xung đột phiên bản chỉ tải lại, lỗi khác hiện lý do và mở lại nút", async () => {
  const originalConfig = mockConfig;
  const originalScenes = mockScenes;
  mockConfig = gridStoppedConfig();
  mockScenes = [gridStoppedScene];
  const { container, unmount } = await renderTab();
  try {
    mockRetryFailure = { code: "CONCURRENCY_CONFLICT", message: "Bước này đã có lần thử mới hơn." };
    await React.act(async () => { retryButtons(container)[0].click(); });
    await React.act(async () => { await new Promise((resolve) => setTimeout(resolve, 100)); });
    assert.doesNotMatch(container.textContent ?? "", /Bước này đã có lần thử mới hơn/, "Bấm lặp không hiện lỗi giả.");
    assert.equal(retryButtons(container).length, 1);

    mockRetryFailure = { code: "VALIDATION_FAILED", message: "Đã thử 3 lần, kết quả vẫn không hợp lệ." };
    await React.act(async () => { retryButtons(container)[0].click(); });
    await React.act(async () => { await new Promise((resolve) => setTimeout(resolve, 100)); });
    assert.match(container.textContent ?? "", /Đã thử 3 lần, kết quả vẫn không hợp lệ\./);
    assert.equal(retryButtons(container).length, 1, "Nút mở lại sau lỗi.");
  } finally {
    mockRetryFailure = null;
    await unmount();
    mockConfig = originalConfig;
    mockScenes = originalScenes;
  }
});

test("Nhiều video: bước không retry được hiện lý do, bước đã hủy không có nút", async () => {
  const originalConfig = mockConfig;
  const originalScenes = mockScenes;
  mockConfig = {
    ...mockConfig,
    workflowMode: "ReadyImage",
    readyImageAssetId: "ast_ready",
    videoStrategy: "MultiVideo",
    referenceImageMode: "Direct",
    runState: "Failed",
    currentRunId: "run_02",
    steps: [
      { kind: "prompt", sceneId: "s-a", jobId: "job-a", jobRevision: 4, outcome: "Failed", reasonCode: "login_required", reasonMessage: null, canRetry: false, blockedReason: "Cần đăng nhập lại tài khoản provider.", attemptCount: 1 },
      { kind: "image", sceneId: "s-b", jobId: "job-b", jobRevision: 2, outcome: "Canceled", reasonCode: "user_canceled", reasonMessage: null, canRetry: false, blockedReason: null, attemptCount: 1 },
      { kind: "render", sceneId: "s-c", jobId: "job-c", jobRevision: 5, outcome: "Failed", reasonCode: "execution_failed", reasonMessage: null, canRetry: true, blockedReason: null, attemptCount: 1 },
    ],
  };
  mockScenes = [
    { sceneId: "s-a", projectId: "prj_test_01", name: "Cảnh A", workflowStatus: "PreparingPrompt", activeJobId: null, videoAssetId: null },
    { sceneId: "s-b", projectId: "prj_test_01", name: "Cảnh B", workflowStatus: "PreparingImage", activeJobId: null, videoAssetId: null },
    { sceneId: "s-c", projectId: "prj_test_01", name: "Cảnh C", workflowStatus: "ReadyForVideo", activeJobId: null, videoAssetId: null },
  ];
  const { container, unmount } = await renderTab();
  try {
    const text = container.textContent ?? "";
    assert.match(text, /Viết prompt chuyển động · Cảnh A/);
    assert.match(text, /Cần đăng nhập lại tài khoản provider\./);
    assert.match(text, /Tạo ảnh cảnh · Cảnh B/);
    assert.match(text, /Tác vụ đã bị hủy\./);
    assert.match(text, /Tạo video · Cảnh C/);
    assert.equal(retryButtons(container).length, 1, "Chỉ bước render của cảnh C retry được.");
    assert(container.querySelector('button[aria-label="Thử lại bước: Tạo video · Cảnh C"]'), "Nút có nhãn truy cập theo bước và cảnh.");
  } finally {
    await unmount();
    mockConfig = originalConfig;
    mockScenes = originalScenes;
  }
});
