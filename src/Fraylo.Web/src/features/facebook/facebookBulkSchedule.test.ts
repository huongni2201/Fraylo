import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { FacebookApi, FacebookBatchRequest } from "./facebookApi";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { FacebookBulkSchedule } = await import("./FacebookBulkSchedule");
const { simulate, newRow, GAP_MS } = await import("./FacebookBulkSchedule");
const { utcToLocalInput } = await import("./facebookApi");

const at = (minutes: number) => utcToLocalInput(new Date(Math.floor(Date.now() / 60000) * 60000 + (minutes + 10) * 60000).toISOString());
const row = (groups: number, due: string) => ({ ...newRow(due), targetIds: Array.from({ length: groups }, (_, index) => `g${index}`) });

test("Bài đến sau chờ bài trước của cùng tài khoản, cách đúng 5 phút", () => {
  const a = row(12, at(0)), b = row(8, at(30));
  const plan = simulate([b, a]);
  assert.equal(plan.get(a.id)!.delayed, false);
  assert.equal(plan.get(a.id)!.end - plan.get(a.id)!.start, 11 * GAP_MS);
  assert.equal(plan.get(b.id)!.start, plan.get(a.id)!.end + GAP_MS);
  assert.equal(plan.get(b.id)!.delayed, true);
});

test("Bài đủ xa nhau giữ nguyên giờ; bài thiếu nhóm hoặc giờ sai bị bỏ qua", () => {
  const a = row(2, at(0)), b = row(2, at(120)), none = row(0, at(10)), bad = row(2, "");
  const plan = simulate([a, b, none, bad]);
  assert.equal(plan.get(b.id)!.delayed, false);
  assert.equal(plan.has(none.id) || plan.has(bad.id), false);
});

test("Hẹn giờ hàng loạt: chọn riêng cách đăng từng bài, nhân bản giữ lựa chọn và gửi đúng request", async () => {
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  const requests: FacebookBatchRequest[] = [];
  const unused = async (): Promise<never> => { throw new Error("unused"); };
  const data = { accounts: [{ accountId: "fb", displayName: "Facebook", accountIdentity: "Facebook (1)", sessionStatus: "Connected" }], groups: [{ targetId: "g0", accountId: "fb", name: "Nhóm thử", url: "https://www.facebook.com/groups/1/" }], tasks: [] };
  const api: FacebookApi = { list: unused, sync: unused, create: async request => { requests.push(request); return []; }, cancel: unused, resume: unused, postNow: unused, reschedule: unused, saveDraft: unused, deleteDraft: unused };
  const button = (label: string) => Array.from(document.querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent?.trim() === label)!;
  const input = async (selector: string, value: string) => React.act(async () => {
    const element = container.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(selector)!;
    const proto = element.tagName === "SELECT" ? HTMLSelectElement.prototype : element.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, "value")!.set!.call(element, value);
    element.dispatchEvent(new Event(element.tagName === "SELECT" ? "change" : "input", { bubbles: true }));
  });
  try {
    await React.act(async () => root.render(React.createElement(FacebookBulkSchedule, { data, api, loading: false, onRefresh: async () => {}, onCreated: () => {}, onOpenAccounts: () => {} })));
    await input("#bulk-account", "fb");
    await input('[id^="bulk-text-"]', "Nội dung gốc");
    await input('[id^="bulk-due-"]', at(60));
    await React.act(async () => container.querySelectorAll<HTMLInputElement>('[name^="bulk-content-mode-"]')[1].click());
    await React.act(async () => button("Chọn nhóm đăng").click());
    await React.act(async () => button("Chọn tất cả").click());
    await React.act(async () => button("Xong — 1 nhóm").click());
    await React.act(async () => container.querySelector<HTMLButtonElement>('[aria-label="Nhân bản bài 1"]')!.click());
    const radios = container.querySelectorAll<HTMLInputElement>('[name^="bulk-content-mode-"]');
    assert.equal(radios[3].checked, true, "Bản sao giữ Đăng nguyên bản");
    await React.act(async () => radios[2].click());
    await React.act(async () => button("Xem lại và lên lịch 2 bài").click());
    assert.match(document.querySelector('[role="dialog"]')!.textContent!, /Đăng nguyên bản/);
    assert.match(document.querySelector('[role="dialog"]')!.textContent!, /Viết lại nội dung/);
    await React.act(async () => button("Xác nhận lên lịch 2 bài").click());
    assert.deepEqual(requests.map(r => r.rewriteContent), [false, true]);
    assert.ok(requests.every(r => r.dueAtUtc && r.text === "Nội dung gốc"));
  } finally { await React.act(async () => root.unmount()); container.remove(); }
});
