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

async function mountPage(api: FacebookApi, view: "compose" | "queue" | "history", onNavigate: (view: string) => void = () => {}) {
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  await React.act(async () => root.render(React.createElement(FacebookPage, { active: true, view, api, onNavigate, onOpenAccounts: () => {} })));
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
    await page.click("Chọn tất cả 4 nhóm");
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
  const stored: FacebookDraft = { draftId: "d1", name: "Nháp cũ", accountId: "a", text: "Nội dung nháp", targetIds: ["a0", "gone"], imageAssetIds: [], updatedAtUtc: new Date().toISOString() };
  const api: FacebookApi = {
    list: async () => ({ accounts: [account], groups, tasks: [], drafts: [stored] }), sync: unused, create: async () => [], cancel: unused, resume: unused, postNow: unused, reschedule: unused,
    saveDraft: async input => { saved.push(input); return { draftId: input.draftId ?? "d2", name: input.name, accountId: input.accountId, text: input.text, targetIds: input.targetIds, imageAssetIds: input.imageAssetIds, updatedAtUtc: new Date().toISOString() }; },
    deleteDraft: async id => { deleted.push(id); },
  };
  const page = await mountPage(api, "compose");
  try {
    await page.setValue("#facebook-account", "a");
    assert.match(page.container.textContent!, /Nháp cũ/);
    await React.act(async () => page.container.querySelector<HTMLButtonElement>('[aria-label="Mở nháp Nháp cũ"]')!.click());
    assert.equal(page.container.querySelector<HTMLTextAreaElement>("#facebook-text")!.value, "Nội dung nháp");
    assert.match(page.container.textContent!, /Đã chọn 1 nhóm/);
    assert.match(page.container.textContent!, /1 nhóm không còn trong danh sách/);
    await page.setValue("#facebook-text", "Nội dung đã sửa");
    await page.click("Cập nhật nháp");
    assert.equal(saved.length, 1);
    assert.deepEqual([saved[0].draftId, saved[0].text, saved[0].targetIds], ["d1", "Nội dung đã sửa", ["a0"]]);
    await page.click("Lưu thành nháp mới");
    await page.click("Lưu nháp");
    assert.equal(saved[1].draftId, undefined, "Nháp mới không ghi đè nháp cũ");
    await React.act(async () => page.container.querySelector<HTMLButtonElement>('[aria-label="Xóa nháp Nháp cũ"]')!.click());
    assert.deepEqual(deleted, ["d1"]);
  } finally { await page.unmount(); }
});

test("Hàng đợi: hẹn giờ có Đăng ngay/Đổi giờ, lỡ lịch Đăng ngay, hủy cả lô cần xác nhận", async () => {
  const future = new Date(Date.now() + 3600_000).toISOString();
  const tasks = [
    makeTask({ taskId: "s1", batchId: "b1", dueAtUtc: future }),
    makeTask({ taskId: "m1", batchId: "b2", state: "NeedsUser", reasonCode: "schedule_missed", dueAtUtc: new Date(Date.now() - 3600_000).toISOString(), message: "Đã lỡ giờ hẹn" }),
    makeTask({ taskId: "c1", batchId: "b3" }), makeTask({ taskId: "c2", batchId: "b3" }), makeTask({ taskId: "c3", batchId: "b3", state: "Submitting" }),
  ];
  const calls: string[] = [];
  const api: FacebookApi = {
    list: async () => ({ accounts: [account], groups, tasks }), sync: unused, create: async () => [],
    cancel: async id => { calls.push("cancel:" + id); return { ...tasks.find(task => task.taskId === id)!, state: "Canceled" }; },
    resume: async id => { calls.push("resume:" + id); return { ...tasks.find(task => task.taskId === id)!, state: "Queued" }; },
    postNow: async id => { calls.push("postNow:" + id); return { ...tasks.find(task => task.taskId === id)!, dueAtUtc: null }; },
    reschedule: async (id, due) => { calls.push("reschedule:" + id); return { ...tasks.find(task => task.taskId === id)!, dueAtUtc: due }; },
    saveDraft: unused, deleteDraft: unused,
  };
  const page = await mountPage(api, "queue");
  try {
    const text = () => page.container.textContent!;
    assert.match(text(), /Đã hẹn giờ/); assert.match(text(), /Đã lỡ lịch/);
    await page.click("Đăng ngay", 0);
    assert.deepEqual(calls, ["postNow:s1"]);
    await page.click("Đăng ngay", 0);
    assert.deepEqual(calls, ["postNow:s1", "resume:m1"], "Lỡ lịch: Đăng ngay chọn Tiếp tục");
    await page.click("Đổi giờ", 0);
    await page.setValue('input[id^="facebook-reschedule-"]', utcToLocalInput(new Date(Date.now() + 5 * 3600_000).toISOString()));
    await page.click("Lưu giờ hẹn");
    assert.equal(calls[2].startsWith("reschedule:"), true);
    assert.equal(page.buttons("Hủy cả lô (2)").length, 2, "Chỉ đếm bài chưa gửi (bỏ Submitting); nút hiện ở từng bài hủy được của lô");
    await page.click("Hủy cả lô (2)");
    assert.equal(calls.length, 3, "Bấm lần đầu chỉ xin xác nhận");
    await page.click("Xác nhận hủy 2 bài chưa gửi");
    assert.deepEqual(calls.slice(3), ["cancel:c1", "cancel:c2"], "Không hủy bài đã Submitting");
    assert.match(text(), /Đã hủy 2 bài chưa gửi của lô/);
  } finally { await page.unmount(); }
});

test("Lịch sử: lọc trạng thái/tên nhóm; Đăng lại nạp nội dung vào Soạn bài", async () => {
  const tasks = [
    makeTask({ taskId: "h1", state: "Published", groupName: "Nhóm Mèo" }),
    makeTask({ taskId: "h2", state: "Failed", groupName: "Nhóm Chó", reasonCode: "composer_not_found", text: "Nội dung cũ", imageAssetIds: ["img1"], targetId: "a1" }),
    makeTask({ taskId: "h3", state: "Unknown", groupName: "Nhóm Cá" }),
  ];
  const api: FacebookApi = { list: async () => ({ accounts: [account], groups, tasks }), sync: unused, create: async () => [], cancel: unused, resume: unused, ...unusedApi };
  let navigated = "";
  const page = await mountPage(api, "history", next => { navigated = next; });
  try {
    const titles = () => Array.from(page.container.querySelectorAll("article h3")).map(item => item.textContent);
    assert.deepEqual(titles(), ["Nhóm Mèo", "Nhóm Chó", "Nhóm Cá"]);
    await page.setValue("#facebook-history-state", "Failed");
    assert.deepEqual(titles(), ["Nhóm Chó"]);
    assert.match(page.container.textContent!, /Mã: composer_not_found/);
    await page.setValue("#facebook-history-state", "all");
    await page.setValue('[aria-label="Tìm nhóm trong lịch sử"]', "mèo");
    assert.deepEqual(titles(), ["Nhóm Mèo"]);
    await page.setValue('[aria-label="Tìm nhóm trong lịch sử"]', "");
    assert.equal(page.buttons("Đăng lại").length, 2, "Chỉ Failed/Unknown/Canceled mới Đăng lại");
    await page.click("Đăng lại", 0);
    assert.equal(navigated, "compose");
    assert.match(page.container.textContent!, /Đã nạp lại nội dung/);
  } finally { await page.unmount(); }
});
