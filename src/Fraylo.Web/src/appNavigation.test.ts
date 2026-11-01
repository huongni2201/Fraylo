import { domWindow } from "./test/setupDom.ts";
import assert from "node:assert/strict";
const mockTemplates = [
  {
    templateId: "tmpl_test_script",
    purpose: "Script",
    name: "Kịch bản test",
    createdAtUtc: "2026-10-28T00:00:00Z",
    updatedAtUtc: "2026-10-28T00:00:00Z",
  },
];

const messageListeners: Array<(event: MessageEvent) => void> = [];
let publishingListCalls = 0;

(domWindow as any).chrome = {
  webview: {
    addEventListener(_type: string, listener: (event: MessageEvent) => void) {
      messageListeners.push(listener);
    },
    removeEventListener(_type: string, listener: (event: MessageEvent) => void) {
      const idx = messageListeners.indexOf(listener);
      if (idx >= 0) messageListeners.splice(idx, 1);
    },
    postMessage: (msg: unknown) => {
      const req = msg as { id: string; command: string; payload?: any; contractVersion: string };
      let payloadData: unknown = {};

      if (req.command === "theme.get_preference") {
        payloadData = { preference: "system", effectiveTheme: "dark" };
      } else if (req.command === "prompt.list_templates") {
        payloadData = { templates: mockTemplates };
      } else if (req.command === "prompt.get_template") {
        payloadData = { template: mockTemplates[0], content: { templateId: mockTemplates[0].templateId, templateText: "Nội dung template test", variableDefinitionsJson: "[]", updatedAtUtc: mockTemplates[0].updatedAtUtc } };
      } else if (req.command === "asset.list") {
        payloadData = { assets: [], totalCount: 0, page: 1, pageSize: 20 };
      } else if (req.command === "settings.get") {
        payloadData = { documentsPath: "", imagesPath: "", videosPath: "" };
      } else if (req.command === "publishing.list") {
        publishingListCalls++;
        payloadData = { videos: [{ projectId: "prj_video", projectName: "Video đã tạo", assetId: "ast_video", fileName: "output.mp4", byteSize: 1024, caption: "Caption đã lưu #test" }], attempts: [] };
      } else if (req.command === "analytics.list_tiktok_accounts") {
        payloadData = { accounts: [] };
      }

      setTimeout(() => {
        const ev = {
          data: {
            id: req.id,
            type: "response",
            success: true,
            contractVersion: req.contractVersion,
            data: payloadData,
          },
        } as MessageEvent;
        for (const listener of [...messageListeners]) {
          listener(ev);
        }
      }, 0);
    },
  },
};

const React = (await import("react")).default;
const { act } = await import("react");
const { createRoot } = await import("react-dom/client");
const App = (await import("./App.tsx")).default;

async function runTests() {
  console.log("=== KIỂM THỬ TÁCH MẪU PROMPT VÀ TÀI NGUYÊN MEDIA THÀNH 2 TAB & 2 SCREEN ===");

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = createRoot(container);

  await act(async () => {
    root.render(React.createElement(App));
  });

  for (let i = 0; i < 20; i++) {
    await act(async () => {
      await new Promise((r) => setTimeout(r, 20));
    });
  }

  // 1. Kiểm tra 2 tab riêng biệt tồn tại trên Sidebar
  console.log("\n[TEST 1] Kiểm tra 2 tab riêng biệt trên Sidebar...");
  const navItems = Array.from(container.querySelectorAll<HTMLButtonElement>("nav.sidebar-nav button.nav-item"));
  const promptTab = navItems.find((b) => b.textContent?.trim() === "Prompt");
  const mediaTab = navItems.find((b) => b.textContent?.trim() === "Tài nguyên");
  const oldLibraryTab = navItems.find((b) => b.textContent?.trim() === "Thư viện");

  assert.ok(promptTab, "Nút 'Prompt' phải tồn tại trên sidebar điều hướng");
  assert.ok(mediaTab, "Nút 'Tài nguyên' phải tồn tại trên sidebar điều hướng");
  assert.strictEqual(oldLibraryTab, undefined, "Không còn tab 'Thư viện' gộp chung cũ");
  console.log("  -> PASS: Đã tách thành 2 tab 'Prompt' và 'Tài nguyên' độc lập trên Sidebar.");

  // 2. Chuyển sang tab Prompt
  console.log("\n[TEST 2] Chuyển sang màn hình Prompt...");
  await act(async () => {
    promptTab.click();
    await new Promise((r) => setTimeout(r, 50));
  });

  assert.strictEqual(promptTab.getAttribute("aria-current"), "page", "Tab 'Prompt' phải có aria-current='page'");
  assert.strictEqual(mediaTab.getAttribute("aria-current"), null, "Tab 'Tài nguyên' không được active");

  // Kiểm tra header và màn hình Prompt
  const promptHeading = Array.from(container.querySelectorAll("h2")).find((h) => h.textContent?.trim() === "Prompt");
  assert.ok(promptHeading, "Màn hình Prompt phải có heading 'Prompt'");
  console.log("  -> PASS: Màn hình Prompt hiển thị chuẩn xác với header riêng.");

  // 3. Chuyển sang tab Tài nguyên
  console.log("\n[TEST 3] Chuyển sang màn hình Tài nguyên...");
  await act(async () => {
    mediaTab.click();
    await new Promise((r) => setTimeout(r, 50));
  });

  assert.strictEqual(mediaTab.getAttribute("aria-current"), "page", "Tab 'Tài nguyên' phải có aria-current='page'");
  assert.strictEqual(promptTab.getAttribute("aria-current"), null, "Tab 'Prompt' không được active");

  // Kiểm tra header và màn hình Tài nguyên
  const mediaHeading = Array.from(container.querySelectorAll("h2")).find((h) => h.textContent?.trim() === "Tài nguyên");
  assert.ok(mediaHeading, "Màn hình Tài nguyên phải có heading 'Tài nguyên'");
  console.log("  -> PASS: Màn hình Tài nguyên hiển thị chuẩn xác với header riêng.");

  // 4. Kiểm tra không còn nút switch tab con kiểu gộp trong cả 2 màn hình
  console.log("\n[TEST 4] Kiểm tra loại bỏ hoàn toàn các nút gộp tab con cũ...");
  const oldPromptToggleBtn = Array.from(container.querySelectorAll("button")).find((b) => b.textContent?.includes("Mẫu Prompt (AI Templates)"));
  assert.strictEqual(oldPromptToggleBtn, undefined, "Không còn nút toggle 'Mẫu Prompt (AI Templates)' gộp bên trong màn hình");
  console.log("  -> PASS: Không còn nút toggle tab gộp cũ.");

  const analyticsIndex = navItems.findIndex(b => b.textContent?.trim() === "TikTok Analytics");
  const publishingIndex = navItems.findIndex(b => b.textContent?.trim() === "Đăng video");
  assert.equal(publishingIndex, analyticsIndex + 1, "Đăng video nằm ngay dưới TikTok Analytics.");
  assert.equal(publishingListCalls, 0, "Trang Đăng video chưa mở không tải dữ liệu nền.");
  const publishingTab = navItems[publishingIndex];
  await act(async () => { publishingTab.click(); await new Promise(r => setTimeout(r, 50)); });
  assert.equal(publishingTab.getAttribute("aria-current"), "page");
  assert.ok(publishingListCalls > 0, "Route phải gọi publishing.list qua bridge allow-list thật.");
  // Chờ publishing.list trả về trước khi chọn video (select bị disable lúc đang tải).
  await act(async () => { await new Promise(r => setTimeout(r, 50)); });
  const videoSelect = container.querySelector<HTMLSelectElement>("#publish-video")!;
  await act(async () => { videoSelect.value = "ast_video"; videoSelect.dispatchEvent(new Event("change", { bubbles: true })); });
  assert.equal(container.querySelector<HTMLTextAreaElement>("#publish-caption")!.value, "Caption đã lưu #test");
  await act(async () => { promptTab.click(); publishingTab.click(); await new Promise(r => setTimeout(r, 50)); });
  assert.equal(container.querySelector<HTMLSelectElement>("#publish-video")!.value, "ast_video", "Chuyển route giữ video đã chọn.");
  assert.equal(container.querySelector<HTMLTextAreaElement>("#publish-caption")!.value, "Caption đã lưu #test", "Chuyển route giữ caption.");
  const accountSettings = Array.from(container.querySelectorAll("button")).find(b => b.textContent?.trim() === "Cài đặt tài khoản")!;
  await act(async () => { accountSettings.click(); });
  assert.equal(Array.from(container.querySelectorAll("nav button")).find(b => b.textContent?.trim() === "Cài đặt")?.getAttribute("aria-current"), "page");
  assert.equal(container.querySelector('[role="tab"][aria-selected="true"]')?.textContent?.includes("Tài khoản"), true);
  console.log("  -> PASS: Đăng video đúng vị trí, nối bridge, giữ draft và chuyển Cài đặt tài khoản.");

  await act(async () => {
    root.unmount();
  });
  container.remove();

  console.log("\n>>> TẤT CẢ KIỂM THỬ ĐIỀU HƯỚNG TÁCH TAB/SCREEN ĐÃ VƯỢT QUA (PASS 100%) <<<");
}

runTests().catch((err) => {
  console.error("TEST FAILED:", err);
  process.exit(1);
});
