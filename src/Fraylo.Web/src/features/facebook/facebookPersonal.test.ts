import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { FacebookApi, FacebookBatchRequest, FacebookDraftInput, FacebookList } from "./facebookApi";
const React = (await import("react")).default;
const { createRoot } = await import("react-dom/client");
const { FacebookPersonalPage } = await import("./FacebookPersonalPage");

test("Cá nhân: xác nhận, replay, nháp, giữ form và tách lịch sử nhóm", async () => {
  const requests: FacebookBatchRequest[] = [];
  const drafts: FacebookDraftInput[] = [];
  const data: FacebookList = { accounts: [{ accountId: "a", displayName: "Tài khoản A", accountIdentity: "Facebook (1)", sessionStatus: "Connected" }], groups: [], tasks: [], posts: [], drafts: [] };
  const api = { list: async () => data, create: async (input: FacebookBatchRequest) => { requests.push(input); throw new Error("Timeout thử nghiệm"); },
    saveDraft: async (input: FacebookDraftInput) => { drafts.push(input); return { ...input, draftId: "draft", updatedAtUtc: "" }; } } as unknown as FacebookApi;
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  const render = (active = true) => React.createElement(FacebookPersonalPage, { active, api, onOpenAccounts: () => {} });
  const buttons = (label: string) => Array.from(document.querySelectorAll<HTMLButtonElement>("button")).filter(b => b.textContent?.trim() === label);
  const click = async (label: string) => React.act(async () => buttons(label)[0].click());
  const set = async (selector: string, value: string) => React.act(async () => {
    const el = container.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(selector)!;
    const proto = el.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : el.tagName === "SELECT" ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, "value")!.set!.call(el, value);
    el.dispatchEvent(new Event(el.tagName === "SELECT" ? "change" : "input", { bubbles: true }));
  });
  try {
    await React.act(async () => root.render(render()));
    assert.equal(buttons("Xem lại bài đăng")[0].disabled, true);
    await set("#personal-account", "a"); await set("#personal-text", "Bài cá nhân thử nghiệm");
    await click("Lưu nháp"); assert.deepEqual(drafts[0].targetIds, ["personal:a"]);
    await React.act(async () => root.render(render(false))); await React.act(async () => root.render(render()));
    assert.equal(container.querySelector<HTMLTextAreaElement>("#personal-text")!.value, "Bài cá nhân thử nghiệm");
    await click("Xem lại bài đăng"); assert.equal(requests.length, 0);
    await click("Xác nhận tạo bài đăng"); await click("Xác nhận tạo bài đăng");
    assert.equal(requests.length, 2); assert.equal(requests[0].batchRequestId, requests[1].batchRequestId);
    assert.equal(requests[0].targetKind, "Profile"); assert.deepEqual(requests[0].targetIds, ["personal:a"]);
    await click("Quay lại chỉnh sửa");
    const check = container.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')[0];
    await React.act(async () => check.click());
    assert.equal(buttons("Xem lại bài đăng")[0].disabled, true, "Thiếu giờ thì chưa gửi");
    await set("#personal-due", new Date(Date.now() + 86400000).toISOString().slice(0, 16)); await click("Xem lại bài đăng"); await click("Xác nhận tạo bài đăng");
    assert.ok(requests[2].dueAtUtc); assert.notEqual(requests[2].batchRequestId, requests[0].batchRequestId);
    await click("Quay lại chỉnh sửa");
    assert.equal(container.querySelector('nav[aria-label="Đăng bài cá nhân"]'), null, "Không còn tab quản lý riêng trong màn soạn cá nhân");
  } finally { await React.act(async () => root.unmount()); container.remove(); }
});
