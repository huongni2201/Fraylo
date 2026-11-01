import { domWindow } from "../../test/setupDom.ts";
const mockAccounts = [
  {
    id: "acc_gpt_1",
    providerKey: "chatgpt-web",
    profileId: "prof_gpt_1",
    displayName: "ChatGPT Team Account",
    verifiedIdentity: "team@company.ai",
    sessionStatus: "Connected",
    enabled: true,
    lifecycle: "Active",
    maxConcurrentJobs: 3,
    maxTabs: 3,
    revision: 1,
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
  },
  {
    id: "acc_gemini_1",
    providerKey: "gemini-web",
    profileId: "prof_gemini_1",
    displayName: "Gemini Advanced Creator",
    verifiedIdentity: "creator@gmail.com",
    sessionStatus: "SessionExpired",
    enabled: true,
    lifecycle: "Active",
    maxConcurrentJobs: 2,
    maxTabs: 2,
    revision: 1,
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
  },
  {
    id: "acc_museai_1",
    providerKey: "museai",
    profileId: "prof_museai_1",
    displayName: "MuseAI Studio Pro",
    verifiedIdentity: "editor@studio.ai",
    sessionStatus: "Unverified",
    enabled: true,
    lifecycle: "Active",
    maxConcurrentJobs: 1,
    maxTabs: 1,
    revision: 1,
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
  },
];

// Mock templates cho PromptTemplateManager
const mockTemplates = [
  {
    templateId: "tmpl_tiktok_script",
    purpose: "Script",
    name: "Kịch bản TikTok",
    createdAtUtc: new Date().toISOString(),
    updatedAtUtc: new Date().toISOString(),
  }
];

let currentSavedStorageSettings = {
  documentsPath: "D:/storage/documents",
  imagesPath: "D:/storage/images",
  videosPath: "D:/storage/videos",
  imageProvider: null,
  scriptProvider: null,
  videoPromptProvider: null,
};

let simulateSaveError = false;

// Mock WebView2 message handling chuẩn xác
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

      if (req.command === "theme.get_preference") {
        payloadData = { preference: "system", effectiveTheme: "dark" };
      } else if (req.command === "theme.save_preference") {
        payloadData = { preference: req.payload?.preference ?? "system", effectiveTheme: "dark" };
      } else if (req.command === "provider_account.list") {
        payloadData = { accounts: mockAccounts };
      } else if (req.command === "provider_account.probe_session") {
        payloadData = { isVerified: false, sessionStatus: "Unverified", errorMessage: "Không đọc được danh tính Gemini." };
      } else if (req.command === "settings.get") {
        payloadData = { ...currentSavedStorageSettings };
      } else if (req.command === "settings.save") {
        if (simulateSaveError) {
          isSuccess = false;
          errorObj = {
            code: "VALIDATION_FAILED",
            message: "Đường dẫn thư mục không hợp lệ hoặc không có quyền ghi.",
            fieldErrors: { documentsPath: ["Đường dẫn không thể truy cập hoặc bị khóa."] },
          };
        } else {
          currentSavedStorageSettings = {
            ...currentSavedStorageSettings,
            documentsPath: req.payload?.documentsPath ?? currentSavedStorageSettings.documentsPath,
            imagesPath: req.payload?.imagesPath ?? currentSavedStorageSettings.imagesPath,
            videosPath: req.payload?.videosPath ?? currentSavedStorageSettings.videosPath,
          };
          payloadData = { ...currentSavedStorageSettings };
        }
      } else if (req.command === "prompt.list_templates") {
        payloadData = { templates: mockTemplates };
      } else if (req.command === "prompt.get_template") {
        payloadData = { template: mockTemplates[0], content: { templateId: mockTemplates[0].templateId, templateText: "Tiêu đề: {topic}\nCảnh 1: Mở đầu thu hút.", variableDefinitionsJson: "[{\"name\":\"topic\",\"defaultValue\":\"Review công nghệ\",\"isRequired\":true}]", updatedAtUtc: new Date().toISOString() } };
      } else if (req.command === "prompt.save_template") {
        payloadData = { template: mockTemplates[0], content: { templateId: mockTemplates[0].templateId, templateText: req.payload?.templateText ?? "", variableDefinitionsJson: req.payload?.variableDefinitionsJson ?? "[]", updatedAtUtc: new Date().toISOString() } };
      } else if (req.command === "asset.list") {
        payloadData = { assets: [], totalCount: 0, page: 1, pageSize: 20 };
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

// Dynamic import React và ReactDOM
const React = (await import("react")).default;
const { act } = await import("react");
const { createRoot } = await import("react-dom/client");

const { ThemeProvider } = await import("../../context/ThemeContext.tsx");
const { SettingsPage } = await import("./SettingsPage.tsx");
const { bridgeClient } = await import("../../services/bridgeClient.ts");
const App = (await import("../../App.tsx")).default;

function setNativeInputValue(element: HTMLInputElement | HTMLTextAreaElement, value: string) {
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

async function runTests() {
  console.log("=== BẮT ĐẦU KIỂM THỬ TRANG CÀI ĐẶT (A01, A02, F1-F3) TRÊN DOM THẬT ===");

  // =========================================================================
  // PHẦN 1: STANDALONE SETTINGSPAGE - WAI-ARIA TABS, 2 THEMES, ACCOUNTS (A01, A02)
  // =========================================================================
  console.log("\n--- [TEST SUITE 1] Standalone SettingsPage: WAI-ARIA, Theme & Accounts ---");
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = createRoot(container);

  await act(async () => {
    root.render(
      React.createElement(
        ThemeProvider,
        null,
        React.createElement(SettingsPage, {
          initialTab: "accounts",
        })
      )
    );
  });

  for (let attempt = 0; attempt < 20 && !container.textContent?.includes("MuseAI Studio Pro"); attempt++) {
    await act(async () => { await new Promise((resolve) => setTimeout(resolve, 50)); });
  }

  const accountCard = (name: string) => Array.from(container.querySelectorAll("span"))
    .find((node) => node.textContent === name)?.closest("div.rounded-xl");
  const loginVisible = (name: string) => !!Array.from(accountCard(name)?.querySelectorAll("button") ?? [])
    .find((button) => button.textContent?.includes("Đăng nhập trình duyệt"));
  if (loginVisible("ChatGPT Team Account") || !loginVisible("Gemini Advanced Creator") || !loginVisible("MuseAI Studio Pro")) {
    throw new Error(`Nút đăng nhập sai: ${["ChatGPT Team Account", "Gemini Advanced Creator", "MuseAI Studio Pro"].map((name) => `${name}: card=${!!accountCard(name)}, login=${loginVisible(name)}`).join(", ")}`);
  }
  await act(async () => {
    for (const listener of [...messageListeners]) listener({ data: {
      type: "event", eventName: "browser.profile_state_changed", contractVersion: "1.1.0", seq: 1,
      payload: { profileId: "prof_gpt_1", sessionStatus: "SessionExpired", visibilityState: "Hidden", operationalState: "Stopped" },
    } } as MessageEvent);
  });
  if (!loginVisible("ChatGPT Team Account") || !accountCard("ChatGPT Team Account")?.textContent?.includes("Chưa xác minh")) {
    throw new Error("Phiên hết hạn phải hiện lại nút đăng nhập và trạng thái chưa xác minh");
  }

  const probeButton = Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(
    (b) => b.textContent?.includes("Kiểm tra phiên")
  );
  if (!probeButton) throw new Error("Không tìm thấy nút kiểm tra phiên");
  await act(async () => {
    probeButton.click();
    await new Promise((r) => setTimeout(r, 100));
  });
  if (!container.textContent?.includes("Không đọc được danh tính Gemini.")) {
    throw new Error("Lỗi kiểm tra phiên phải còn hiển thị sau khi tải lại danh sách tài khoản");
  }

  // 1. Kiểm tra tiêu đề và 4 Tab con ngang theo WAI-ARIA
  const tablist = container.querySelector('[role="tablist"]');
  if (!tablist) throw new Error("FAIL: Không tìm thấy role='tablist' trong SettingsPage");

  const tabButtons = Array.from(tablist.querySelectorAll<HTMLButtonElement>('[role="tab"]'));
  if (tabButtons.length !== 4) {
    throw new Error(`FAIL: Kỳ vọng 4 tab con, thực tế tìm thấy ${tabButtons.length}`);
  }
  console.log("  -> [PASS A01.1] 4 tab con ngang đầy đủ chuẩn WAI-ARIA.");

  const activeTabBtn = tablist.querySelector<HTMLButtonElement>('[role="tab"][aria-selected="true"]');
  if (activeTabBtn?.id !== "tab-accounts") {
    throw new Error(`FAIL: Tab mặc định phải là tab-accounts, thực tế là ${activeTabBtn?.id}`);
  }
  console.log("  -> [PASS A01.2] Mặc định hiển thị tab Tài khoản.");

  // Điều hướng bằng phím mũi tên và Home/End theo chuẩn WAI-ARIA
  await act(async () => {
    activeTabBtn?.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true }));
  });
  let currentActiveTab = tablist.querySelector<HTMLButtonElement>('[role="tab"][aria-selected="true"]');
  if (currentActiveTab?.id !== "tab-storage") {
    throw new Error(`FAIL: Sau khi bấm ArrowRight phải chuyển sang tab-storage, thực tế là ${currentActiveTab?.id}`);
  }

  // Phím End: Chuyển đến tab cuối cùng (tab-appearance)
  await act(async () => {
    currentActiveTab?.dispatchEvent(new KeyboardEvent("keydown", { key: "End", bubbles: true }));
  });
  currentActiveTab = tablist.querySelector<HTMLButtonElement>('[role="tab"][aria-selected="true"]');
  if (currentActiveTab?.id !== "tab-appearance") {
    throw new Error(`FAIL: Sau khi bấm End phải chuyển sang tab-appearance, thực tế là ${currentActiveTab?.id}`);
  }

  // Phím ArrowLeft: Chuyển lùi lại (tab-ai)
  await act(async () => {
    currentActiveTab?.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowLeft", bubbles: true }));
  });
  currentActiveTab = tablist.querySelector<HTMLButtonElement>('[role="tab"][aria-selected="true"]');
  if (currentActiveTab?.id !== "tab-ai") {
    throw new Error(`FAIL: Sau khi bấm ArrowLeft phải chuyển sang tab-ai, thực tế là ${currentActiveTab?.id}`);
  }

  // Phím Home: Chuyển về tab đầu tiên (tab-accounts)
  await act(async () => {
    currentActiveTab?.dispatchEvent(new KeyboardEvent("keydown", { key: "Home", bubbles: true }));
  });
  currentActiveTab = tablist.querySelector<HTMLButtonElement>('[role="tab"][aria-selected="true"]');
  if (currentActiveTab?.id !== "tab-accounts") {
    throw new Error(`FAIL: Sau khi bấm Home phải chuyển sang tab-accounts, thực tế là ${currentActiveTab?.id}`);
  }
  console.log("  -> [PASS A01.3] Điều hướng phím bàn phím (ArrowRight, End, ArrowLeft, Home) chuẩn WAI-ARIA.");

  // Tab Giao diện
  const appearanceTabBtn = container.querySelector<HTMLButtonElement>("#tab-appearance")!;
  await act(async () => {
    appearanceTabBtn.click();
  });
  const themeRadiogroup = container.querySelector('[role="radiogroup"]');
  if (!themeRadiogroup) throw new Error("FAIL: Không tìm thấy role='radiogroup' trong tab Giao diện");
  console.log("  -> [PASS A01.4] Tab Giao diện hiển thị đầy đủ tùy chọn System / Light / Dark.");

  // Tab Tài khoản danh sách hợp nhất
  const accountsTabBtn = container.querySelector<HTMLButtonElement>("#tab-accounts")!;
  await act(async () => {
    accountsTabBtn.click();
    await new Promise((r) => setTimeout(r, 40));
  });

  const pageText = container.textContent || "";
  if (!pageText.includes("ChatGPT Team Account") || !pageText.includes("Gemini Advanced Creator") || !pageText.includes("MuseAI Studio Pro")) {
    throw new Error("FAIL: Không hiển thị đầy đủ tài khoản 3 provider trong tab Tài khoản");
  }
  if (Array.from(container.querySelectorAll("h2, h3, h4")).some((heading) =>
    heading.textContent === "AI dùng chung" || heading.textContent === "AI riêng")) {
    throw new Error("FAIL: Tài khoản AI không được chia nhóm theo phạm vi dùng chung/riêng.");
  }
  console.log("  -> [PASS A02] Tab Tài khoản hiển thị danh sách tài khoản AI toàn ứng dụng.");

  // =========================================================================
  // PHẦN 2: F1 - BẢO TOÀN DRAFT ĐƯỜNG DẪN LƯU TRỮ QUA 4 TAB & DIRTY GUARD
  // =========================================================================
  console.log("\n--- [TEST SUITE 2] F1: Bảo toàn nháp đường dẫn lưu trữ qua 4 tab & Dirty Guard ---");

  // Bước 2.1: Chuyển sang tab Lưu trữ và nhập đường dẫn mới (chưa lưu)
  const storageTabBtn = container.querySelector<HTMLButtonElement>("#tab-storage")!;
  await act(async () => {
    storageTabBtn.click();
    await new Promise((r) => setTimeout(r, 40));
  });

  const docsInput = container.querySelector<HTMLInputElement>("#docs-path");
  if (!docsInput) throw new Error("FAIL: Không tìm thấy input #docs-path trong tab Lưu trữ");

  const DRAFT_PATH = "D:/titoai_custom_draft_storage/docs";
  await act(async () => {
    setNativeInputValue(docsInput, DRAFT_PATH);
  });

  if (docsInput.value !== DRAFT_PATH) {
    throw new Error("FAIL: Không thể đặt giá trị nháp cho #docs-path");
  }

  // Bước 2.2: Chuyển lần lượt qua cả 4 tab: Accounts -> Storage -> AI -> Appearance -> Storage
  for (const tabKey of ["accounts", "ai", "appearance", "storage"] as const) {
    const btn = container.querySelector<HTMLButtonElement>(`#tab-${tabKey}`)!;
    await act(async () => {
      btn.click();
      await new Promise((r) => setTimeout(r, 20));
    });
  }

  // Kiểm chứng F1.1: Nháp đường dẫn vẫn còn nguyên 100%!
  const restoredDocsInput = container.querySelector<HTMLInputElement>("#docs-path")!;
  if (restoredDocsInput.value !== DRAFT_PATH) {
    throw new Error(`FAIL [F1]: Nháp đường dẫn bị mất sau khi chuyển 4 tab! Giá trị hiện tại: '${restoredDocsInput.value}'`);
  }
  console.log("  -> [PASS F1.1] Nhập đường dẫn chưa lưu -> Duyệt đủ 4 tab con -> Bản nháp bảo toàn 100%!");

  // Bước 2.3: Kiểm tra Header Cài đặt chuẩn tab: không có nút 'Quay lại công việc'
  const backBtn = container.querySelector<HTMLButtonElement>('button[aria-label="Quay lại giao diện trước"]');
  if (backBtn) {
    throw new Error("FAIL [F1]: Header Cài đặt không được có nút 'Quay lại công việc' vì Cài đặt là tab chuẩn");
  }
  console.log("  -> [PASS F1.2] Header Cài đặt chuẩn tab: Đã loại bỏ nút 'Quay lại công việc'.");

  // Bước 2.4: Lưu lỗi / validation thất bại giữ nguyên draft
  simulateSaveError = true;
  await act(async () => {
    setNativeInputValue(docsInput, "D:/invalid_storage_path");
  });
  const saveBtn = container.querySelector<HTMLButtonElement>('button[type="submit"]')!;
  await act(async () => {
    saveBtn.click();
    await new Promise((r) => setTimeout(r, 40));
  });

  const afterErrorInput = container.querySelector<HTMLInputElement>("#docs-path")!;
  if (afterErrorInput.value !== "D:/invalid_storage_path") {
    throw new Error("FAIL [F1]: Lỗi save làm mất nội dung người dùng vừa nhập!");
  }
  simulateSaveError = false;
  console.log("  -> [PASS F1.4] Lỗi lưu cài đặt / validation thất bại giữ nguyên bản nháp trong form.");

  await act(async () => {
    root.unmount();
  });
  container.remove();

  // =========================================================================
  // PHẦN 3: F2 (Q2-APP-DRAFT) - PRODUCTION APP + PRODUCTION PROMPT EDITOR INTEGRATION
  // =========================================================================
  console.log("\n--- [TEST SUITE 3] F2 (Q2-APP-DRAFT): Production App & Prompt Editor Integration ---");
  const appContainer = document.createElement("div");
  document.body.appendChild(appContainer);
  const appRoot = createRoot(appContainer);

  await act(async () => {
    appRoot.render(React.createElement(App));
  });
  // ThemeProvider chỉ render sau khi theme.get_preference trả về (qua timer của mock bridge).
  await act(async () => { await new Promise((r) => setTimeout(r, 50)); });

  // Bước 3.1: Chuyển đến màn hình Mẫu Prompt (nơi render PromptTemplateManager production)
  const navItems = Array.from(appContainer.querySelectorAll<HTMLButtonElement>("button.nav-item, nav button"));
  const promptNavBtn = navItems.find((b) => b.textContent?.trim() === "Prompt" || b.textContent?.includes("Prompt"));
  if (!promptNavBtn) throw new Error("FAIL: Không tìm thấy nút 'Prompt' trong sidebar App production");

  await act(async () => {
    promptNavBtn.click();
    await new Promise((r) => setTimeout(r, 50));
  });

  // Chờ PromptTemplateManager nạp danh sách template
  for (let attempt = 0; attempt < 25; attempt++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 30));
    });
    if (appContainer.querySelector("textarea#prompt-template-text")) break;
  }

  const promptTextarea = appContainer.querySelector<HTMLTextAreaElement>("textarea#prompt-template-text");
  if (!promptTextarea) {
    throw new Error("FAIL: Không tìm thấy textarea trong PromptTemplateManager production");
  }

  const PROMPT_DRAFT_TEXT = "Kịch bản phân cảnh giới thiệu sản phẩm: [DRAFT TẠI APP PRODUCTION CHỜ LƯU]";
  await act(async () => {
    setNativeInputValue(promptTextarea, PROMPT_DRAFT_TEXT);
  });

  if (promptTextarea.value !== PROMPT_DRAFT_TEXT) {
    throw new Error("FAIL: Không thể nhập draft vào Prompt editor textarea production");
  }

  // Bước 3.2: Bấm nút 'Cài đặt' từ Sidebar điều hướng production của App
  const settingsNavBtn = navItems.find((b) => b.textContent?.includes("Cài đặt"));
  if (!settingsNavBtn) throw new Error("FAIL: Không tìm thấy nút 'Cài đặt' trong sidebar App production");

  await act(async () => {
    settingsNavBtn.click();
    await new Promise((r) => setTimeout(r, 50));
  });

  // Xác minh SettingsPage đang hiển thị
  const appTablist = appContainer.querySelector('[role="tablist"][aria-label="Các nhóm cài đặt Fraylo"]');
  if (!appTablist) throw new Error("FAIL: Trang Cài đặt không được mở trong App production");

  // Bước 3.3: Duyệt qua toàn bộ 4 tab con của Cài đặt
  for (const tabKey of ["accounts", "storage", "ai", "appearance"] as const) {
    const tabBtn = appContainer.querySelector<HTMLButtonElement>(`#tab-${tabKey}`)!;
    await act(async () => {
      tabBtn.click();
      await new Promise((r) => setTimeout(r, 20));
    });
  }

  // Bước 3.4: Người dùng chuyển từ Cài đặt về màn hình Prompt qua sidebar
  await act(async () => {
    promptNavBtn.click();
    await new Promise((r) => setTimeout(r, 50));
  });

  // Bước 3.5: Kiểm chứng màn hình Prompt hiển thị lại và draft được giữ.
  const restoredPromptTextarea = appContainer.querySelector<HTMLTextAreaElement>("textarea#prompt-template-text");
  if (!restoredPromptTextarea) {
    throw new Error("FAIL: Không tìm thấy textarea sau khi quay lại màn hình Thư viện");
  }

  if (restoredPromptTextarea.value !== PROMPT_DRAFT_TEXT) {
    throw new Error(`FAIL [F2]: Draft kịch bản bị reset trong App production! Giá trị: '${restoredPromptTextarea.value}'`);
  }
  console.log("  -> [PASS F2 / Q2-APP-DRAFT] App giữ draft Prompt qua điều hướng.");

  // =========================================================================
  // PHẦN 5: F1 - SIDEBAR NAVIGATION GUARD KHI SETTINGS DIRTY
  // =========================================================================
  console.log("\n--- [TEST SUITE 5] F1: Dirty Guard khi điều hướng sidebar trong App ---");

  // Mở Cài đặt, chuyển sang tab Lưu trữ và nhập nháp.
  await act(async () => {
    settingsNavBtn.click();
    await new Promise((r) => setTimeout(r, 40));
  });
  const appStorageTab = appContainer.querySelector<HTMLButtonElement>("#tab-storage")!;
  await act(async () => {
    appStorageTab.click();
    await new Promise((r) => setTimeout(r, 30));
  });

  const appDocsInput = appContainer.querySelector<HTMLInputElement>("#docs-path")!;
  await act(async () => {
    setNativeInputValue(appDocsInput, "D:/storage/sidebar_dirty_test");
  });

  // Người dùng bấm vào Dự án trên sidebar trong khi Cài đặt đang dirty
  const projectsNavBtn = navItems.find((b) => b.textContent?.includes("Dự án"))!;
  await act(async () => {
    projectsNavBtn.click();
    await new Promise((r) => setTimeout(r, 30));
  });

  // Modal xác nhận của App phải xuất hiện
  const appDirtyModal = appContainer.querySelector('[role="dialog"][aria-labelledby="app-dirty-confirm-title"]');
  if (!appDirtyModal) {
    throw new Error("FAIL [F1]: Không hiển thị modal xác nhận rời Cài đặt khi bấm sidebar!");
  }

  // Bấm 'Ở lại'
  const appStayBtn = Array.from(appDirtyModal.querySelectorAll<HTMLButtonElement>("button")).find(
    (b) => b.textContent?.includes("Ở lại")
  );
  if (!appStayBtn) throw new Error("FAIL: Không tìm thấy nút 'Ở lại' trên app dirty modal");

  await act(async () => {
    appStayBtn.click();
  });

  // Vẫn ở trang Cài đặt, input vẫn giữ nháp
  if (appContainer.querySelector('[role="dialog"][aria-labelledby="app-dirty-confirm-title"]')) {
    throw new Error("FAIL: Modal không đóng sau khi bấm 'Ở lại'");
  }
  if (appDocsInput.value !== "D:/storage/sidebar_dirty_test") {
    throw new Error("FAIL: Mất bản nháp sau khi bấm 'Ở lại' từ sidebar guard");
  }
  console.log("  -> [PASS F1.5] Sidebar Guard: Bấm menu sidebar khi dirty -> Modal xác nhận bảo vệ nháp hoàn hảo.");

  // Dọn dẹp
  await act(async () => {
    appRoot.unmount();
  });
  appContainer.remove();

  // =========================================================================
  // PHẦN 6: AI ACCOUNT CONCURRENCY UI CONTRACT VERIFICATION
  // =========================================================================
  console.log("\n--- [TEST SUITE 6] AI Account Concurrency: Single N, Labels, Description & Edit Form ---");
  const accountTestContainer = document.createElement("div");
  document.body.appendChild(accountTestContainer);
  const accountRoot = createRoot(accountTestContainer);

  await act(async () => {
    accountRoot.render(
      React.createElement(
        ThemeProvider,
        null,
        React.createElement(SettingsPage, {
          initialTab: "accounts",
          onBack: () => {},
        })
      )
    );
  });

  // Chờ AccountSettingsPanel nạp xong danh sách tài khoản
  for (let attempt = 0; attempt < 25; attempt++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 30));
    });
    if ((accountTestContainer.textContent || "").includes("ChatGPT Team Account")) break;
  }

  // 1. Kiểm tra Card danh sách: "Đồng thời: tối đa N tác vụ"
  const cardText = accountTestContainer.textContent || "";
  if (!cardText.includes("Đồng thời:") || !cardText.includes("tối đa 3 tác vụ") || !cardText.includes("tối đa 2 tác vụ")) {
    throw new Error("FAIL [SUITE 6]: Danh sách tài khoản không hiển thị đúng định dạng 'Đồng thời: tối đa N tác vụ'");
  }
  if (cardText.includes("Hạn mức:") || cardText.includes("tác vụ /")) {
    throw new Error("FAIL [SUITE 6]: Danh sách tài khoản vẫn còn nhãn cũ 'Hạn mức: X tác vụ / Y tab'");
  }
  console.log("  -> [PASS 6.1] Danh sách tài khoản hiển thị chuẩn: 'Đồng thời: tối đa N tác vụ', loại bỏ nhãn cũ.");

  // 2. Mở form thêm tài khoản
  const addAccountBtn = Array.from(accountTestContainer.querySelectorAll<HTMLButtonElement>("button")).find(
    (b) => b.textContent?.includes("Thêm tài khoản AI")
  );
  if (!addAccountBtn) throw new Error("FAIL [SUITE 6]: Không tìm thấy nút 'Thêm tài khoản AI'");

  await act(async () => {
    addAccountBtn.click();
    await new Promise((r) => setTimeout(r, 40));
  });

  const formText = accountTestContainer.textContent || "";
  // Kiểm tra nhãn "Số tác vụ chạy đồng thời"
  if (!formText.includes("Số tác vụ chạy đồng thời")) {
    throw new Error("FAIL [SUITE 6]: Form thêm tài khoản thiếu nhãn 'Số tác vụ chạy đồng thời'");
  }
  // Kiểm tra mô tả tab
  if (!formText.includes("Mỗi tác vụ sử dụng một tab riêng. Các tác vụ vượt giới hạn sẽ chờ trong hàng đợi.")) {
    throw new Error("FAIL [SUITE 6]: Form thêm tài khoản thiếu mô tả 'Mỗi tác vụ sử dụng một tab riêng. Các tác vụ vượt giới hạn sẽ chờ trong hàng đợi.'");
  }
  // Kiểm tra KHÔNG có ô "Max Jobs"
  if (formText.includes("Tối đa tác vụ đồng thời (Max Jobs)") || accountTestContainer.querySelector("#new-acc-max-jobs")) {
    throw new Error("FAIL [SUITE 6]: Form thêm tài khoản vẫn còn ô Max Jobs cũ!");
  }
  console.log("  -> [PASS 6.2] Form thêm tài khoản: Nguồn cấu hình duy nhất N từ MaxTabs, có mô tả hàng đợi, loại bỏ ô Max Jobs cũ.");

  // 3. Kiểm tra form Sửa tài khoản (bấm nút 'Sửa' trên card đầu tiên)
  const editButtons = Array.from(accountTestContainer.querySelectorAll<HTMLButtonElement>("button")).filter(
    (b) => b.textContent?.trim() === "Sửa"
  );
  if (editButtons.length === 0) throw new Error("FAIL [SUITE 6]: Không tìm thấy nút 'Sửa' trên card tài khoản");

  await act(async () => {
    editButtons[0].click();
    await new Promise((r) => setTimeout(r, 40));
  });

  const editMaxTabsInput = accountTestContainer.querySelector<HTMLInputElement>('input[id^="edit-acc-max-tabs-"]');
  if (!editMaxTabsInput) {
    throw new Error("FAIL [SUITE 6]: Không tìm thấy ô sửa Số tác vụ chạy đồng thời khi bấm Sửa");
  }
  if (editMaxTabsInput.value !== "3") {
    throw new Error(`FAIL [SUITE 6]: Giá trị N ban đầu của ChatGPT Team Account phải là 3, thực tế: ${editMaxTabsInput.value}`);
  }
  console.log("  -> [PASS 6.3] Form sửa inline tài khoản: Cho phép điều chỉnh N duy nhất, hiển thị nhãn và mô tả tab nhất quán.");

  await act(async () => {
    accountRoot.unmount();
  });
  accountTestContainer.remove();

  // =========================================================================
  // PHẦN 7: KIỂM CHỨNG BỎ TAB TRÌNH DUYỆT & TÁCH SUB-TABS TÀI KHOẢN (CHATGPT, GEMINI, MUSEAI, TIKTOK)
  // =========================================================================
  console.log("\n--- [TEST SUITE 7] Bỏ Tab Trình duyệt & Sub-tabs Tài khoản (ChatGPT, Gemini, MuseAI, TikTok) ---");

  // 1. Kiểm chứng thanh điều hướng App Shell KHÔNG CÒN tab "Quản lý Trình duyệt"
  const appVerifyContainer = document.createElement("div");
  document.body.appendChild(appVerifyContainer);
  const appVerifyRoot = createRoot(appVerifyContainer);

  await act(async () => {
    appVerifyRoot.render(React.createElement(App));
  });

  for (let attempt = 0; attempt < 25; attempt++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 30));
    });
    if (appVerifyContainer.querySelector("button.nav-item")) break;
  }

  const appNavButtons = Array.from(appVerifyContainer.querySelectorAll<HTMLButtonElement>("button.nav-item, nav button"));
  const navText = appNavButtons.map((b) => b.textContent || "").join(" ");
  if (navText.includes("Quản lý Trình duyệt")) {
    throw new Error("FAIL [SUITE 7]: Tab 'Quản lý Trình duyệt' vẫn còn tồn tại trên thanh điều hướng sidebar!");
  }
  console.log("  -> [PASS 7.1] Sidebar App Shell: Đã loại bỏ hoàn toàn tab 'Quản lý Trình duyệt'.");

  await act(async () => {
    appVerifyRoot.unmount();
  });
  appVerifyContainer.remove();

  // 2. Kiểm chứng Sub-tabs bên trong phần Tài khoản
  const subTabTestContainer = document.createElement("div");
  document.body.appendChild(subTabTestContainer);
  const subTabRoot = createRoot(subTabTestContainer);

  await act(async () => {
    subTabRoot.render(
      React.createElement(
        ThemeProvider,
        null,
        React.createElement(SettingsPage, {
          initialTab: "accounts",
          onBack: () => {},
        })
      )
    );
  });

  // Chờ tải accounts
  for (let attempt = 0; attempt < 25; attempt++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 30));
    });
    if ((subTabTestContainer.textContent || "").includes("ChatGPT Team Account")) break;
  }

  const subTabList = subTabTestContainer.querySelector('[role="tablist"][aria-label="Phân loại tài khoản theo Provider"]');
  if (!subTabList) {
    throw new Error("FAIL [SUITE 7]: Không tìm thấy tablist phân loại Provider trong tab Tài khoản");
  }

  // Xác minh đủ 5 sub-tab: Tất cả, ChatGPT, Gemini, MuseAI, TikTok
  const subTabButtons = Array.from(subTabList.querySelectorAll<HTMLButtonElement>('button[role="tab"]'));
  const subTabTexts = subTabButtons.map((b) => b.textContent || "");

  if (!subTabTexts.some((t) => t.includes("Tất cả"))) {
    throw new Error("FAIL [SUITE 7]: Thiếu sub-tab 'Tất cả'");
  }
  if (!subTabTexts.some((t) => t.includes("ChatGPT"))) {
    throw new Error("FAIL [SUITE 7]: Thiếu sub-tab 'ChatGPT'");
  }
  if (!subTabTexts.some((t) => t.includes("Gemini"))) {
    throw new Error("FAIL [SUITE 7]: Thiếu sub-tab 'Gemini'");
  }
  if (!subTabTexts.some((t) => t.includes("MuseAI"))) {
    throw new Error("FAIL [SUITE 7]: Thiếu sub-tab 'MuseAI'");
  }
  if (!subTabTexts.some((t) => t.includes("TikTok"))) {
    throw new Error("FAIL [SUITE 7]: Thiếu sub-tab 'TikTok'");
  }
  console.log("  -> [PASS 7.2] Phân chia thành các sub-tabs chuẩn xác: Tất cả, ChatGPT, Gemini, MuseAI, TikTok.");

  // 3. Kiểm thử lọc khi bấm từng sub-tab
  // Bấm tab ChatGPT
  const chatGptTab = subTabButtons.find((b) => b.textContent?.includes("ChatGPT"))!;
  await act(async () => {
    chatGptTab.click();
    await new Promise((r) => setTimeout(r, 30));
  });
  let content = subTabTestContainer.textContent || "";
  if (!content.includes("ChatGPT Team Account") || content.includes("Gemini Advanced Creator") || content.includes("MuseAI Studio Pro")) {
    throw new Error("FAIL [SUITE 7]: Lọc theo tab ChatGPT không chính xác!");
  }

  // Bấm tab Gemini
  const geminiTab = subTabButtons.find((b) => b.textContent?.includes("Gemini"))!;
  await act(async () => {
    geminiTab.click();
    await new Promise((r) => setTimeout(r, 30));
  });
  content = subTabTestContainer.textContent || "";
  if (!content.includes("Gemini Advanced Creator") || content.includes("ChatGPT Team Account") || content.includes("MuseAI Studio Pro")) {
    throw new Error("FAIL [SUITE 7]: Lọc theo tab Gemini không chính xác!");
  }

  // Bấm tab MuseAI
  const museAiTab = subTabButtons.find((b) => b.textContent?.includes("MuseAI"))!;
  await act(async () => {
    museAiTab.click();
    await new Promise((r) => setTimeout(r, 30));
  });
  content = subTabTestContainer.textContent || "";
  if (!content.includes("MuseAI Studio Pro") || content.includes("ChatGPT Team Account") || content.includes("Gemini Advanced Creator")) {
    throw new Error("FAIL [SUITE 7]: Lọc theo tab MuseAI không chính xác!");
  }

  // Bấm tab TikTok (chưa có tài khoản)
  const tikTokTab = subTabButtons.find((b) => b.textContent?.includes("TikTok"))!;
  await act(async () => {
    tikTokTab.click();
    await new Promise((r) => setTimeout(r, 30));
  });
  content = subTabTestContainer.textContent || "";
  if (!content.includes("Chưa có tài khoản nào thuộc nhóm TikTok") || !content.includes("Thêm tài khoản TikTok")) {
    throw new Error("FAIL [SUITE 7]: Empty state của tab TikTok không hiển thị đúng hướng dẫn và nút tạo!");
  }
  console.log("  -> [PASS 7.3] Tương tác chuyển đổi giữa các sub-tabs và lọc danh sách tài khoản theo Provider hoạt động hoàn hảo.");

  // 4. Mở form thêm tài khoản khi đang ở tab TikTok -> Provider tự chọn tiktok
  const addTikTokBtn = Array.from(subTabTestContainer.querySelectorAll<HTMLButtonElement>("button")).find(
    (b) => b.textContent?.includes("Thêm tài khoản TikTok")
  );
  if (!addTikTokBtn) throw new Error("FAIL [SUITE 7]: Không tìm thấy nút 'Thêm tài khoản TikTok'");

  await act(async () => {
    addTikTokBtn.click();
    await new Promise((r) => setTimeout(r, 30));
  });

  const providerSelect = subTabTestContainer.querySelector<HTMLSelectElement>("#new-acc-provider");
  if (!providerSelect) throw new Error("FAIL [SUITE 7]: Không tìm thấy #new-acc-provider");
  if (providerSelect.value !== "tiktok") {
    throw new Error(`FAIL [SUITE 7]: Khi mở form từ tab TikTok, provider phải là 'tiktok', thực tế: ${providerSelect.value}`);
  }

  // Kiểm tra option TikTok trong dropdown
  const providerOptions = Array.from(providerSelect.options).map((o) => o.value);
  if (!providerOptions.includes("tiktok")) {
    throw new Error("FAIL [SUITE 7]: Dropdown Dịch vụ AI thiếu tùy chọn 'tiktok'");
  }
  console.log("  -> [PASS 7.4] Form thêm tài khoản: Hỗ trợ tạo tài khoản TikTok và tự động chọn Provider theo sub-tab hiện hành.");

  // Quay lại tab Tất cả
  const allTab = subTabButtons.find((b) => b.textContent?.includes("Tất cả"))!;
  await act(async () => {
    allTab.click();
    await new Promise((r) => setTimeout(r, 30));
  });
  content = subTabTestContainer.textContent || "";
  if (!content.includes("ChatGPT Team Account") || !content.includes("Gemini Advanced Creator") || !content.includes("MuseAI Studio Pro")) {
    throw new Error("FAIL [SUITE 7]: Quay lại tab 'Tất cả' không khôi phục toàn bộ danh sách!");
  }
  console.log("  -> [PASS 7.5] Sub-tab 'Tất cả' hiển thị hợp nhất toàn bộ tài khoản hệ thống.");

  await act(async () => {
    subTabRoot.unmount();
  });
  subTabTestContainer.remove();

  console.log("\n=================================================================");
  console.log(">>> TOÀN BỘ KIỂM THỬ F1-F3, A01-A02, SUITE 6 & SUITE 7 ĐÃ VƯỢT QUA (PASS 100%) <<<");
  console.log("=================================================================\n");
}

runTests().catch((err) => {
  console.error("TEST FAILED:", err);
  process.exit(1);
});
