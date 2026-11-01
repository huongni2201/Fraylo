import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { PublishingApi, PublishRequest, PublishAttempt } from "./publishingApi";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { PublishingPage } = await import("./PublishingPage");
const { PublishingNavItem } = await import("./PublishingNavItem");
const { blocksAnotherPublish } = await import("./publishingApi");

test("Form đăng video: chọn đầu ra, không double-submit và lỗi cho phép đăng lại thủ công", async () => {
  const requests: PublishRequest[] = [];
  let attempts: PublishAttempt[] = [];
  let accounts = [{ accountId: "account", accountIdentity: "@creator", profileId: "profile", sessionStatus: "Connected" }];
  let openSettings = 0;
  let releasePublish: ((attempt: PublishAttempt) => void) | undefined;
  const api: PublishingApi = {
    list: async () => ({ videos: [{ projectId: "project", projectName: "Video đầu tiên", assetId: "video", fileName: "output.mp4", byteSize: 1024, caption: "Caption tiếng Việt #test", createdAtUtc: "2026-11-01T00:00:00Z" }], attempts }),
    listAccounts: async () => accounts,
    publish: async request => { requests.push(request); return await new Promise(resolve => { releasePublish = resolve; }); },
  };
  const container = document.createElement("div"); document.body.append(container);
  const root = createRoot(container);
  await React.act(async () => { root.render(React.createElement(PublishingPage, { active: true, api, onOpenAccounts: () => openSettings++, onReviewVideo: () => {} })); });
  const select = async (id: string, value: string) => {
    await React.act(async () => {
      const element = container.querySelector<HTMLSelectElement>(id)!;
      element.value = value;
      element.dispatchEvent(new Event("change", { bubbles: true }));
    });
  };
  const button = (text: string) => Array.from(container.querySelectorAll("button")).find(b => b.textContent?.includes(text))!;
  assert.equal(button("Đăng lên TikTok").disabled, true);
  await select("#publish-video", "video");
  await select("#publish-account", "account");
  assert.equal(container.querySelector<HTMLTextAreaElement>("#publish-caption")!.value, "Caption tiếng Việt #test");
  assert.equal(button("Đăng lên TikTok").disabled, false);
  await React.act(async () => { button("Đăng lên TikTok").click(); button("Đăng lên TikTok").click(); });
  assert.equal(requests.length, 1);
  assert.equal("soundName" in requests[0], false);
  const result: PublishAttempt = { ...requests[0], attemptId: requests[0].clientRequestId, accountIdentity: "@creator", state: "Failed", message: "Connection lost", externalPostUrl: null, createdAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString() };
  attempts = [result];
  await React.act(async () => { releasePublish!(result); await new Promise(resolve => setTimeout(resolve, 10)); });
  assert.match(container.textContent!, /Thất bại/);
  assert.match(container.textContent!, /Connection lost/);
  assert.doesNotMatch(container.textContent!, /đối soát|Xác nhận chưa đăng|Xác nhận đã đăng/i);
  assert.equal(button("Đăng lên TikTok").disabled, false);
  await React.act(async () => {
    const sound = container.querySelector<HTMLInputElement>("#publish-sound")!;
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!.call(sound, "  Trend Beat ");
    sound.dispatchEvent(new Event("input", { bubbles: true }));
  });
  await React.act(async () => { button("Đăng lên TikTok").click(); });
  assert.equal(requests.length, 2);
  assert.equal(requests[1].soundName, "Trend Beat");
  assert.notEqual(requests[0].clientRequestId, requests[1].clientRequestId);
  const second = { ...result, attemptId: requests[1].clientRequestId };
  attempts = [second];
  await React.act(async () => { releasePublish!(second); });
  accounts = [];
  await React.act(async () => { button("Tải lại").click(); });
  assert.equal(button("Đăng lên TikTok").disabled, true);
  await React.act(async () => { button("Cài đặt tài khoản").click(); });
  assert.equal(openSettings, 1);
  await React.act(async () => root.unmount()); container.remove();
});

test("Sidebar item và trạng thái khóa đăng lại", async () => {
  assert.equal(blocksAnotherPublish("Published"), true);
  assert.equal(blocksAnotherPublish("Submitted"), true);
  assert.equal(blocksAnotherPublish("Failed"), false);
  const container = document.createElement("div"); const root = createRoot(container); let clicks = 0;
  await React.act(async () => root.render(React.createElement(PublishingNavItem, { active: true, onClick: () => clicks++ })));
  assert.equal(container.querySelector("button")!.getAttribute("aria-current"), "page");
  assert.match(container.textContent!, /Đăng video/);
  await React.act(async () => container.querySelector("button")!.click());
  assert.equal(clicks, 1);
  await React.act(async () => root.unmount());
});

test("Trang ẩn không tải dữ liệu; mở lại từ Project chọn đúng output mới và giữ draft khi chỉ đổi route", async () => {
  let calls = 0;
  let videos = [
    { projectId: "a", projectName: "A", assetId: "video-a", fileName: "a.mp4", byteSize: 1024, caption: "Caption A", createdAtUtc: "2026-11-01T00:00:00Z" },
    { projectId: "b", projectName: "B", assetId: "video-b", fileName: "b.mp4", byteSize: 1024, caption: "Caption B", createdAtUtc: "2026-11-01T00:00:00Z" },
  ];
  const api: PublishingApi = {
    list: async () => { calls++; return { videos, attempts: [] }; }, listAccounts: async () => [],
    publish: async () => { throw new Error("Không được tự đăng"); },
  };
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  const render = (active: boolean, selectionRevision: number) => React.createElement(PublishingPage,
    { active, selectionRevision, initialProjectId: "a", api, onOpenAccounts: () => {}, onReviewVideo: async () => { throw new Error("Dự án đã xóa"); } });
  await React.act(async () => root.render(render(false, 1)));
  assert.equal(calls, 0);
  await React.act(async () => root.render(render(true, 1)));
  assert.equal(container.querySelector<HTMLSelectElement>("#publish-video")!.value, "video-a");
  await React.act(async () => {
    const select = container.querySelector<HTMLSelectElement>("#publish-video")!;
    select.value = "video-b"; select.dispatchEvent(new Event("change", { bubbles: true }));
  });
  await React.act(async () => root.render(render(false, 1)));
  await React.act(async () => root.render(render(true, 1)));
  assert.equal(container.querySelector<HTMLSelectElement>("#publish-video")!.value, "video-b");
  assert.equal(container.querySelector<HTMLTextAreaElement>("#publish-caption")!.value, "Caption B");
  await React.act(async () => root.render(render(false, 1)));
  videos = [{ ...videos[0], assetId: "video-a-2", fileName: "a-2.mp4", caption: "Caption A mới" }, videos[1]];
  await React.act(async () => root.render(render(true, 2)));
  assert.equal(container.querySelector<HTMLSelectElement>("#publish-video")!.value, "video-a-2");
  assert.equal(container.querySelector<HTMLTextAreaElement>("#publish-caption")!.value, "Caption A mới");
  await React.act(async () => { Array.from(container.querySelectorAll("button")).find(b => b.textContent?.includes("Xem lại video"))!.click(); });
  assert.match(container.textContent!, /Dự án đã xóa/);
  await React.act(async () => root.unmount()); container.remove();
});

test("Tab danh sách video: mới nhất trước, phân trang 10 dòng, đăng đúng dòng với caption đã sửa", async () => {
  const requests: PublishRequest[] = [];
  let release: ((attempt: PublishAttempt) => void) | undefined;
  let stored: PublishAttempt[] = [];
  const videos = Array.from({ length: 25 }, (_, i) => ({
    projectId: `p${i}`, projectName: `Dự án ${i}`, assetId: `v${i}`, fileName: `v${i}.mp4`, byteSize: 1048576,
    caption: `Caption ${i}`, createdAtUtc: new Date(Date.UTC(2026, 10, 1, 0, i)).toISOString(),
  }));
  const accounts = [
    { accountId: "ok", accountIdentity: "@ok", profileId: "p-ok", sessionStatus: "Connected" },
    { accountId: "login", accountIdentity: "@login", profileId: "p-login", sessionStatus: "NeedsLogin" },
  ];
  const api: PublishingApi = {
    list: async () => ({ videos, attempts: stored }), listAccounts: async () => accounts,
    publish: async request => { requests.push(request); return await new Promise(resolve => { release = resolve; }); },
  };
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  await React.act(async () => root.render(React.createElement(PublishingPage, { active: true, api, onOpenAccounts: () => {}, onReviewVideo: () => {} })));
  const rows = () => Array.from(container.querySelectorAll("tbody tr"));
  const files = () => rows().map(r => r.querySelector("p.text-xs")!.textContent!.split(" · ")[0]);
  const nav = (text: string) => Array.from(container.querySelectorAll("nav button")).find(b => b.textContent?.includes(text)) as HTMLButtonElement;
  const act = (fn: () => void) => React.act(async () => fn());
  assert.equal(rows().length, 10);
  assert.deepEqual(files(), Array.from({ length: 10 }, (_, i) => `v${24 - i}.mp4`));
  assert.match(container.textContent!, /Trang 1\/3/);
  assert.equal(nav("Trước").disabled, true);
  const row = rows()[0];
  const rowButton = () => rows()[0].querySelector("button")!;
  assert.equal(rowButton().disabled, true);
  await act(() => {
    const textarea = row.querySelector("textarea")!;
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")!.set!.call(textarea, "Caption đã sửa");
    textarea.dispatchEvent(new Event("input", { bubbles: true }));
  });
  const pick = (value: string) => act(() => {
    const select = rows()[0].querySelector("select")!; select.value = value; select.dispatchEvent(new Event("change", { bubbles: true }));
  });
  await pick("login");
  assert.equal(rowButton().disabled, true);
  await pick("ok");
  assert.equal(rowButton().disabled, false);
  await act(() => nav("Sau").click());
  assert.match(container.textContent!, /Trang 2\/3/);
  assert.equal(files()[0], "v14.mp4");
  await act(() => nav("Sau").click());
  assert.equal(rows().length, 5);
  assert.equal(nav("Sau").disabled, true);
  await act(() => nav("Trước").click()); await act(() => nav("Trước").click());
  assert.equal(rows()[0].querySelector("textarea")!.value, "Caption đã sửa");
  await act(() => { rowButton().click(); rowButton().click(); });
  assert.equal(requests.length, 1);
  assert.deepEqual(requests[0], { clientRequestId: requests[0].clientRequestId, projectId: "p24", assetId: "v24", accountId: "ok", caption: "Caption đã sửa" });
  assert.equal(rows()[1].querySelector("button")!.disabled, true);
  const single = Array.from(container.querySelectorAll("button")).find(b => b.textContent?.includes("Đăng lên TikTok"))!;
  assert.equal(single.disabled, true);
  const done: PublishAttempt = { ...requests[0], attemptId: requests[0].clientRequestId, accountIdentity: "@ok", state: "Published", message: "OK", externalPostUrl: null, createdAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString() };
  stored = [done];
  await React.act(async () => { release!(done); await new Promise(resolve => setTimeout(resolve, 10)); });
  assert.equal(rows()[0].querySelector("button")!.disabled, true);
  assert.match(rows()[0].textContent!, /Đã đăng/);
  await React.act(async () => root.unmount()); container.remove();
});
