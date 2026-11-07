import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { AiProviderSettingsPanel } = await import("./AiProviderSettingsPanel");
const { aiSettingsApi } = await import("./aiSettingsApi");

test("AI viết lại Facebook: riêng provider, dirty/hủy/lưu và giữ nguyên giới hạn đồng thời", async () => {
  const previous = { ...aiSettingsApi };
  const saved = { imageProvider: null, scriptProvider: "gemini-web", analyticsProvider: "gemini-web", videoPromptProvider: null, facebookRewriteProvider: "chatgpt-web", revision: 4, maxOpenBrowsers: 3, maxConcurrentJobs: 5 };
  const requests: unknown[] = [];
  aiSettingsApi.getSettings = async () => ({ ...saved });
  aiSettingsApi.listProviders = async () => ({ accounts: [], tasks: [] });
  aiSettingsApi.saveSettings = async request => { requests.push(request); return { ...saved, facebookRewriteProvider: request.facebookRewriteProvider ?? "", revision: 5 }; };
  const container = document.createElement("div"); document.body.append(container); const root = createRoot(container);
  const button = (label: string) => Array.from(container.querySelectorAll<HTMLButtonElement>("button")).find(b => b.textContent?.trim() === label)!;
  const change = async (value: string) => React.act(async () => {
    const select = container.querySelector<HTMLSelectElement>("#facebook-rewrite-provider")!;
    Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, "value")!.set!.call(select, value); select.dispatchEvent(new Event("change", { bubbles: true }));
  });
  try {
    await React.act(async () => root.render(React.createElement(AiProviderSettingsPanel)));
    assert.equal(container.querySelector<HTMLSelectElement>("#facebook-rewrite-provider")!.value, "chatgpt-web");
    assert.equal(button("Lưu cài đặt").disabled, true);
    await change("gemini-web"); assert.equal(button("Lưu cài đặt").disabled, false);
    await React.act(async () => button("Hủy / Khôi phục").click());
    assert.equal(container.querySelector<HTMLSelectElement>("#facebook-rewrite-provider")!.value, "chatgpt-web");
    await change("gemini-web"); await React.act(async () => button("Lưu cài đặt").click());
    assert.equal(requests.length, 1);
    assert.deepEqual({ ...requests[0] as object, clientRequestId: undefined }, { expectedRevision: 4, scriptProvider: "gemini-web", analyticsProvider: "gemini-web", facebookRewriteProvider: "gemini-web", imageProvider: null, videoPromptProvider: null, maxOpenBrowsers: 3, maxConcurrentJobs: 5, visibleBrowserProviders: [], documentsPath: undefined, imagesPath: undefined, videosPath: undefined, clientRequestId: undefined });
    assert.equal(button("Lưu cài đặt").disabled, true);
  } finally { Object.assign(aiSettingsApi, previous); await React.act(async () => root.unmount()); container.remove(); }
});
