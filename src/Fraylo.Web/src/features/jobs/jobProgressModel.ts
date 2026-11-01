import type { VideoContentItemDto } from "../projects/videoWorkflowApi";
import type { JobSummaryDto } from "./jobTypes";

export const OUTPUT_PREVIEW_LENGTH = 160;

/** Kịch bản và prompt video của một cảnh, dùng làm nội dung cố định cho mỗi dòng video. */
export interface VideoJobText {
  script: string;
  prompt: string;
  tiktok: string;
  imageAssetId: string | null;
}

/** Gộp các danh sách theo id; danh sách đến sau ghi đè bản cũ, kết quả mới nhất lên đầu. */
export function mergeJobs(...lists: JobSummaryDto[][]): JobSummaryDto[] {
  const byId = new Map<string, JobSummaryDto>();
  for (const list of lists) {
    for (const job of list) byId.set(job.id, job);
  }
  return [...byId.values()].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc));
}

/** Rút gọn output một dòng để hiển thị trong bảng; bản đầy đủ nằm trong hộp chi tiết. */
export function outputPreview(resultText: string | null | undefined): string {
  const flat = (resultText ?? "").replace(/\s+/g, " ").trim();
  return flat.length > OUTPUT_PREVIEW_LENGTH ? `${flat.slice(0, OUTPUT_PREVIEW_LENGTH)}…` : flat;
}

/** Lập chỉ mục kịch bản và prompt theo sceneId để tra cứu khi vẽ từng dòng video. */
export function indexVideoText(items: VideoContentItemDto[]): Record<string, VideoJobText> {
  const index: Record<string, VideoJobText> = {};
  for (const item of items) {
    if (item.sceneId) {
      index[item.sceneId] = {
        script: item.contentText ?? "",
        prompt: item.videoPrompt,
        tiktok: item.tiktokContent ?? "",
        imageAssetId: item.imageAssetId ?? null,
      };
    }
  }
  return index;
}

/** Dòng bảng: mọi job render video, cùng job Kịch bản/Ảnh/Prompt chưa thành công (job thành công đã nằm trong dòng video). */
export function isPipelineRow(job: JobSummaryDto): boolean {
  if (job.taskType === "VideoRender") return true;
  return ["Script", "Image", "VideoPrompt"].includes(job.taskType) && job.state !== "Succeeded";
}

/** Kịch bản và prompt hiển thị cho dòng: theo cảnh nếu có, còn job Kịch bản không gắn cảnh thì lấy output của chính nó. */
export function jobRowText(job: JobSummaryDto, videoText: Record<string, VideoJobText>): VideoJobText {
  if (job.sceneId) return videoText[job.sceneId] ?? { script: "", prompt: "", tiktok: "", imageAssetId: null };
  return { script: job.taskType === "Script" ? (job.resultText ?? "") : "", prompt: "", tiktok: "", imageAssetId: null };
}

/** Các dự án có job gắn cảnh, để nạp kịch bản và prompt tương ứng. */
export function videoProjectIds(jobs: JobSummaryDto[]): string[] {
  return [...new Set(jobs.filter((job) => job.sceneId).map((job) => job.projectId))];
}
