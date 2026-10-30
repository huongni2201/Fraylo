import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { PublishingApi, PublishRequest, PublishAttempt } from "./publishingApi";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { PublishingPage } = await import("./PublishingPage");
const { PublishingNavItem } = await import("./PublishingNavItem");
const { blocksAnotherPublish } = await import("./publishingApi");

test("Form đăng video: chọn đầu ra, mặc định private, không double-submit và đối soát uncertain", async () => {
  const requests: PublishRequest[] = [];
  let attempts: PublishAttempt[] = [];
  let accounts = [{ accountId: "account", accountIdentity: "@creator", profileId: "profile", sessionStatus: "Connected" }];
  let openSettings = 0;
  let releasePublish: ((attempt: PublishAttempt) => void) | undefined;
  let reconcileCalls = 0;
  const api: PublishingApi = {
    list: async () => ({ videos: [{ projectId: "project", projectName: "Video đầu tiên", assetId: "video", fileName: "output.mp4", byteSize: 1024, caption: "Caption tiếng Việt #test" }], attempts }),
    listAccounts: async () => accounts,
    publish: async request => { requests.push(request); return await new Promise(resolve => { releasePublish = resolve; }); },
    reconcile: async request => { reconcileCalls++; const result = { ...attempts[0], state: "UserConfirmedNotPublished" as const, message: "Đã kiểm tra" }; attempts = [result]; return result; },
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
  assert.equal(container.querySelector<HTMLSelectElement>("#publish-privacy")!.value, "private");
  await select("#publish-video", "video");
  await select("#publish-account", "account");
  assert.equal(container.querySelector<HTMLTextAreaElement>("#publish-caption")!.value, "Caption tiếng Việt #test");
  assert.equal(button("Đăng lên TikTok").disabled, false);
  await React.act(async () => { button("Đăng lên TikTok").click(); button("Đăng lên TikTok").click(); });
  assert.equal(requests.length, 1);
  assert.equal(requests[0].privacy, "private");
  const result: PublishAttempt = { ...requests[0], attemptId: requests[0].clientRequestId, accountIdentity: "@creator", state: "Uncertain", message: "Cần kiểm tra TikTok", externalPostUrl: null, createdAtUtc: new Date().toISOString(), updatedAtUtc: new Date().toISOString() };
  attempts = [result];
  await React.act(async () => { releasePublish!(result); await new Promise(resolve => setTimeout(resolve, 10)); });
  assert.match(container.textContent!, /Cần đối soát/);
  assert.equal(button("Đăng lên TikTok").disabled, true);
  assert.equal(button("Xác nhận chưa đăng").disabled, true);
  assert.equal(button("Xác nhận đã đăng").disabled, true);
  await React.act(async () => { container.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click(); });
  await React.act(async () => { button("Xác nhận chưa đăng").click(); });
  assert.equal(reconcileCalls, 1);
  assert.equal(button("Đăng lên TikTok").disabled, false);

  accounts = [];
  await React.act(async () => { button("Tải lại").click(); });
  assert.equal(button("Đăng lên TikTok").disabled, true);
  await React.act(async () => { button("Cài đặt tài khoản").click(); });
  assert.equal(openSettings, 1);
  await React.act(async () => root.unmount()); container.remove();
});

test("Sidebar item và trạng thái khóa đăng lại", async () => {
  assert.equal(blocksAnotherPublish("Uncertain"), true);
  assert.equal(blocksAnotherPublish("Published"), true);
  assert.equal(blocksAnotherPublish("Submitted"), true);
  assert.equal(blocksAnotherPublish("Failed"), false);
  assert.equal(blocksAnotherPublish("UserConfirmedNotPublished"), false);
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
    { projectId: "a", projectName: "A", assetId: "video-a", fileName: "a.mp4", byteSize: 1024, caption: "Caption A" },
    { projectId: "b", projectName: "B", assetId: "video-b", fileName: "b.mp4", byteSize: 1024, caption: "Caption B" },
  ];
  const api: PublishingApi = {
    list: async () => { calls++; return { videos, attempts: [] }; }, listAccounts: async () => [],
    publish: async () => { throw new Error("Không được tự đăng"); }, reconcile: async () => { throw new Error("Không đối soát tự động"); },
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
