/**
 * Fraylo — Lưu trữ prompt toàn ứng dụng và lựa chọn prompt active theo mục đích.
 */

import type { PromptPurpose } from "./promptApi.ts";
import tikTokDefaults from "../../../../Fraylo.Contracts/Prompts/TikTokPromptDefaults.json" with { type: "json" };

export interface CustomPromptItem {
  id: string;
  name: string;
  purpose: PromptPurpose;
  templateText: string;
  variableDefinitionsJson?: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  backendTemplateId?: string | null;
  isPreset?: boolean;
}

const STORAGE_KEY_PREFIX = "Fraylo_user_prompts_";
const ACTIVE_PROMPT_PREFIX = "Fraylo_active_prompt_";
const DELETED_PROMPTS_PREFIX = "Fraylo_deleted_prompts_";

/**
 * Các prompt mẫu chuẩn ban đầu cho từng mục đích.
 * Đã chuẩn hóa tên biến không dấu ({ten_bien}) theo quy tắc 2.2:
 * - {chu_de} thay cho {chủ_đề}
 * - {san_pham} thay cho {tên_sản_phẩm}
 * - {phong_cach} thay cho {phong_cách}
 * - {hanh_dong} thay cho {hành_động}
 */
export const DEFAULT_PRESET_PROMPTS: Record<PromptPurpose, CustomPromptItem[]> = {
  Script: [
    {
      id: "preset-script-tiktok-standard",
      ...tikTokDefaults.Script,
      purpose: "Script",
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isPreset: true,
    },
    {
      id: "preset-script-ideas-analysis",
      name: "Phân tích dữ liệu & Đề xuất 5 ý tưởng video",
      purpose: "Script",
      templateText:
        "Bạn là chuyên gia chiến lược nội dung video ngắn cho kênh được cung cấp.\n" +
        "Hãy phân tích dữ liệu kênh và đề xuất 5 ý tưởng video có thể triển khai về chủ đề {chu_de}.\n\n" +
        "Sử dụng CHANNEL_CONTEXT, ANALYTICS_CONTEXT và RECENT_IDEAS trong phần dữ liệu. Nêu kỳ phân tích, nguồn, số video và dữ liệu còn thiếu. Phân biệt quan sát có bằng chứng với suy luận sáng tạo.\n\n" +
        "Mỗi ý tưởng gồm tiêu đề, hook, nội dung chính, thời lượng mục tiêu gợi ý, lý do phù hợp, evidenceRefs nếu có, giả thuyết cần kiểm chứng và một bài đăng TikTok gồm caption hoàn chỉnh cùng hashtags liên quan. Trả tiếng Việt và đúng OUTPUT_CONTRACT.",
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isPreset: true,
    },
    {
      id: "preset-script-product-review",
      name: "Kịch bản Review sản phẩm / Bán hàng viral",
      purpose: "Script",
      templateText:
        "Bạn là nhà sáng tạo nội dung review sản phẩm chuyên nghiệp trên TikTok.\n" +
        "Tạo kịch bản review chân thực và cuốn hút cho sản phẩm: {san_pham} với phong cách {phong_cach}.\n\n" +
        "Cấu trúc 3 phần bắt buộc:\n" +
        "1. Hook 3 giây đầu: Nêu vấn đề bức xúc hoặc trải nghiệm bất ngờ gây tò mò.\n" +
        "2. Thân bài (15-40s): Mở hộp, trải nghiệm tính năng thực tế, nêu 2 ưu điểm nổi trội và 1 lưu ý chân thực.\n" +
        "3. Kết bài & CTA (5-10s): Đánh giá tổng quan, gợi ý đối tượng phù hợp và lời kêu gọi hành động xem thêm ở giỏ hàng/bio.",
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isPreset: true,
    },
  ],
  Image: [
    {
      id: "preset-image-photorealistic",
      ...tikTokDefaults.Image,
      purpose: "Image",
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isPreset: true,
    },
    {
      id: "preset-image-3d-render",
      name: "Tạo ảnh Phong cách 3D / Hoạt hình Pixar",
      purpose: "Image",
      templateText:
        "A stylized 3D image based on the provided scene description and reference image, with vibrant colors, volumetric lighting, a clean background, detailed character design, and a charming expression.",
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isPreset: true,
    },
  ],
  ComposeImage: [
    {
      id: "preset-compose-image-model-product",
      ...tikTokDefaults.ComposeImage,
      purpose: "ComposeImage",
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isPreset: true,
    },
  ],
  Storyboard: [
    {
      id: "preset-storyboard-grid9",
      name: "Storyboard nhất quán",
      purpose: "Storyboard",
      templateText:
        "Bạn là đạo diễn hình ảnh cho video ngắn. Dựa trên kịch bản, yêu cầu nội dung, thời lượng và ảnh tham chiếu, xây dựng storyboard theo đúng số cảnh ứng dụng yêu cầu.\n\n" +
        "Mở đầu bằng hình ảnh thu hút, phát triển nội dung mạch lạc và kết thúc bằng điểm nhấn phù hợp. Chia mốc thời gian liên tục, không chồng lấn, bao phủ đúng thời lượng video. Mỗi phân cảnh mô tả rõ bối cảnh, góc máy, cỡ cảnh, hành động, prompt tạo ảnh và chuyển động phù hợp.\n\n" +
        "Giữ cùng nhân vật, trang phục, sản phẩm, màu sắc, ánh sáng và phong cách giữa các phân cảnh. Bảo toàn chi tiết từ ảnh nguồn; không tự thêm tính năng, giá, ưu đãi hoặc công dụng sản phẩm chưa được cung cấp. Mỗi phân cảnh có hành động đơn giản, khả thi khi tạo video.\n\n" +
        "Đọc các ô storyboard từ trái sang phải, trên xuống dưới. Viết mô tả bằng tiếng Việt và prompt tạo ảnh hoặc chuyển động bằng tiếng Anh khi được yêu cầu. Tuân thủ OUTPUT_CONTRACT của ứng dụng.",
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isPreset: true,
    },
  ],
  TikTokContent: [
    {
      id: "preset-tiktok-content-natural",
      ...tikTokDefaults.TikTokContent,
      purpose: "TikTokContent",
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isPreset: true,
    },
  ],
  VideoPrompt: [
    {
      id: "preset-video-cinematic-camera",
      ...tikTokDefaults.VideoPrompt,
      purpose: "VideoPrompt",
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isPreset: true,
    },
    {
      id: "preset-video-dynamic-motion",
      name: "Prompt Video Chuyển động nhanh & Kịch tính",
      purpose: "VideoPrompt",
      templateText:
        "Write concise English render directions for the action and reference provided. Use a brisk, deliberate lateral track or a short push-in when it reveals the product, followed by a steady close-up. Keep the product sharp during motion. Match the reference lighting and preserve skin tone and product color. Use a subtle rim light only when it separates the subject without changing the product appearance. Maintain continuity and follow OUTPUT_CONTRACT.",
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
      isPreset: true,
    },
  ],
};

const STORAGE_SCOPE = "global";

/**
 * Lấy tập hợp các ID prompt/preset đã bị người dùng xóa.
 * Ngăn chặn việc preset tự động tái sinh sau khi người dùng đã bấm xóa.
 */
export function getDeletedPromptIds(): Set<string> {
  if (typeof window === "undefined" || !window.localStorage) {
    return new Set<string>();
  }
  try {
    const raw = window.localStorage.getItem(
      `${DELETED_PROMPTS_PREFIX}${STORAGE_SCOPE}`
    );
    if (!raw) return new Set<string>();
    const parsed = JSON.parse(raw);
    return new Set<string>(Array.isArray(parsed) ? parsed : []);
  } catch {
    return new Set<string>();
  }
}

/**
 * Lấy danh sách custom prompts toàn ứng dụng, bỏ qua các prompt đã xóa.
 */
export function getStoredPrompts(): CustomPromptItem[] {
  if (typeof window === "undefined" || !window.localStorage) {
    return [];
  }
  try {
    const raw = window.localStorage.getItem(`${STORAGE_KEY_PREFIX}${STORAGE_SCOPE}`);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    const list: CustomPromptItem[] = Array.isArray(parsed) ? parsed : [];
    const deleted = getDeletedPromptIds();
    return list.filter((p) => !deleted.has(p.id));
  } catch (err) {
    console.warn("Lỗi khi đọc danh sách prompts từ localStorage:", err);
    return [];
  }
}

/**
 * Lấy ID prompt đang được chọn làm active cho mục đích.
 */
export function getActivePromptId(purpose: PromptPurpose): string | null {
  if (typeof window === "undefined" || !window.localStorage) return null;
  try {
    const id = window.localStorage.getItem(
      `${ACTIVE_PROMPT_PREFIX}${STORAGE_SCOPE}_${purpose}`
    );
    if (!id) return null;
    const deleted = getDeletedPromptIds();
    if (deleted.has(id)) {
      setActivePromptId(purpose, null);
      return null;
    }
    return id;
  } catch {
    return null;
  }
}

/**
 * Lưu lựa chọn prompt active cho lần sau
 */
export function setActivePromptId(
  purpose: PromptPurpose,
  promptId: string | null
): void {
  if (typeof window === "undefined" || !window.localStorage) return;
  try {
    const key = `${ACTIVE_PROMPT_PREFIX}${STORAGE_SCOPE}_${purpose}`;
    if (promptId) {
      window.localStorage.setItem(key, promptId);
    } else {
      window.localStorage.removeItem(key);
    }
  } catch (err) {
    console.warn("Lỗi khi lưu active prompt vào localStorage:", err);
  }
}
