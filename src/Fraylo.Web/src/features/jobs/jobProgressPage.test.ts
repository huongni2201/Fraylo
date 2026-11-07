import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { JobDetailDto, JobSummaryDto } from "./jobTypes";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { JobProgressPage } = await import("./JobProgressPage");
const { jobApi } = await import("./jobApi");
const { projectApi } = await import("../projects/projectApi");

test("Tiến trình chung: dòng gọn cho mọi loại job, lọc, phân trang, mở chi tiết và hủy", async (t) => {
  const makeJob = (id: string, taskType: string, state: JobSummaryDto["state"]): JobSummaryDto => ({
    id, taskType, state, projectId: null, sceneId: null, attempts: 1, reasonCode: null,
    progressPercent: state === "Running" ? 35 : null, currentStep: null,
    resultText: "NỘI DUNG CHỈ HIỆN TRONG CHI TIẾT", createdAtUtc: "2026-11-07T01:00:00Z",
  });
  const jobs = [makeJob("video", "VideoRender", "Running"), makeJob("facebook", "FacebookContentRewrite", "Queued"),
    { ...makeJob("analytics", "AnalyticsAnalysis", "Queued"), attempts: 0, reasonCode: "provider_account_required" },
    makeJob("script", "Script", "Succeeded"),
    makeJob("future", "NewTask", "Failed")];
  let failed = false;
  let analyticsRunning = false;
  let openedAccounts = 0;
  const canceled: string[] = [];
  t.mock.method(jobApi, "listActive", async () => { if (failed) throw new Error("Lỗi tải fixture"); return jobs.slice(0, 3); });
  t.mock.method(jobApi, "listHistory", async (request: { cursor?: string }) => request.cursor
    ? { items: [makeJob("older", "Image", "Succeeded")], nextCursor: null }
    : { items: jobs.slice(3), nextCursor: "next" });
  t.mock.method(jobApi, "onStateChanged", () => () => {});
  t.mock.method(jobApi, "onProgressUpdated", () => () => {});
  t.mock.method(projectApi, "listAll", async () => []);
  t.mock.method(jobApi, "getStatus", async (id: string): Promise<JobDetailDto> => ({
    ...jobs.find(j => j.id === id)!, providerKey: id === "analytics" ? "chatgpt-web" : "fixture", storageRootId: "root", maxAttempts: 3,
    ...(id === "analytics" && analyticsRunning ? { state: "Running", reasonCode: null } : {}),
    sideEffectSubmitted: 0, cancelRequested: false, resultReference: null, resultText: "NỘI DUNG CHỈ HIỆN TRONG CHI TIẾT",
    revision: 1, updatedAtUtc: "2026-11-07T01:00:00Z",
  }));
  t.mock.method(jobApi, "cancel", async (id: string) => { canceled.push(id); return { jobId: id, state: "Canceled", cancelRequested: true, message: "" }; });
  const host = document.createElement("div"); document.body.append(host); const root = createRoot(host);
  const rows = () => host.querySelectorAll('[aria-label="Danh sách tiến trình"] > li');
  const click = async (el: Element | null | undefined) => {
    assert.ok(el);
    await React.act(async () => {
      if (el.getAttribute("role") === "tab") el.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, button: 0 }));
      else (el as HTMLElement).click();
    });
  };
  try {
    await React.act(async () => root.render(React.createElement(JobProgressPage, {
      active: true, onNavigateToSettingsAccounts: () => { openedAccounts++; },
    })));
    assert.equal(rows().length, 5);
    assert.match(host.textContent!, /AI viết lại bài nhóm/);
    assert.match(host.textContent!, /AI phân tích số liệu/);
    assert.match(host.textContent!, /NewTask/);
    assert.match(host.textContent!, /35%/);
    assert.doesNotMatch(host.textContent!, /NỘI DUNG CHỈ HIỆN|Kịch bản \(AI\)|Nội dung đăng TikTok/);
    assert.equal(host.querySelectorAll("img, video").length, 0);
    assert.ok([...rows()].every(row => row.querySelectorAll("button").length === 1));
    await click([...host.querySelectorAll('[role="tab"]')].find(el => el.textContent?.startsWith("Hoàn tất")));
    assert.equal(rows().length, 1, "Giữ cả Script thành công, không còn lọc riêng pipeline video");
    await click([...host.querySelectorAll('[role="tab"]')].find(el => el.textContent?.startsWith("Tất cả")));
    await click([...host.querySelectorAll("button")].find(el => el.textContent === "Tải thêm"));
    assert.equal(rows().length, 6);
    await click(host.querySelector('[aria-label="Xem chi tiết AI phân tích số liệu · #analytic"]'));
    const waiting = document.querySelector('[role="dialog"]'); assert.ok(waiting);
    assert.match(waiting.textContent!, /Chưa có tài khoản AI đủ điều kiện/);
    assert.doesNotMatch(waiting.textContent!, /provider_account_required/);
    await click([...waiting.querySelectorAll("button")].find(el => el.textContent === "Đến Cài đặt Tài khoản"));
    assert.equal(openedAccounts, 1);
    assert.deepEqual(canceled, []);
    assert.equal(document.querySelector('[role="dialog"]'), null);
    analyticsRunning = true;
    await click(host.querySelector('[aria-label="Xem chi tiết AI phân tích số liệu · #analytic"]'));
    const running = document.querySelector('[role="dialog"]'); assert.ok(running);
    assert.doesNotMatch(running.textContent!, /Chưa có tài khoản AI đủ điều kiện/);
    await click([...running.querySelectorAll("button")].find(el => el.textContent === "Đóng"));
    await click(host.querySelector('[aria-label="Xem chi tiết AI viết lại bài nhóm · #facebook"]'));
    const dialog = document.querySelector('[role="dialog"]'); assert.ok(dialog);
    assert.match(dialog.textContent!, /NỘI DUNG CHỈ HIỆN TRONG CHI TIẾT/);
    await click([...dialog.querySelectorAll("button")].find(el => /Hủy tác vụ/.test(el.textContent ?? "")));
    assert.deepEqual(canceled, ["facebook"]);
    assert.equal(document.querySelector('[role="dialog"]'), null);
    failed = true;
    await click(host.querySelector('[aria-label="Làm mới tiến trình"]'));
    assert.match(host.querySelector('[role="alert"]')!.textContent!, /Lỗi tải fixture/);
    assert.equal(rows().length, 6, "Lỗi tải giữ dữ liệu đã có");
  } finally { await React.act(async () => root.unmount()); host.remove(); }
});
