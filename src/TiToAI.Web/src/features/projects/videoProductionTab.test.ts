import { domWindow } from "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
let lastBridgeCommand: string | null = null;
let lastBridgePayload: Record<string, unknown> | null = null;
let lastWorkflowConfigPayload: Record<string, unknown> | null = null;
const workflowReadPayloads = new Map<string, { payload: Record<string, unknown> }>();
let executedCommands: string[] = [];

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

        switch (req.command) {
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
                templateText: "Kịch bản video TikTok về {chu_de}.",
                variableDefinitionsJson: "[]",
                createdAtUtc: "2026-10-27T00:00:00Z",
              },
            };
            break;
          case "workflow.get_config":
            responseData = mockConfig;
            break;
          case "workflow.list_scenes":
            responseData = { scenes: mockScenes };
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
              asset: {
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
          default:
            responseData = null;
        }

        const respEnvelope = {
          id: req.id,
          type: "response",
          success,
          data: responseData,
          error: null,
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

test("Prompt đã sẵn sàng hiển thị trạng thái chờ nhà cung cấp video", async () => {
  mockConfig = { ...mockConfig, runState: "Running" };
  mockScenes = [{ sceneId: "s-ready", projectId: "prj_test_01", name: "Cảnh 1", workflowStatus: "ReadyForVideo", motionPromptText: "Camera pan", activeJobId: null, videoAssetId: null }];
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOMClient.createRoot(container);
  await React.act(async () => { root.render(React.createElement(VideoProductionTab, { projectId: "prj_test_01" })); });
  await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 100)); });
  assert.match(container.textContent ?? "", /Chức năng tạo video đang chờ nhà cung cấp được kiểm chứng/);
  assert.doesNotMatch(container.textContent ?? "", /Đang tạo video/);
  await React.act(async () => root.unmount());
  container.remove();
  mockConfig = { ...mockConfig, runState: "Idle" };
  mockScenes = [];
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
  assert(!options.includes("30"), "CD05: Option 30s không được xuất hiện khi chưa verified direct.");

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
  localStorage.removeItem("titoai_workflow_prompt_values_prj_test_02");
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

test("VideoProductionTab: Render biến ở frontend và chỉ lưu biến riêng của dự án", async () => {
  executedCommands = [];
  localStorage.removeItem("titoai_workflow_prompt_values_prj_test_dyn_vars");
  mockPromptTemplate = {
    template: {
      templateId: "tpl_script_fashion",
      purpose: "Script",
      createdAtUtc: "2026-10-27T00:00:00Z",
      updatedAtUtc: "2026-10-27T00:00:00Z",
    },
    content: {
      templateId: "tpl_script_fashion",
      templateText: "Tạo kịch bản video TikTok về chủ đề {chu_de} cho sản phẩm {san_pham}.",
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
    videoGuidance: null,
    promptVariables: null,
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
  assert(badgeArea.includes("Toàn cục"), "Phải hiển thị phạm vi template toàn cục.");

  // 2. Kiểm tra ô nhập dynamic variable {chu_de} có id 'var-input-chu_de'
  const chuDeInput = container.querySelector("#var-input-chu_de") as HTMLInputElement;
  assert(chuDeInput, "Phải hiển thị ô nhập liệu id='var-input-chu_de' cho biến {chu_de}.");

  // Kiểm tra ô nhập {san_pham}
  const sanPhamInput = container.querySelector("#var-input-san_pham") as HTMLInputElement;
  assert(sanPhamInput, "Phải hiển thị ô nhập liệu id='var-input-san_pham' cho biến {san_pham}.");

  await React.act(async () => {
    const nativeInputValueSetter = Object.getOwnPropertyDescriptor(
      window.HTMLInputElement.prototype,
      "value"
    )?.set;
    nativeInputValueSetter?.call(sanPhamInput, "Áo lụa cao cấp");
    sanPhamInput.dispatchEvent(new Event("input", { bubbles: true }));
  });

  // 3. Nhập giá trị cho {chu_de}
  await React.act(async () => {
    const nativeInputValueSetter = Object.getOwnPropertyDescriptor(
      window.HTMLInputElement.prototype,
      "value"
    )?.set;
    nativeInputValueSetter?.call(chuDeInput, "Thời trang dạo phố mùa hè");
    chuDeInput.dispatchEvent(new Event("input", { bubbles: true }));
  });

  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  // 4. Bấm nút "Lưu cấu hình"
  const saveButton = Array.from(container.querySelectorAll("button")).find((b) =>
    b.textContent?.includes("Lưu cấu hình")
  );
  assert(saveButton, "Phải có nút 'Lưu cấu hình'.");
  assert(!saveButton.disabled, "Nút Lưu cấu hình phải enabled khi có thay đổi.");

  await React.act(async () => {
    saveButton.click();
  });

  await React.act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  // Kiểm tra lệnh workflow.save_config chỉ gửi biến riêng của dự án và hướng dẫn riêng (không gửi text đã render)
  assert(executedCommands.includes("workflow.save_config"), "Bấm lưu phải gọi 'workflow.save_config'.");
  assert(lastBridgePayload, "Phải có payload gửi sang bridge.");
  assert.deepStrictEqual((lastBridgePayload as any)?.promptVariables, {
    chu_de: "Thời trang dạo phố mùa hè",
    san_pham: "Áo lụa cao cấp",
  }, "Payload phải chứa đúng biến riêng của dự án.");
  assert.strictEqual((lastBridgePayload as any)?.scriptGuidance, "",
    "Hướng dẫn kịch bản riêng để trống thì gửi chuỗi rỗng.");
  assert.strictEqual("scriptPromptText" in (lastBridgePayload as any), false,
    "Payload không còn gửi text mẫu đã render.");

  // 5. Bấm bắt đầu dùng biến đã lưu và khởi chạy workflow
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

  assert(executedCommands.includes("workflow.start"), "Bấm bắt đầu phải kích hoạt 'workflow.start'.");
  assert.deepStrictEqual(mockConfig.promptVariables, {
    chu_de: "Thời trang dạo phố mùa hè",
    san_pham: "Áo lụa cao cấp",
  }, "Config phải giữ biến riêng của dự án sau khi workflow bắt đầu.");

  React.act(() => root.unmount());
  container.remove();
  mockPromptTemplate = null;

  React.act(() => root.unmount());
  container.remove();
});
