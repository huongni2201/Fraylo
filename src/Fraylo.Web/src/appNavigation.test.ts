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
let facebookAnalyticsCatalogCalls = 0;
let facebookAnalyticsSyncCalls = 0;
let aiAnalysisStarts = 0;
const savedDraftIds: string[] = [];
const futureDraftDueAtUtc = new Date(Date.now() + 7 * 86_400_000).toISOString();

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
      } else if (req.command === "facebook.list") {
        const posts = [false, true].map(personal => {
          const id = personal ? "personal:a" : "group:a";
          const task = { taskId: id, batchId: id, accountId: "a", targetId: id, groupName: personal ? "Trang cá nhân" : "Nhóm A", groupUrl: "https://www.facebook.com/", text: personal ? "Nội dung cá nhân chung" : "Nội dung nhóm chung", state: "Queued", message: "", createdAtUtc: "", updatedAtUtc: "", submittedAtUtc: null };
          return { batchId: id, name: personal ? "Bài cá nhân chung" : "Bài nhóm chung", accountId: "a", paused: false, revision: 1, createdAtUtc: "", tasks: [task] };
        });
        payloadData = { accounts: [{ accountId: "a", displayName: "Tài khoản A", accountIdentity: "A", sessionStatus: "Connected" }], groups: [{ targetId: "group:a", accountId: "a", name: "Nhóm A", url: "https://www.facebook.com/" }], posts, tasks: posts.flatMap(p => p.tasks), drafts: posts.map(p => ({ draftId: p.batchId, name: "Nháp " + p.name, accountId: p.accountId, text: "Nháp " + p.tasks[0].text, targetIds: [p.tasks[0].targetId], imageAssetIds: [], updatedAtUtc: "", dueAtUtc: futureDraftDueAtUtc })) };
      } else if (req.command === "facebook.drafts.save") {
        savedDraftIds.push(req.payload.draftId); payloadData = { ...req.payload, updatedAtUtc: "" };
      } else if (req.command === "facebook.analytics.catalog") {
        facebookAnalyticsCatalogCalls++;
        payloadData = { accounts: [{ accountId: "fb-account", name: "Facebook test", enabled: true, sessionStatus: "Connected" }], pages: [
          { id: "fb-page-1", accountId: "fb-account", facebookPageId: "100", name: "Page test 1", url: "https://www.facebook.com/100" },
          { id: "fb-page-2", accountId: "fb-account", facebookPageId: "200", name: "Page test 2", url: "https://www.facebook.com/200" },
        ] };
      } else if (req.command === "facebook.analytics.dashboard") {
        payloadData = { capture: { id: "fb-capture", pageId: req.payload.pageId, status: "Complete", startedAtUtc: "2026-11-06T10:00:00Z", scope: "Trọn đời" }, latestAttempt: null, content: [], totalCount: 0, page: 1, totals: {}, captures: [] };
      } else if (req.command === "analytics.analysis.list") {
        const pageId = req.payload.pageId;
        payloadData = { latestSourceId: "fb-capture", hasMore: false, reports: [{ id: pageId, sourceId: "fb-capture",
          createdAtUtc: "2026-11-06T10:00:00Z", providerKey: "chatgpt-web", state: "Succeeded", reason: null,
          input: { source: "Fixture", capturedAt: "2026-11-06T10:00:00Z", scope: "Trọn đời", partial: true,
            totalRecords: 1, omittedRecords: 0, sample: [], totals: [{ id: "T1", group: "reel", metric: "views", value: 12, available: 1, rows: 1 }] },
          result: { summary: `Báo cáo riêng ${pageId}`, highlights: [{ text: "Xem lượt xem", evidence: ["T1"] }],
            improvements: [], recommendations: [], limitations: ["Dữ liệu mẫu"] } }] };
      } else if (req.command === "analytics.analysis.start" || req.command === "analytics.analysis.retry") {
        aiAnalysisStarts++;
      } else if (req.command === "facebook.analytics.sync" || req.command === "facebook.analytics.pages.sync") {
        facebookAnalyticsSyncCalls++;
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

async function waitForRendered(predicate: () => boolean) {
  for (let i = 0; i < 100 && !predicate(); i++) {
    await act(async () => { await new Promise(resolve => setTimeout(resolve, 20)); });
  }
}

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
  await act(async () => promptTab.click());
  await act(async () => { publishingTab.click(); await new Promise(r => setTimeout(r, 50)); });
  assert.equal(container.querySelector<HTMLSelectElement>("#publish-video")!.value, "ast_video", "Chuyển route giữ video đã chọn.");
  assert.equal(container.querySelector<HTMLTextAreaElement>("#publish-caption")!.value, "Caption đã lưu #test", "Chuyển route giữ caption.");
  const platformSelect = container.querySelector<HTMLSelectElement>("#navigation-platform")!;
  const switchPlatform = (value: string) => { platformSelect.value = value; platformSelect.dispatchEvent(new Event("change", { bubbles: true })); };
  assert.equal(platformSelect.value, "tiktok");
  assert.equal(container.querySelector("nav select"), platformSelect, "Bộ chọn nền tảng đứng đầu menu.");
  await act(async () => { switchPlatform("facebook"); await new Promise(r => setTimeout(r, 100)); });
  assert.ok(promptTab.closest(".hidden"), "Prompt nằm trong TikTok, ẩn khi chọn Facebook.");
  assert.ok(navItems.find(b => b.textContent === "Dự án")!.closest(".hidden"), "Dự án thuộc TikTok.");
  assert.equal(mediaTab.closest(".hidden"), null, "Tài nguyên vẫn dùng chung.");
  const composeRoute = Array.from(container.querySelectorAll<HTMLButtonElement>("nav button")).find(button => button.textContent === "Đăng bài nhóm")!;
  await act(async () => composeRoute.click());
  assert.equal(composeRoute.getAttribute("aria-current"), "page");
  const groupNav = container.querySelector('nav[aria-label="Đăng bài Facebook"]');
  assert.ok(!Array.from(groupNav?.querySelectorAll("button") ?? []).some(b => ["Hàng đợi", "Lịch sử", "Nháp"].includes(b.textContent?.trim() ?? "")), "Màn soạn nhóm không còn nút quản lý trùng sidebar");
  await act(async () => { switchPlatform("tiktok"); await new Promise(r => setTimeout(r, 50)); });
  assert.equal(publishingTab.getAttribute("aria-current"), "page", "Nhớ route TikTok trước khi chuyển nền tảng.");
  assert.equal(container.querySelector<HTMLTextAreaElement>("#publish-caption")!.value, "Caption đã lưu #test", "Chuyển Facebook không làm mất nháp TikTok.");
  await act(async () => switchPlatform("facebook"));
  assert.equal(composeRoute.getAttribute("aria-current"), "page", "Nhớ route Facebook trước khi chuyển nền tảng.");
  await act(async () => switchPlatform("tiktok"));
  const accountSettings = Array.from(container.querySelectorAll("button")).find(b => b.textContent?.trim() === "Cài đặt tài khoản")!;
  await act(async () => { accountSettings.click(); });
  assert.equal(Array.from(container.querySelectorAll("nav button")).find(b => b.textContent?.trim() === "Cài đặt")?.getAttribute("aria-current"), "page");
  assert.equal(container.querySelector('[role="tab"][aria-selected="true"]')?.textContent?.includes("Tài khoản"), true);
  console.log("  -> PASS: Đăng video đúng vị trí, nối bridge, giữ draft và chuyển Cài đặt tài khoản.");

  for (const label of ["Đăng bài cá nhân", "Đăng bài Page"]) {
    const tab = container.querySelector<HTMLButtonElement>(`nav button[aria-current="page"]`);
    const target = Array.from(container.querySelectorAll<HTMLButtonElement>("nav button.nav-item")).find(button => button.textContent === label)!;
    assert.ok(target);
    await act(async () => target.click());
    assert.equal(target.getAttribute("aria-current"), "page");
    assert.equal(composeRoute.getAttribute("aria-current"), null);
    if (label === "Đăng bài Page") { await act(async () => new Promise(r => setTimeout(r, 100))); assert.ok(container.textContent?.includes("Chưa hỗ trợ đăng bài")); }
    else { await act(async () => new Promise(r => setTimeout(r, 100))); assert.ok(container.querySelector("#personal-text")); }
    if (tab && tab !== target) assert.equal(tab.getAttribute("aria-current"), null);
  }
  const queueRoute = Array.from(container.querySelectorAll<HTMLButtonElement>("nav button.nav-item")).find(button => button.textContent === "Hàng đợi")!;
  const personalRoute = Array.from(container.querySelectorAll<HTMLButtonElement>("nav button.nav-item")).find(button => button.textContent === "Đăng bài cá nhân")!;
  await act(async () => { personalRoute.click(); await new Promise(r => setTimeout(r, 50)); });
  const personalText = container.querySelector<HTMLTextAreaElement>("#personal-text")!;
  await act(async () => { Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")!.set!.call(personalText, "Nháp giữ qua hàng đợi"); personalText.dispatchEvent(new Event("input", { bubbles: true })); });
  await act(async () => { queueRoute.click(); await new Promise(r => setTimeout(r, 100)); });
  assert.equal(queueRoute.getAttribute("aria-current"), "page");
  const queue = container.querySelector('[aria-label="Hàng đợi Facebook chung"]')!;
  await waitForRendered(() => queue.querySelectorAll("article").length === 2);
  assert.match(queue.textContent!, /Bài cá nhân chung/); assert.match(queue.textContent!, /Bài nhóm chung/);
  await act(async () => personalRoute.click());
  assert.equal(personalText.value, "Nháp giữ qua hàng đợi");
  await act(async () => { queueRoute.click(); await new Promise(r => setTimeout(r, 30)); });
  const articles = Array.from(queue.querySelectorAll("article"));
  await act(async () => { Array.from(articles[1].querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent === "Nhân bản")!.click(); });
  assert.equal(personalRoute.getAttribute("aria-current"), "page"); assert.equal(personalText.value, "Nội dung cá nhân chung");
  await act(async () => { queueRoute.click(); await new Promise(r => setTimeout(r, 30)); });
  await act(async () => { Array.from(queue.querySelector("article")!.querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent === "Nhân bản")!.click(); });
  assert.equal(composeRoute.getAttribute("aria-current"), "page");
  assert.equal(container.querySelector<HTMLTextAreaElement>("#facebook-text")!.value, "Nội dung nhóm chung");
  await act(async () => { Array.from(container.querySelectorAll<HTMLButtonElement>("nav button.nav-item")).find(b => b.textContent === "Đăng bài Page")!.click(); });
  const draftsRoute = Array.from(container.querySelectorAll<HTMLButtonElement>("nav button.nav-item")).find(b => b.textContent === "Nháp")!;
  const historyRoute = Array.from(container.querySelectorAll<HTMLButtonElement>("nav button.nav-item")).find(b => b.textContent === "Lịch sử")!;
  await act(async () => { historyRoute.click(); await new Promise(r => setTimeout(r, 30)); });
  assert.equal(historyRoute.getAttribute("aria-current"), "page"); assert.equal(queueRoute.getAttribute("aria-current"), null);
  assert.ok(Array.from(container.querySelectorAll("h2")).some(h => h.textContent === "Lịch sử Facebook"));
  await act(async () => { draftsRoute.click(); await new Promise(r => setTimeout(r, 30)); });
  const drafts = container.querySelector('[aria-label="Nháp Facebook chung"]')!;
  await waitForRendered(() => drafts.querySelectorAll("article").length === 2);
  assert.equal(draftsRoute.getAttribute("aria-current"), "page");
  assert.equal(drafts.querySelectorAll("article").length, 2);
  await act(async () => { drafts.querySelectorAll("article")[1].querySelector<HTMLButtonElement>("button")!.click(); });
  assert.equal(personalRoute.getAttribute("aria-current"), "page");
  assert.equal(personalText.value, "Nháp Nội dung cá nhân chung");
  assert.ok(container.querySelector<HTMLInputElement>("#personal-due")!.value);
  await act(async () => { Array.from(personalText.closest("section")!.querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent === "Lưu nháp")!.click(); await new Promise(r => setTimeout(r, 30)); });
  assert.equal(savedDraftIds.at(-1), "personal:a", "Sửa nháp cá nhân giữ mã nháp gốc.");
  await act(async () => { draftsRoute.click(); await new Promise(r => setTimeout(r, 30)); });
  await act(async () => { drafts.querySelector("article")!.querySelector<HTMLButtonElement>("button")!.click(); });
  assert.equal(composeRoute.getAttribute("aria-current"), "page");
  assert.equal(container.querySelector<HTMLTextAreaElement>("#facebook-text")!.value, "Nháp Nội dung nhóm chung");
  await act(async () => { Array.from(container.querySelector("#facebook-text")!.closest("section")!.querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent === "Cập nhật nháp")!.click(); await new Promise(r => setTimeout(r, 30)); });
  assert.equal(savedDraftIds.at(-1), "group:a", "Sửa nháp nhóm giữ mã nháp gốc.");
  await act(async () => { Array.from(container.querySelectorAll<HTMLButtonElement>("nav button.nav-item")).find(b => b.textContent === "Đăng bài Page")!.click(); });
  assert.equal(Array.from(container.querySelectorAll("nav button")).some(button => button.textContent === "Facebook Analytics"), false, "Không còn Analytics riêng trong sidebar.");
  assert.equal(facebookAnalyticsCatalogCalls, 0, "Chưa mở tab Analytics thì chưa tải catalog.");
  const pageRoute = Array.from(container.querySelectorAll<HTMLButtonElement>("nav button")).find(button => button.textContent === "Đăng bài Page")!;
  const pageTabs = container.querySelector('[role="tablist"][aria-label="Đăng bài Page"]')!;
  assert.ok(pageTabs);
  const analyticsTab = Array.from(pageTabs.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent === "Analytics")!;
  const postingTab = Array.from(pageTabs.querySelectorAll<HTMLButtonElement>('[role="tab"]')).find(tab => tab.textContent === "Đăng bài")!;
  const selectTab = async (tab: HTMLButtonElement) => {
    await act(async () => { tab.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true })); await new Promise(r => setTimeout(r, 100)); });
  };
  const waitForReport = async (pageId: string) => {
    for (let i = 0; i < 30 && !container.textContent?.includes(`Báo cáo riêng ${pageId}`); i++) {
      await act(async () => new Promise(r => setTimeout(r, 25)));
    }
  };
  await selectTab(analyticsTab);
  await waitForReport("fb-page-1");
  assert.equal(analyticsTab.getAttribute("aria-selected"), "true");
  assert.equal(pageRoute.getAttribute("aria-current"), "page");
  assert.equal(facebookAnalyticsCatalogCalls, 1);
  assert.match(container.textContent!, /Báo cáo riêng fb-page-1/);
  assert.match(container.textContent!, /Lượt xem 12/);
  const pageSelect = container.querySelector<HTMLSelectElement>('select[aria-label="Fanpage"]')!;
  await act(async () => { pageSelect.value = "fb-page-2"; pageSelect.dispatchEvent(new Event("change", { bubbles: true })); await new Promise(r => setTimeout(r, 50)); });
  const search = container.querySelector<HTMLInputElement>('input[aria-label="Tìm nội dung"]')!;
  await act(async () => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!.call(search, "giữ bộ lọc"); search.dispatchEvent(new Event("input", { bubbles: true })); });
  await selectTab(postingTab);
  await selectTab(analyticsTab);
  assert.equal(pageSelect.value, "fb-page-2", "Đổi tab giữ Fanpage đã chọn.");
  assert.equal(search.value, "giữ bộ lọc", "Đổi tab giữ bộ lọc.");
  await waitForReport("fb-page-2");
  assert.match(container.textContent!, /Báo cáo riêng fb-page-2/);
  assert.doesNotMatch(container.textContent!, /Báo cáo riêng fb-page-1/);
  await act(async () => composeRoute.click());
  await act(async () => pageRoute.click());
  assert.equal(analyticsTab.getAttribute("aria-selected"), "true", "Quay lại màn Page nhớ tab Analytics.");
  assert.equal(pageSelect.value, "fb-page-2");
  assert.equal(facebookAnalyticsCatalogCalls, 1, "Analytics giữ mounted khi đổi tab/route.");
  assert.equal(facebookAnalyticsSyncCalls, 0, "Đổi tab/route không tự gọi Facebook.");
  assert.equal(aiAnalysisStarts, 0, "Mở báo cáo hay đổi Fanpage không tự gửi AI.");
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
