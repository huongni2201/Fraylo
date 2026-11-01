import { domWindow } from "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";

// Payload phải khớp tên trường JsonPropertyName của Contracts C# (VideoWorkflowDtos.cs).
const sent: Array<{ command: string; payload: Record<string, unknown> }> = [];
const wireItem = {
  id: "item_2", projectId: "prj_1", sceneId: "scn_2", sequenceOrder: 2, idea: "Ý", hook: "Hook",
  video_script: "Kịch bản", video_prompt: "Prompt", tiktok_content: "Caption #tag",
  status: "Ready", createdAtUtc: "t", updatedAtUtc: "t",
};

window.chrome = {
  webview: {
    postMessage: (envelope: unknown) => {
      const req = (typeof envelope === "string" ? JSON.parse(envelope) : envelope) as {
        id: string; command: string; payload: Record<string, unknown>; contractVersion: string;
      };
      sent.push({ command: req.command, payload: req.payload });
      const data = req.command === "workflow.list_content_items" ? [wireItem]
        : req.command === "workflow.update_content_item" ? wireItem
        : { enqueued: true, count: 1 };
      setTimeout(() => window.dispatchEvent(new domWindow.MessageEvent("message", {
        data: JSON.stringify({ id: req.id, type: "response", success: true, data, error: null,
          serverTimestampUtc: new Date().toISOString(), contractVersion: req.contractVersion || "1.1.0" }),
      }) as unknown as Event), 1);
    },
    addEventListener: (type: string, listener: EventListener) => window.addEventListener(type, listener),
    removeEventListener: (type: string, listener: EventListener) => window.removeEventListener(type, listener),
  },
} as any;

const { videoWorkflowApi } = await import("./videoWorkflowApi.ts");

test("Bảng nội dung gửi và đọc đúng tên trường của backend", async () => {
  const [item] = await videoWorkflowApi.listContentItems("prj_1");
  assert.equal(item.videoScript, "Kịch bản");
  assert.equal(item.videoPrompt, "Prompt");
  assert.equal(item.tiktokContent, "Caption #tag");

  const updated = await videoWorkflowApi.updateContentItem({
    projectId: "prj_1", itemId: "item_2", videoScript: "Mới", videoPrompt: "P", tiktokContent: "C",
  });
  assert.equal(updated.tiktokContent, "Caption #tag");
  const update = sent.find((s) => s.command === "workflow.update_content_item")!;
  assert.equal(update.payload.id, "item_2");
  assert.equal(update.payload.video_script, "Mới");
  assert.equal(update.payload.video_prompt, "P");
  assert.equal(update.payload.tiktok_content, "C");
  assert.equal("itemId" in update.payload, false);

  await videoWorkflowApi.createSceneVideo({ projectId: "prj_1", itemId: "item_2" });
  const create = sent.find((s) => s.command === "workflow.create_scene_video")!;
  assert.deepEqual(create.payload, { projectId: "prj_1", contentItemId: "item_2" });
});
