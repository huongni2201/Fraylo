import "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
import type { JobSummaryDto } from "./jobTypes";
const { mergeJobs } = await import("./jobProgressModel");

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
