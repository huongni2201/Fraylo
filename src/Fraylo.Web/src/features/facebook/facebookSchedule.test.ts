import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { FacebookApi, FacebookBatchRequest, FacebookDraft, FacebookDraftInput, FacebookGroup, FacebookTask } from "./facebookApi";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { FacebookPage } = await import("./FacebookPage");
const { facebookDisplayState, localInputToUtc, utcToLocalInput } = await import("./facebookApi");

const groups: FacebookGroup[] = Array.from({ length: 4 }, (_, index) => ({ targetId: `a${index}`, accountId: "a", name: `Nhóm ${index}`, url: `https://www.facebook.com/groups/${index + 1}/` }));
const account = { accountId: "a", displayName: "Facebook A", accountIdentity: "Facebook (1)", sessionStatus: "Connected" };
const unused = async (): Promise<never> => { throw new Error("unused"); };
const unusedApi = { postNow: unused, reschedule: unused, saveDraft: unused, deleteDraft: unused };
const makeTask = (overrides: Partial<FacebookTask>): FacebookTask => ({
  taskId: "t1", batchId: "b1", accountId: "a", targetId: "a0", groupName: "Nhóm 0", groupUrl: groups[0].url, text: "Nội dung", state: "Queued", message: "msg",
  createdAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString(), submittedAtUtc: null, ...overrides,
});

async function mountPage(api: FacebookApi, view: "compose" | "queue" | "history", onNavigate: (view: string) => void = () => {}, openedDraft?: FacebookDraft) {
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  await React.act(async () => root.render(React.createElement(FacebookPage, { active: true, view, api, openedDraft, onNavigate, onOpenAccounts: () => {} })));
  const buttons = (label: string) => Array.from(container.querySelectorAll<HTMLButtonElement>("button")).filter(item => item.textContent?.trim() === label);
  const click = async (label: string, index = 0) => React.act(async () => buttons(label)[index].click());
  const setValue = async (selector: string, value: string) => React.act(async () => {
    const element = container.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(selector)!;
    const proto = element.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : element.tagName === "SELECT" ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, "value")!.set!.call(element, value);
    element.dispatchEvent(new Event(element.tagName === "SELECT" ? "change" : "input", { bubbles: true }));
  });
  const unmount = async () => { await React.act(async () => root.unmount()); container.remove(); };
  return { container, buttons, click, setValue, unmount };
}

test("Giờ hiển thị: hẹn giờ và lỡ lịch phân biệt qua giờ hẹn/mã lý do; giờ máy khứ hồi chính xác", () => {
  const now = Date.parse("2030-01-01T00:00:00Z");
  assert.equal(facebookDisplayState({ state: "Queued", dueAtUtc: "2030-01-01T01:00:00Z" }, now), "Scheduled");
  assert.equal(facebookDisplayState({ state: "Queued", dueAtUtc: "2029-12-31T23:00:00Z" }, now), "Queued", "Quá giờ hẹn thì là hàng đợi thường");
  assert.equal(facebookDisplayState({ state: "NeedsUser", reasonCode: "schedule_missed" }, now), "Missed");
  assert.equal(facebookDisplayState({ state: "NeedsUser", reasonCode: "session_expired" }, now), "NeedsUser");
  const iso = "2030-06-15T10:30:00.000Z";
  assert.equal(localInputToUtc(utcToLocalInput(iso)), iso, "datetime-local ↔ UTC giữ nguyên đến phút");
  assert.throws(() => localInputToUtc(""));
  assert.throws(() => localInputToUtc("không phải giờ"));
});

test("Hẹn giờ: chỉ tạo lô khi giờ hợp lệ ở tương lai và gửi dueAtUtc theo giờ máy", async () => {
  const requests: FacebookBatchRequest[] = [];
  const api: FacebookApi = { list: async () => ({ accounts: [account], groups, tasks: [] }), sync: unused, create: async request => { requests.push(request); return []; }, cancel: unused, resume: unused, ...unusedApi };
  const page = await mountPage(api, "compose");
  try {
    await page.setValue("#facebook-account", "a");
    await page.click("Chọn tất cả");
    await page.setValue("#facebook-text", "Bài hẹn giờ");
    assert.equal(page.buttons("Xem lại lô đăng")[0].disabled, false, "Đăng ngay không cần giờ");
    await React.act(async () => { page.container.querySelectorAll<HTMLInputElement>('input[name="facebook-mode"]')[1].click(); });
    assert.equal(page.buttons("Xem lại lô đăng")[0].disabled, true, "Hẹn giờ mà chưa nhập giờ thì chưa tạo được");
    await page.setValue("#facebook-due", utcToLocalInput(new Date(Date.now() - 3600_000).toISOString()));
    assert.equal(page.buttons("Xem lại lô đăng")[0].disabled, true, "Giờ quá khứ bị chặn");
    assert.match(page.container.textContent!, /Chọn giờ ở tương lai/);
    const future = utcToLocalInput(new Date(Date.now() + 2 * 3600_000).toISOString());
    await page.setValue("#facebook-due", future);
    await page.click("Xem lại lô đăng");
    assert.match(document.querySelector('[role="dialog"]')!.textContent!, /Hẹn giờ đăng:/);
    await React.act(async () => Array.from(document.querySelectorAll<HTMLButtonElement>("button")).find(item => item.textContent?.trim() === "Xác nhận tạo 4 tác vụ")!.click());
    assert.equal(requests.length, 1);
    assert.equal(requests[0].dueAtUtc, localInputToUtc(future));
  } finally { await page.unmount(); }
});

test("Nháp: lưu bền qua API, mở lại khôi phục nhóm/nội dung và bỏ nhóm không còn", async () => {
  const saved: FacebookDraftInput[] = [];
  const deleted: string[] = [];
  const stored: FacebookDraft = { draftId: "d1", name: "Nháp cũ", accountId: "a", text: "Nội dung nháp", rewriteContent: false, targetIds: ["a0", "gone"], imageAssetIds: [], updatedAtUtc: new Date().toISOString() };
  const api: FacebookApi = {
    list: async () => ({ accounts: [account], groups, tasks: [], drafts: [stored] }), sync: unused, create: async () => [], cancel: unused, resume: unused, postNow: unused, reschedule: unused,
    saveDraft: async input => { saved.push(input); return { draftId: input.draftId ?? "d2", name: input.name, accountId: input.accountId, text: input.text, targetIds: input.targetIds, imageAssetIds: input.imageAssetIds, updatedAtUtc: new Date().toISOString() }; },
    deleteDraft: unused,
  };
  const page = await mountPage(api, "compose", () => {}, stored);
  try {
    assert.equal(page.container.querySelector<HTMLTextAreaElement>("#facebook-text")!.value, "Nội dung nháp");
    assert.equal(page.container.querySelectorAll<HTMLInputElement>('[name="facebook-content-mode"]')[1].checked, true);
    assert.match(page.container.textContent!, /Đã chọn 1 nhóm/);
    assert.match(page.container.textContent!, /1 nhóm không còn trong danh sách/);
    await page.setValue("#facebook-text", "Nội dung đã sửa");
    await page.click("Cập nhật nháp");
    assert.equal(saved.length, 1);
    assert.equal(saved[0].rewriteContent, false, "Cập nhật nháp giữ lựa chọn nguyên bản");
    assert.deepEqual([saved[0].draftId, saved[0].text, saved[0].targetIds], ["d1", "Nội dung đã sửa", ["a0"]]);
    await page.click("Lưu thành nháp mới");
    await page.click("Lưu nháp");
    assert.equal(saved[1].draftId, undefined, "Nháp mới không ghi đè nháp cũ");
  } finally { await page.unmount(); }
});


test("Bài đăng: gom 20/30 nhóm, chi tiết, tạm dừng và hủy cả bài qua revision", async () => {
  const future = new Date(Date.now() + 3600_000).toISOString();
  const posts = [20, 30].map((n, i) => ({ batchId: "b" + i, name: "Bài " + i, accountId: "a", paused: false, revision: 1, createdAtUtc: new Date().toISOString(), tasks: Array.from({ length: n }, (_, j) => makeTask({ taskId: `${i}-${j}`, batchId: "b" + i, groupName: `Nhóm ${i}-${j}`, dueAtUtc: future })) }));
  const calls: string[] = [];
  const api: FacebookApi = { list: async () => ({ accounts: [account], groups, tasks: posts.flatMap(p => p.tasks), posts }), sync: unused, create: unused, cancel: unused, resume: unused, ...unusedApi,
    postAction: async (id, revision, action) => { calls.push(`${id}:${revision}:${action}`); const post = posts.find(p => p.batchId === id)!; post.revision++; post.paused = action === "pause"; return post; },
  };
  const page = await mountPage(api, "posts");
  try {
    assert.equal(page.container.querySelectorAll("article").length, 2, "Mỗi bài một dòng, không phải 50 dòng nhóm");
    assert.match(page.container.textContent!, /20 nhóm/); assert.match(page.container.textContent!, /30 nhóm/);
    assert.equal(page.container.querySelectorAll("article li").length, 0);
    await page.click("Chi tiết");
    assert.equal(page.container.querySelectorAll("article li").length, 20);
    await page.click("Tạm dừng"); assert.deepEqual(calls, ["b0:1:pause"]);
    assert.match(page.container.textContent!, /Tạm dừng/);
    await page.click("Tiếp tục"); assert.equal(calls[1], "b0:2:resume");
    await page.click("Hủy phần chưa gửi"); assert.equal(calls.length, 2, "Lần đầu chỉ xác nhận");
    await page.click("Xác nhận hủy phần chưa gửi"); assert.equal(calls[2], "b0:3:cancel");
  } finally { await page.unmount(); }
});

test("Lịch sử theo bài: PendingApproval và Unknown không tính là Published; nhân bản giữ toàn bộ nhóm", async () => {
  const tasks = [makeTask({ taskId: "p", state: "Published" }), makeTask({ taskId: "a", state: "PendingApproval" }), makeTask({ taskId: "u", state: "Unknown" })];
  const post = { batchId: "batch", name: "Bài cũ", accountId: "a", paused: false, revision: 1, createdAtUtc: new Date().toISOString(), tasks };
  let navigated = "";
  const api: FacebookApi = { list: async () => ({ accounts: [account], groups, tasks, posts: [post] }), sync: unused, create: unused, cancel: unused, resume: unused, ...unusedApi };
  const page = await mountPage(api, "history", next => { navigated = next; });
  try {
    assert.equal(page.container.querySelectorAll("article").length, 1);
    assert.match(page.container.textContent!, /1 đã đăng · 1 chờ duyệt/);
    await page.click("Chi tiết"); assert.match(page.container.textContent!, /Hãy kiểm tra nhóm/);
    await page.click("Nhân bản"); assert.equal(navigated, "compose");
  } finally { await page.unmount(); }
});
