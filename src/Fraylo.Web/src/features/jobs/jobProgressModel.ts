import type { JobSummaryDto } from "./jobTypes";

/** Gộp các danh sách theo id; danh sách đến sau ghi đè bản cũ, kết quả mới nhất lên đầu. */
export function mergeJobs(...lists: JobSummaryDto[][]): JobSummaryDto[] {
  const byId = new Map<string, JobSummaryDto>();
  for (const list of lists) {
    for (const job of list) byId.set(job.id, job);
  }
  return [...byId.values()].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc));
}
