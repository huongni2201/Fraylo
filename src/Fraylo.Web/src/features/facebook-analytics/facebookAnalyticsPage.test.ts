import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { FacebookAnalyticsApi, FacebookDashboard, FacebookCatalog, FacebookContent } from "./facebookAnalyticsApi";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { FacebookAnalyticsPage } = await import("./FacebookAnalyticsPage");
const { formatMetric } = await import("./facebookAnalyticsApi");
const catalog: FacebookCatalog = { accounts: [{ accountId: "a", name: "Account A", enabled: true, sessionStatus: "Connected" }, { accountId: "b", name: "Account B", enabled: true, sessionStatus: "Connected" }], pages: [{ id: "pa", accountId: "a", facebookPageId: "100", name: "Page A", url: "https://www.facebook.com/a" }, { id: "pb", accountId: "b", facebookPageId: "200", name: "Page B", url: "https://www.facebook.com/b" }] };
const item: FacebookContent = { contentId: "123", kind: "Reel", title: "Reel đã lưu", url: "https://www.facebook.com/reel/123/", publishedLabel: "23 Tháng 5, 2024", publishedDate: "2024-05-23", metrics: { views: { value: null, raw: "‑‑", approximate: false, missingReason: "unavailable" }, comments: { value: 0, raw: "0", approximate: false, missingReason: null } } };
const dashboard: FacebookDashboard = { capture: { id: "capture", pageId: "pa", status: "Partial", startedAtUtc: "2026-11-06T09:00:00Z", finishedAtUtc: "2026-11-06T09:01:00Z", recordCount: 1, message: "Có chỉ số bị thiếu", source: "facebook", scope: "Trọn đời · 50 nội dung" }, latestAttempt: null, content: [item], totalCount: 1, page: 1, totals: { views: { value: null, available: 0, total: 1, approximate: false }, comments: { value: 0, available: 1, total: 1, approximate: false } }, captures: [] };
const unused = async (): Promise<never> => { throw new Error("Unexpected API"); };
const settle = async () => { await React.act(async () => { await new Promise(resolve => setTimeout(resolve, 35)); }); };
async function mount(override: Partial<FacebookAnalyticsApi> = {}) {
  let syncCalls = 0; let pageCalls = 0;
  const api: FacebookAnalyticsApi = { catalog: async () => catalog, dashboard: async () => dashboard, syncPages: async () => { pageCalls++; return catalog; }, sync: async () => { syncCalls++; return { ...dashboard.capture!, status: "Running" }; }, cancel: unused, history: async () => [{ captureId: "capture", capturedAtUtc: "2026-11-06T09:00:00Z", content: item }], ...override };
  const host = document.createElement("div"); document.body.append(host);
  const root = createRoot(host);
  await React.act(async () => { root.render(React.createElement(FacebookAnalyticsPage, { active: true, onOpenAccounts() {}, api })); });
  await settle(); await settle();
  return { host, api, get syncCalls() { return syncCalls; }, get pageCalls() { return pageCalls; }, close: async () => { await React.act(async () => root.unmount()); host.remove(); } };
}
function button(host: HTMLElement, name: string) { const node = [...host.querySelectorAll("button")].find(b => b.textContent === name); assert.ok(node, name); return node; }
test("Mở trang/restart dùng SQLite, null khác 0 và phạm vi không là tổng Fanpage", async () => {
  for (let run = 0; run < 2; run++) {
    const view = await mount();
    assert.equal(view.syncCalls, 0); assert.equal(view.pageCalls, 0);
    assert.match(view.host.textContent!, /Số liệu tích lũy/); assert.match(view.host.textContent!, /không phải tổng toàn Fanpage/);
    assert.equal(formatMetric(item.metrics.views), "—"); assert.equal(formatMetric(item.metrics.comments), "0");
    assert.match(view.host.textContent!, /Từ 0\/1 nội dung/);
    await React.act(async () => button(view.host, "Tải Fanpage từ Facebook").click()); await settle();
    assert.equal(view.pageCalls, 1); assert.equal(view.syncCalls, 0);
    await view.close();
  }
});
test("Đổi account bỏ phản hồi dashboard đến muộn", async () => {
  let late: (value: FacebookDashboard) => void = () => {};
  const view = await mount({ dashboard: async q => q.accountId === "a" ? new Promise(resolve => { late = resolve; }) : { ...dashboard, content: [{ ...item, title: "Reel Account B" }] } });
  const select = view.host.querySelector('select[aria-label="Tài khoản Facebook"]') as HTMLSelectElement;
  await React.act(async () => { select.value = "b"; select.dispatchEvent(new Event("change", { bubbles: true })); }); await settle();
  await React.act(async () => late(dashboard)); await settle();
  assert.match(view.host.textContent!, /Reel Account B/); assert.doesNotMatch(view.host.textContent!, /Reel đã lưu/);
  await view.close();
});
test("Lịch sử giữ các mốc quan sát; request hủy gửi đúng capture", async () => {
  const running = { ...dashboard.capture!, status: "Running" };
  let canceled = "";
  const view = await mount({ dashboard: async () => ({ ...dashboard, latestAttempt: running }), cancel: async (_q, id) => { canceled = id; } });
  await React.act(async () => button(view.host, "Dừng cập nhật").click()); assert.equal(canceled, running.id);
  await React.act(async () => button(view.host, "Reel đã lưu").click()); await settle();
  assert.match(document.body.textContent!, /Các mốc quan sát gần nhất/); assert.match(document.body.textContent!, /không phải lượt phát sinh theo ngày/);
  await view.close();
});
test("Lỗi cập nhật giữ dữ liệu đã lưu và không tự sync", async () => {
  const view = await mount({ sync: async () => { throw new Error("Mất kết nối"); } });
  await React.act(async () => button(view.host, "Cập nhật số liệu").click()); await settle();
  assert.match(view.host.textContent!, /Mất kết nối/); assert.match(view.host.textContent!, /Reel đã lưu/);
  await view.close();
});
