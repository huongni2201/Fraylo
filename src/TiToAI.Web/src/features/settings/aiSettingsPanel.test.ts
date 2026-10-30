import { domWindow } from "../../test/setupDom.ts";
const mockProviders = [
  {
    providerKey: "gemini-web",
    displayName: "Gemini Web",
    supportedTasks: ["Script", "Image", "VideoPrompt"],
    verificationStatus: "VERIFIED",
    sessionStatus: "Connected",
  },
  {
    providerKey: "chatgpt-web",
    displayName: "ChatGPT Web",
    supportedTasks: ["Script", "Image", "VideoPrompt"],
    verificationStatus: "NOT_VERIFIED",
    sessionStatus: "Unverified",
  },
];

const mockAccounts = [
  {
    id: "acc_gemini_1",
    providerKey: "gemini-web",
    profileId: "prof_gemini_1",
    displayName: "Gemini Advanced Creator",
    verifiedIdentity: "creator@gmail.com",
    enabled: true,
    lifecycle: "Active",
    maxConcurrentJobs: 3,
    maxTabs: 3,
    revision: 1,
  },
  {
    id: "acc_chatgpt_1",
    providerKey: "chatgpt-web",
    profileId: "prof_chatgpt_1",
    displayName: "ChatGPT Team",
    verifiedIdentity: null,
    enabled: true,
    lifecycle: "Active",
    maxConcurrentJobs: 2,
    maxTabs: 2,
    revision: 1,
  },
];

const mockTasks = [
  {
    taskType: "Script",
    providerKey: "gemini-web",
    verificationStatus: "VERIFIED",
    automationSupported: true,
    manualSupported: false,
  },
  {
    taskType: "Script",
    providerKey: "chatgpt-web",
    verificationStatus: "NOT_VERIFIED",
    automationSupported: false,
    manualSupported: false,
  },
];

let currentSavedSettings = {
  documentsPath: "D:/storage/documents",
  imagesPath: "D:/storage/images",
  videosPath: "D:/storage/videos",
  imageProvider: "gemini-web",
  scriptProvider: "gemini-web",
  videoPromptProvider: null as string | null,
  maxOpenBrowsers: 4,
  maxConcurrentJobs: 8,
  revision: 1,
};

let lastSavedRequestPayload: any = null;
let simulateSaveErrorType: "NONE" | "CONCURRENCY_CONFLICT" | "VALIDATION_FAILED" = "NONE";

// Setup WebView2 message handling
const messageListeners: ((event: MessageEvent) => void)[] = [];
(domWindow as unknown as Record<string, unknown>).chrome = {
  webview: {
    addEventListener(type: string, listener: (event: MessageEvent) => void) {
      if (type === "message") messageListeners.push(listener);
    },
    removeEventListener(_type: string, listener: (event: MessageEvent) => void) {
      const idx = messageListeners.indexOf(listener);
      if (idx >= 0) messageListeners.splice(idx, 1);
    },
    postMessage: (msg: unknown) => {
      const req = msg as { id: string; command: string; payload?: any };
      let payloadData: unknown = {};
      let isSuccess = true;
      let errorObj: any = null;

      if (req.command === "settings.get") {
        payloadData = { ...currentSavedSettings };
      } else if (req.command === "provider.list") {
        payloadData = {
          providers: mockProviders,
          accounts: mockAccounts,
          tasks: mockTasks,
        };
      } else if (req.command === "settings.save") {
        lastSavedRequestPayload = req.payload;

        if (simulateSaveErrorType === "CONCURRENCY_CONFLICT") {
          isSuccess = false;
          errorObj = {
            code: "CONCURRENCY_CONFLICT",
            message: "Cài đặt đã bị thay đổi ở một phiên khác. Bạn có thể tải lại để lấy mới nhất.",
            details: { currentRevision: currentSavedSettings.revision + 1 },
          };
        } else if (simulateSaveErrorType === "VALIDATION_FAILED") {
          isSuccess = false;
          errorObj = {
            code: "VALIDATION_FAILED",
            message: "Hạn mức cấu hình không hợp lệ.",
            fieldErrors: {
              maxOpenBrowsers: ["Số trình duyệt mở tối đa không được vượt quá 16."],
            },
          };
        } else {
          currentSavedSettings = {
            ...currentSavedSettings,
            scriptProvider: req.payload?.scriptProvider ?? null,
            imageProvider: req.payload?.imageProvider ?? null,
            videoPromptProvider: req.payload?.videoPromptProvider ?? null,
            maxOpenBrowsers: req.payload?.maxOpenBrowsers ?? currentSavedSettings.maxOpenBrowsers,
            maxConcurrentJobs: req.payload?.maxConcurrentJobs ?? currentSavedSettings.maxConcurrentJobs,
            revision: currentSavedSettings.revision + 1,
          };
          payloadData = { ...currentSavedSettings };
        }
      }

      setTimeout(() => {
        const ev = {
          data: {
            id: req.id,
            type: "response",
            success: isSuccess,
            contractVersion: "1.1.0",
            data: isSuccess ? payloadData : undefined,
            error: isSuccess ? undefined : errorObj,
          },
        } as MessageEvent;
        for (const listener of [...messageListeners]) {
          listener(ev);
        }
      }, 0);
    },
  },
};

// Đảm bảo "provider.list" nằm trong allow-list để test chạy độc lập trước khi coordinator merge bridge.ts
const { bridgeCommandAllowlist } = await import("../../contracts/bridge.ts");
if (!bridgeCommandAllowlist.includes("provider.list" as any)) {
  (bridgeCommandAllowlist as string[]).push("provider.list");
}

// Dynamic imports
const React = (await import("react")).default;
const { act } = await import("react");
const { createRoot } = await import("react-dom/client");
const { AiProviderSettingsPanel } = await import("./AiProviderSettingsPanel.tsx");

function setNativeSelectValue(element: HTMLSelectElement, value: string) {
  element.value = value;
  element.dispatchEvent(new Event("change", { bubbles: true }));
}

function setNativeInputValue(element: HTMLInputElement, value: string) {
  const proto = Object.getPrototypeOf(element);
  const setter = Object.getOwnPropertyDescriptor(proto, "value")?.set;
  if (setter) {
    setter.call(element, value);
  } else {
    element.value = value;
  }
  element.dispatchEvent(new Event("input", { bubbles: true }));
  element.dispatchEvent(new Event("change", { bubbles: true }));
}

async function runAiSettingsPanelTests() {
  console.log("=== BẮT ĐẦU KIỂM THỬ AI PROVIDER SETTINGS PANEL TRÊN DOM THẬT ===");

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = createRoot(container);

  let dirtyStates: boolean[] = [];
  let accountsNavigated = false;
  let resetSignalCount = 0;

  const renderComponent = async (props = {}) => {
    await act(async () => {
      root.render(
        React.createElement(AiProviderSettingsPanel, {
          onDirtyChange: (d: boolean) => dirtyStates.push(d),
          onNavigateToAccounts: () => {
            accountsNavigated = true;
          },
          resetSignal: resetSignalCount,
          ...props,
        })
      );
    });
    // Chờ các Promise fetch dữ liệu hoàn tất
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50));
    });
  };

  await renderComponent();

  // =========================================================================
  // TEST 1: Khởi tạo render 3 dropdowns và 2 trường budget, hiển thị đúng dữ liệu đã lưu
  // =========================================================================
  console.log("\n--- [TEST 1] Khởi tạo render 3 dropdowns, 2 budgets & thẻ Video Render ---");

  const scriptSelect = container.querySelector<HTMLSelectElement>("#script-provider");
  const imageSelect = container.querySelector<HTMLSelectElement>("#image-provider");
  const videoPromptSelect = container.querySelector<HTMLSelectElement>("#video-prompt-provider");
  const maxBrowsersInput = container.querySelector<HTMLInputElement>("#max-open-browsers");
  const maxJobsInput = container.querySelector<HTMLInputElement>("#max-concurrent-jobs");

  if (!scriptSelect) throw new Error("FAIL: Không tìm thấy #script-provider");
  if (!imageSelect) throw new Error("FAIL: Không tìm thấy #image-provider");
  if (!videoPromptSelect) throw new Error("FAIL: Không tìm thấy #video-prompt-provider");
  if (!maxBrowsersInput) throw new Error("FAIL: Không tìm thấy #max-open-browsers");
  if (!maxJobsInput) throw new Error("FAIL: Không tìm thấy #max-concurrent-jobs");

  if (scriptSelect.value !== "gemini-web") {
    throw new Error(`FAIL: scriptSelect kỳ vọng 'gemini-web', nhận: '${scriptSelect.value}'`);
  }
  if (imageSelect.value !== "gemini-web") {
    throw new Error(`FAIL: imageSelect kỳ vọng 'gemini-web', nhận: '${imageSelect.value}'`);
  }
  if (videoPromptSelect.value !== "") {
    throw new Error(`FAIL: videoPromptSelect kỳ vọng '', nhận: '${videoPromptSelect.value}'`);
  }
  if (maxBrowsersInput.value !== "4") {
    throw new Error(`FAIL: maxBrowsersInput kỳ vọng '4', nhận: '${maxBrowsersInput.value}'`);
  }
  if (maxJobsInput.value !== "8") {
    throw new Error(`FAIL: maxJobsInput kỳ vọng '8', nhận: '${maxJobsInput.value}'`);
  }

  // Kiểm tra thẻ thông tin Video Render MuseAI cố định
  const videoRenderCardText = container.textContent || "";
  if (!videoRenderCardText.includes("Tác vụ Tạo Video (Video Render)")) {
    throw new Error("FAIL: Không tìm thấy mục Tác vụ Tạo Video (Video Render)");
  }
  if (!videoRenderCardText.includes("Cố định qua MuseAI")) {
    throw new Error("FAIL: Không tìm thấy huy hiệu Cố định qua MuseAI");
  }

  // Trạng thái dirty ban đầu phải là false
  const initialDirty = dirtyStates[dirtyStates.length - 1];
  if (initialDirty !== false) {
    throw new Error(`FAIL: isDirty ban đầu phải là false, thực tế: ${initialDirty}`);
  }

  console.log("  -> [PASS TEST 1] Render đủ 3 dropdowns, 2 budgets, thẻ MuseAI cố định và dirty = false.");

  // =========================================================================
  // TEST 2: Thay đổi lựa chọn dropdown kích hoạt dirty state
  // =========================================================================
  console.log("\n--- [TEST 2] Thay đổi lựa chọn dropdown kích hoạt dirty state ---");

  dirtyStates = [];
  await act(async () => {
    setNativeSelectValue(videoPromptSelect, "chatgpt-web");
  });

  const changedDirty = dirtyStates[dirtyStates.length - 1];
  if (changedDirty !== true) {
    throw new Error(`FAIL: Sau khi chọn videoPromptSelect thành 'chatgpt-web', isDirty phải là true, thực tế: ${changedDirty}`);
  }

  // Kiểm tra nút Lưu và Hủy chuyển sang trạng thái enabled
  const saveBtn = Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(
    (b) => b.textContent?.includes("Lưu cài đặt")
  );
  const discardBtn = Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(
    (b) => b.textContent?.includes("Hủy / Khôi phục")
  );

  if (!saveBtn || saveBtn.disabled) {
    throw new Error("FAIL: Nút 'Lưu cài đặt' phải được kích hoạt (enabled) khi form dirty");
  }
  if (!discardBtn || discardBtn.disabled) {
    throw new Error("FAIL: Nút 'Hủy / Khôi phục' phải được kích hoạt (enabled) khi form dirty");
  }

  console.log("  -> [PASS TEST 2] Thay đổi dropdown kích hoạt dirty state = true và kích hoạt các nút Lưu/Hủy.");

  // =========================================================================
  // TEST 3: Lưu cài đặt thành công gọi đúng API với expectedRevision và cập nhật state
  // =========================================================================
  console.log("\n--- [TEST 3] Lưu cài đặt thành công gọi đúng API với expectedRevision và cập nhật state ---");

  dirtyStates = [];
  simulateSaveErrorType = "NONE";

  await act(async () => {
    saveBtn.click();
  });

  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  if (!lastSavedRequestPayload) {
    throw new Error("FAIL: settings.save không được gọi");
  }
  if (lastSavedRequestPayload.expectedRevision !== 1) {
    throw new Error(`FAIL: expectedRevision kỳ vọng 1, nhận: ${lastSavedRequestPayload.expectedRevision}`);
  }
  if (lastSavedRequestPayload.videoPromptProvider !== "chatgpt-web") {
    throw new Error(`FAIL: videoPromptProvider gửi đi kỳ vọng 'chatgpt-web', nhận: ${lastSavedRequestPayload.videoPromptProvider}`);
  }
  if (!lastSavedRequestPayload.clientRequestId) {
    throw new Error("FAIL: settings.save phải gửi kèm clientRequestId");
  }

  // Dirty state sau khi lưu thành công phải về false
  const dirtyAfterSave = dirtyStates[dirtyStates.length - 1];
  if (dirtyAfterSave !== false) {
    throw new Error(`FAIL: Sau khi lưu thành công, isDirty phải là false, nhận: ${dirtyAfterSave}`);
  }

  // Hiển thị thông báo thành công xanh
  const successAlert = container.querySelector('[role="alert"]');
  if (!successAlert || !successAlert.textContent?.includes("Đã lưu thành công")) {
    throw new Error(`FAIL: Kỳ vọng thông báo thành công trong DOM, nhận: ${successAlert?.textContent}`);
  }

  console.log("  -> [PASS TEST 3] Lưu cài đặt thành công: gửi đúng expectedRevision=1, clientRequestId và cập nhật state sạch.");

  // =========================================================================
  // TEST 4: Lỗi lưu / xung đột phiên bản bảo toàn nguyên vẹn bản nháp của người dùng trong form
  // =========================================================================
  console.log("\n--- [TEST 4] Lỗi lưu / xung đột phiên bản bảo toàn nguyên vẹn bản nháp của người dùng ---");

  // Người dùng điều chỉnh: đổi maxOpenBrowsers thành 6, maxConcurrentJobs thành 12, scriptProvider thành chatgpt-web
  await act(async () => {
    setNativeInputValue(maxBrowsersInput, "6");
    setNativeInputValue(maxJobsInput, "12");
    setNativeSelectValue(scriptSelect, "chatgpt-web");
  });

  if (maxBrowsersInput.value !== "6") throw new Error("FAIL: maxBrowsersInput chưa nhận 6");
  if (maxJobsInput.value !== "12") throw new Error("FAIL: maxJobsInput chưa nhận 12");
  if (scriptSelect.value !== "chatgpt-web") throw new Error("FAIL: scriptSelect chưa nhận chatgpt-web");

  // Giả lập lỗi CONCURRENCY_CONFLICT
  simulateSaveErrorType = "CONCURRENCY_CONFLICT";

  const saveBtnRetry = Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(
    (b) => b.textContent?.includes("Lưu cài đặt")
  );
  if (!saveBtnRetry) throw new Error("FAIL: Không tìm thấy nút Lưu cài đặt");

  await act(async () => {
    saveBtnRetry.click();
  });

  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });

  // Kiểm tra banner cảnh báo xung đột phiên bản xuất hiện
  const conflictBanner = container.querySelector('[role="alert"]');
  if (!conflictBanner || !conflictBanner.textContent?.includes("Cài đặt đã bị thay đổi ở một phiên khác")) {
    throw new Error(`FAIL: Kỳ vọng banner xung đột phiên bản, thực tế nhận: ${conflictBanner?.textContent}`);
  }

  // BẢO TOÀN NGUYÊN VẸN CÁC LỰA CHỌN TRONG FORM DRAFT (Không được reset form)
  if (maxBrowsersInput.value !== "6") {
    throw new Error(`FAIL: Bản nháp maxBrowsersInput bị mất sau lỗi! Kỳ vọng '6', thực tế: '${maxBrowsersInput.value}'`);
  }
  if (maxJobsInput.value !== "12") {
    throw new Error(`FAIL: Bản nháp maxJobsInput bị mất sau lỗi! Kỳ vọng '12', thực tế: '${maxJobsInput.value}'`);
  }
  if (scriptSelect.value !== "chatgpt-web") {
    throw new Error(`FAIL: Bản nháp scriptSelect bị mất sau lỗi! Kỳ vọng 'chatgpt-web', thực tế: '${scriptSelect.value}'`);
  }

  console.log("  -> [PASS TEST 4] Xung đột phiên bản hiển thị banner cảnh báo và BẢO TOÀN NGUYÊN VẸN bản nháp.");

  // =========================================================================
  // TEST 5: Bấm Hủy khôi phục lại dữ liệu đã lưu ban đầu
  // =========================================================================
  console.log("\n--- [TEST 5] Bấm Hủy khôi phục lại dữ liệu đã lưu ban đầu ---");

  const discardBtnAction = Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(
    (b) => b.textContent?.includes("Hủy / Khôi phục")
  );
  if (!discardBtnAction) throw new Error("FAIL: Không tìm thấy nút Hủy / Khôi phục");

  dirtyStates = [];
  await act(async () => {
    discardBtnAction.click();
  });

  // Kiểm tra các trường được khôi phục về cấu hình đã lưu (savedSettings revision 2: script=gemini-web, browsers=4, jobs=8)
  if (scriptSelect.value !== "gemini-web") {
    throw new Error(`FAIL: scriptSelect sau khi Hủy phải là 'gemini-web', nhận: '${scriptSelect.value}'`);
  }
  if (maxBrowsersInput.value !== "4") {
    throw new Error(`FAIL: maxBrowsersInput sau khi Hủy phải là '4', nhận: '${maxBrowsersInput.value}'`);
  }
  if (maxJobsInput.value !== "8") {
    throw new Error(`FAIL: maxJobsInput sau khi Hủy phải là '8', nhận: '${maxJobsInput.value}'`);
  }

  const dirtyAfterDiscard = dirtyStates[dirtyStates.length - 1];
  if (dirtyAfterDiscard !== false) {
    throw new Error(`FAIL: Sau khi bấm Hủy, isDirty phải là false, nhận: ${dirtyAfterDiscard}`);
  }

  // Thử nghiệm resetSignal từ cha
  await act(async () => {
    setNativeInputValue(maxBrowsersInput, "10");
  });
  if (maxBrowsersInput.value !== "10") throw new Error("FAIL: Không cập nhật được maxBrowsers");

  resetSignalCount++;
  await renderComponent({ resetSignal: resetSignalCount });

  if (maxBrowsersInput.value !== "4") {
    throw new Error(`FAIL: resetSignal phải khôi phục maxBrowsers về 4, nhận: ${maxBrowsersInput.value}`);
  }

  console.log("  -> [PASS TEST 5] Bấm Hủy và resetSignal khôi phục chính xác dữ liệu đã lưu, reset dirty state.");

  // =========================================================================
  // TEST 6: Provider NOT_VERIFIED hiển thị đúng nhãn automation chưa sẵn sàng
  // =========================================================================
  console.log("\n--- [TEST 6] Provider NOT_VERIFIED hiển thị đúng nhãn automation chưa sẵn sàng ---");

  // Khi chọn Gemini Web (đã xác minh):
  await act(async () => {
    setNativeSelectValue(scriptSelect, "gemini-web");
  });

  const verificationBadges = Array.from(container.querySelectorAll('[data-testid="verification-badge"]'));
  const scriptBadge = verificationBadges[0];
  if (!scriptBadge || !scriptBadge.textContent?.includes("Đã xác minh (Tự động hóa hoàn toàn)")) {
    throw new Error(`FAIL: Gemini Web kỳ vọng huy hiệu 'Đã xác minh', nhận: '${scriptBadge?.textContent}'`);
  }

  // Khi chọn ChatGPT Web (chưa xác minh):
  await act(async () => {
    setNativeSelectValue(scriptSelect, "chatgpt-web");
  });

  const updatedBadges = Array.from(container.querySelectorAll('[data-testid="verification-badge"]'));
  const unverifiedBadge = updatedBadges[0];
  if (!unverifiedBadge || !unverifiedBadge.textContent?.includes("Chưa xác minh — Tự động hóa chưa sẵn sàng")) {
    throw new Error(`FAIL: ChatGPT Web kỳ vọng 'Chưa xác minh — Tự động hóa chưa sẵn sàng', nhận: '${unverifiedBadge?.textContent}'`);
  }

  // Kiểm tra liên kết điều hướng sang Cài đặt -> Tài khoản
  const navLink = container.querySelector<HTMLButtonElement>("button:has(svg)");
  const accountLinks = Array.from(container.querySelectorAll("button")).filter(
    (b) => b.textContent?.includes("Quản lý tài khoản tại Cài đặt → Tài khoản") || b.textContent?.includes("Quản lý hoặc đăng nhập")
  );
  if (accountLinks.length === 0) {
    throw new Error("FAIL: Không tìm thấy liên kết hướng dẫn tới Cài đặt → Tài khoản");
  }

  accountsNavigated = false;
  await act(async () => {
    accountLinks[0].click();
  });
  if (!accountsNavigated) {
    throw new Error("FAIL: Bấm liên kết phải gọi onNavigateToAccounts");
  }

  console.log("  -> [PASS TEST 6] NOT_VERIFIED hiển thị đúng 'Tự động hóa chưa sẵn sàng', VERIFIED hiển thị tự động hóa, liên kết tài khoản hoạt động.");

  console.log("\n=================================================================");
  console.log(">>> TOÀN BỘ 6 BỘ KIỂM THỬ AI PROVIDER SETTINGS ĐÃ VƯỢT QUA (PASS 100%) <<<");
  console.log("=================================================================\n");
}

runAiSettingsPanelTests().catch((err) => {
  console.error("TEST FAILED:", err);
  process.exit(1);
});
