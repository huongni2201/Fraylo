import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";

const React = await import("react");
const { createRoot } = await import("react-dom/client");
const { BulkVideoCreateDialog } = await import("./BulkVideoCreateDialog.tsx");
const { projectApi } = await import("./projectApi.ts");
const { videoWorkflowApi } = await import("./videoWorkflowApi.ts");
const { bridgeClient } = await import("../../services/bridgeClient.ts");

test("Tạo hàng loạt kiểm ảnh, dùng lại dự án và giữ tên vượt giới hạn", async (t) => {
  const create = t.mock.method(projectApi, "createProject", async () => "project-a");
  t.mock.method(videoWorkflowApi, "getConfig", async () => ({ workflowMode: "ModelAndProduct" }));
  t.mock.method(videoWorkflowApi, "saveConfig", async () => {});
  let startCount = 0;
  const start = t.mock.method(videoWorkflowApi, "startWorkflow", async () => {
    if (++startCount === 1) throw new Error("Lỗi khởi chạy");
  });
  t.mock.method(bridgeClient, "invoke", async (command: string) => {
    assert.equal(command, "asset.list");
    return { assets: [{ assetId: "asset-a", relativePath: "image.png", byteSize: 100, status: "Linked" }] };
  });
  const container = document.body.appendChild(document.createElement("div"));
  const root = createRoot(container);
  const click = (label: string) => React.act(async () => {
    const button = Array.from(document.querySelectorAll("button")).find(b => b.textContent?.trim() === label);
    assert(button && !button.disabled, label);
    button.click();
  });
  const setValue = (field: HTMLInputElement | HTMLTextAreaElement, value: string) => React.act(async () => {
    const prototype = field instanceof HTMLInputElement ? HTMLInputElement.prototype : HTMLTextAreaElement.prototype;
    Object.getOwnPropertyDescriptor(prototype, "value")!.set!.call(field, value);
    field.dispatchEvent(new Event("input", { bubbles: true }));
  });
  try {
    await React.act(async () => root.render(React.createElement(BulkVideoCreateDialog, { isOpen: true, onClose() {}, onCreated() {} })));
    await setValue(document.querySelector<HTMLInputElement>('[aria-label="Tên video 1"]')!, "Video thử");
    await click("Tạo 1 video");
    assert.equal(create.mock.callCount(), 0);
    await click("Chọn ảnh mẫu chung");
    await click("Xác nhận chọn");
    await click("Thêm ảnh");
    await click("Xác nhận chọn");
    await click("Tạo 1 video");
    assert.match(document.body.textContent!, /Đã lưu dự án.*Lỗi khởi chạy/);
    await click("Tạo 1 video");
    assert.equal(create.mock.callCount(), 1);
    assert.equal(start.mock.callCount(), 2);
    assert.deepEqual(start.mock.calls[0].arguments, start.mock.calls[1].arguments);
    const paste = document.querySelector<HTMLTextAreaElement>('[aria-label="Dán danh sách tên video"]')!;
    await setValue(paste, Array.from({ length: 51 }, (_, i) => `Video ${i + 1}`).join("\n"));
    await click("Thêm");
    assert.equal(document.querySelectorAll('input[aria-label^="Tên video"]').length, 50);
    assert.equal(paste.value, "Video 51");
  } finally {
    await React.act(async () => root.unmount());
    container.remove();
  }
});
