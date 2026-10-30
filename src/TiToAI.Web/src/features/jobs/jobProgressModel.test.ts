import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { VideoContentItemDto } from "../projects/videoWorkflowApi";
import type { JobSummaryDto } from "./jobTypes";
const { mergeJobs, outputPreview, indexVideoText, isPipelineRow, OUTPUT_PREVIEW_LENGTH } = await import("./jobProgressModel");

function job(id: string, state: JobSummaryDto["state"], createdAtUtc: string): JobSummaryDto {
  return {
    id,
    projectId: "prj_01",
    sceneId: null,
    taskType: "Script",
    state,
    reasonCode: null,
    attempts: 1,
    progressPercent: null,
    currentStep: null,
    createdAtUtc,
  };
}

test("mergeJobs: bản ghi đến sau ghi đè cùng id và sắp xếp mới nhất lên đầu", () => {
  const history = [job("job_a", "Succeeded", "2026-10-28T10:00:00Z"), job("job_b", "Failed", "2026-10-28T09:00:00Z")];
  const active = [job("job_a", "Running", "2026-10-28T10:00:00Z"), job("job_c", "Queued", "2026-10-28T11:00:00Z")];

  const merged = mergeJobs(history, active);

  assert.deepEqual(merged.map((j) => j.id), ["job_c", "job_a", "job_b"]);
  assert.equal(merged[1].state, "Running");
});

test("outputPreview: gộp khoảng trắng và cắt bớt khi quá dài", () => {
  assert.equal(outputPreview("  dòng 1\n\n  dòng 2 "), "dòng 1 dòng 2");
  assert.equal(outputPreview(null), "");

  const long = outputPreview("a".repeat(OUTPUT_PREVIEW_LENGTH + 50));
  assert.equal(long.length, OUTPUT_PREVIEW_LENGTH + 1);
  assert.ok(long.endsWith("…"));
});

test("indexVideoText: tra cứu kịch bản và prompt theo sceneId, bỏ mục chưa gắn cảnh", () => {
  const index = indexVideoText([
    { id: "s1", sceneId: "scene_1", contentText: "Mở đầu", videoPrompt: "Cinematic" } as VideoContentItemDto,
    { id: "x", sceneId: null, contentText: "Bỏ qua", videoPrompt: "" } as VideoContentItemDto,
  ]);

  assert.deepEqual(index, { scene_1: { script: "Mở đầu", prompt: "Cinematic" } });
});

test("isPipelineRow: giữ job render và job Kịch bản/Ảnh/Prompt chưa thành công", () => {
  const withType = (taskType: string, state: JobSummaryDto["state"]) => ({ ...job("j", state, "2026-10-28T10:00:00Z"), taskType });

  assert.equal(isPipelineRow(withType("VideoRender", "Succeeded")), true);
  assert.equal(isPipelineRow(withType("Script", "Failed")), true);
  assert.equal(isPipelineRow(withType("VideoPrompt", "Running")), true);
  assert.equal(isPipelineRow(withType("Image", "Succeeded")), false);
});
