import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { FacebookApi, FacebookBatchRequest, FacebookList, FacebookPost } from "./facebookApi";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { FacebookPosts, postStatus } = await import("./FacebookPosts");
const { FacebookPage } = await import("./FacebookPage");
const now = new Date().toISOString();
const post: FacebookPost = {
  batchId: "batch", accountId: "fb", name: "Bài thử AI", paused: false, revision: 7, createdAtUtc: now, originalText: "Nguồn ban đầu",
  tasks: ["Bản riêng nhóm một", "Bản riêng nhóm hai"].map((text, i) => ({ taskId: `task-${i}`, batchId: "batch", accountId: "fb", targetId: `group-${i}`, groupName: `Nhóm ${i}`, groupUrl: `https://www.facebook.com/groups/${i}/`, text, state: "Queued", message: "Chờ", createdAtUtc: now, updatedAtUtc: now, submittedAtUtc: null })),
  rewriteChunks: [{ chunkId: "chunk", ordinal: 1, count: 2, state: "Failed", reason: "InvalidRewriteResult", applied: false, canRetry: true, uncertain: false }],
};
const data: FacebookList = { accounts: [{ accountId: "fb", displayName: "Facebook", accountIdentity: "Facebook (1)", sessionStatus: "Connected" }], groups: post.tasks.map(t => ({ targetId: t.targetId, accountId: "fb", name: t.groupName, url: t.groupUrl })), tasks: post.tasks, posts: [post], rewriteProvider: "chatgpt-web" };
const unused = async (): Promise<never> => { throw new Error("unused"); };
const api: FacebookApi = { list: async () => data, create: unused, sync: unused, cancel: unused, resume: unused, postNow: unused, reschedule: unused, saveDraft: unused, deleteDraft: unused };

test("Viết lại Facebook: trạng thái AI tách khỏi đăng, pause và lỡ lịch vẫn ưu tiên", () => {
  assert.equal(postStatus(post), "Cần xử lý AI");
  assert.equal(postStatus({ ...post, rewriteChunks: post.rewriteChunks!.map(c => ({ ...c, state: "Running" })) }), "Đang viết lại");
  assert.equal(postStatus({ ...post, rewriteChunks: post.rewriteChunks!.map(c => ({ ...c, state: "Queued" })) }), "Chờ AI");
  assert.equal(postStatus({ ...post, paused: true }), "Tạm dừng");
  assert.equal(postStatus({ ...post, tasks: post.tasks.map(t => ({ ...t, state: "NeedsUser", reasonCode: "schedule_missed" })) }), "Đã lỡ lịch");
  assert.equal(postStatus({ ...post, tasks: post.tasks.map(t => ({ ...t, state: "Canceled" })) }), "Đã hủy");
});

test("Chi tiết dùng đúng text từng nhóm, retry mang scope/revision; uncertain không có nút gửi lại", async () => {
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  const calls: unknown[][] = []; let refreshed = 0;
  const button = (name: string) => Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent?.trim() === name)!;
  const render = (value: FacebookPost) => React.createElement(FacebookPosts, { data: { ...data, posts: [value] }, api: { ...api, retryRewrite: async (...args) => { calls.push(args); } }, history: false, onRefresh: async () => { refreshed++; }, onError: () => {}, onClone: () => {} });
  try {
    await React.act(async () => root.render(render(post)));
    assert.match(container.textContent!, /AI: đã có 0\/2 nội dung/);
    await React.act(async () => button("Chi tiết").click());
    for (const task of post.tasks) assert.match(container.textContent!, new RegExp(task.text));
    await React.act(async () => button("Tiếp tục / thử lại lượt AI 1").click());
    assert.deepEqual(calls, [["batch", "chunk", 7]]); assert.equal(refreshed, 1);
    await React.act(async () => root.render(render({ ...post, rewriteChunks: post.rewriteChunks!.map(c => ({ ...c, state: "AwaitingUser", canRetry: false, uncertain: true })) })));
    assert.match(container.textContent!, /AI có thể đã nhận yêu cầu/);
    assert.equal(button("Tiếp tục / thử lại lượt AI 1"), undefined);
  } finally { await React.act(async () => root.unmount()); container.remove(); }
});

test("Nhân bản bài AI dùng nguồn gốc, form cho biết provider và số lô trước xác nhận", async () => {
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  try {
    await React.act(async () => root.render(React.createElement(FacebookPage, { active: true, view: "compose", clonePost: post, api, onNavigate: () => {}, onOpenAccounts: () => {} })));
    assert.equal(container.querySelector<HTMLTextAreaElement>("#facebook-text")!.value, "Nguồn ban đầu");
    assert.match(container.textContent!, /ChatGPT Web/);
    const review = Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent?.trim() === "Xem lại lô đăng")!;
    await React.act(async () => review.click());
    const dialog = document.querySelector('[role="dialog"]')!;
    assert.match(dialog.textContent!, /AI tạo 2 nội dung riêng trong 1 lượt/);
  } finally { await React.act(async () => root.unmount()); container.remove(); }
});

test("Đổi cách đăng: xác nhận nguyên bản không cần AI, retry giữ mã và đổi lựa chọn cấp mã mới", async () => {
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  const requests: FacebookBatchRequest[] = [];
  const button = (name: string) => Array.from(document.querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent?.trim() === name)!;
  const original = { ...post, rewriteChunks: null, originalText: null };
  try {
    await React.act(async () => root.render(React.createElement(FacebookPage, { active: true, view: "compose", clonePost: original,
      api: { ...api, list: async () => ({ ...data, rewriteProvider: null }), create: async request => { requests.push(request); throw new Error("timeout"); } }, onNavigate: () => {}, onOpenAccounts: () => {} })));
    assert.equal(container.querySelectorAll<HTMLInputElement>('[name="facebook-content-mode"]')[1].checked, true, "Bản sao nguyên bản giữ lựa chọn");
    await React.act(async () => button("Xem lại lô đăng").click());
    assert.match(document.querySelector('[role="dialog"]')!.textContent!, /Đăng nguyên bản: giữ nguyên/);
    assert.doesNotMatch(document.querySelector('[role="dialog"]')!.textContent!, /AI tạo/);
    await React.act(async () => button("Xác nhận tạo 2 tác vụ").click());
    await React.act(async () => button("Xác nhận tạo 2 tác vụ").click());
    assert.equal(requests[0].rewriteContent, false);
    assert.equal(requests[0].text, post.tasks[0].text);
    assert.equal(requests[0].batchRequestId, requests[1].batchRequestId);
    await React.act(async () => button("Quay lại chỉnh sửa").click());
    await React.act(async () => container.querySelectorAll<HTMLInputElement>('[name="facebook-content-mode"]')[0].click());
    await React.act(async () => button("Xem lại lô đăng").click());
    assert.match(document.querySelector('[role="dialog"]')!.textContent!, /AI tạo 2 nội dung riêng/);
    await React.act(async () => button("Xác nhận tạo 2 tác vụ").click());
    assert.equal(requests[2].rewriteContent, true);
    assert.notEqual(requests[2].batchRequestId, requests[0].batchRequestId);
  } finally { await React.act(async () => root.unmount()); container.remove(); }
});
