import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { FacebookApi, FacebookDraft, FacebookList } from "./facebookApi";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { FacebookDrafts } = await import("./FacebookDrafts");

test("Nháp chung: lọc, mở đúng dữ liệu, xác nhận xóa và giữ nháp khi xóa lỗi", async () => {
  const drafts: FacebookDraft[] = [false, true].map((personal, i) => ({ draftId: String(i), name: `Nháp ${i}`, accountId: String(i), text: `Nội dung ${i}`, targetIds: [personal ? "personal:1" : "group:0"], imageAssetIds: ["ảnh"], updatedAtUtc: "", dueAtUtc: "2026-11-08T10:00:00Z" }));
  const data: FacebookList = { accounts: drafts.map(d => ({ accountId: d.accountId, displayName: `Tài khoản ${d.accountId}`, accountIdentity: null, sessionStatus: "Connected" })), groups: [], tasks: [], drafts };
  let opened: FacebookDraft | undefined; let error: string | null = null; let fail = true;
  const deleted: string[] = [];
  const api = { deleteDraft: async (id: string) => { deleted.push(id); if (fail) throw new Error("Không thể xóa nháp"); data.drafts = data.drafts!.filter(d => d.draftId !== id); } } as FacebookApi;
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  const render = () => root.render(React.createElement(FacebookDrafts, { data, api, onOpen: d => { opened = d; }, onError: e => { error = e; }, onRefresh: async () => { render(); } }));
  const click = async (label: string) => React.act(async () => { Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent === label)!.click(); });
  const select = async (label: string, value: string) => React.act(async () => { const el = container.querySelector<HTMLSelectElement>(`[aria-label="${label}"]`)!; el.value = value; el.dispatchEvent(new Event("change", { bubbles: true })); });
  try {
    await React.act(async () => render()); assert.equal(container.querySelectorAll("article").length, 2);
    await select("Lọc nơi đăng nháp", "personal"); assert.equal(container.querySelectorAll("article").length, 1);
    await click("Mở nháp"); assert.deepEqual(opened, drafts[1]);
    await select("Lọc tài khoản nháp", "0"); assert.match(container.textContent!, /Không có nháp khớp bộ lọc/);
    await select("Lọc tài khoản nháp", "all");
    await click("Xóa nháp"); assert.deepEqual(deleted, []);
    await click("Giữ nháp"); assert.deepEqual(deleted, []);
    await click("Xóa nháp"); await click("Xác nhận xóa nháp"); assert.equal(error, "Không thể xóa nháp");
    assert.equal(container.querySelectorAll("article").length, 1);
    fail = false; await click("Xác nhận xóa nháp"); assert.deepEqual(deleted, ["1", "1"]);
    assert.deepEqual(data.drafts, [drafts[0]]); assert.equal(error, null);
  } finally { await React.act(async () => root.unmount()); container.remove(); }
});
