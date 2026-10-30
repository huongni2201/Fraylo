/**
 * TiToAI — API adapter cho prompt template toàn ứng dụng.
 */

import { bridgeClient } from "../../services/bridgeClient.ts";
import type { G1Command } from "../../contracts/bridge.ts";

/**
 * Năm mục đích prompt hợp lệ, khớp backend (PromptTemplateService.ValidPurposes)
 */
export type PromptPurpose = "Script" | "ComposeImage" | "VideoPrompt" | "Storyboard" | "Image";

export interface PromptVariableDefinition {
  name: string;
  description?: string | null;
  isRequired: boolean;
  defaultValue?: string | null;
  example?: string | null;
}

export interface PromptTemplateDto {
  templateId: string;
  purpose: PromptPurpose | string;
  name: string;
  isActive: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface PromptContentDto {
  templateId: string;
  templateText: string;
  variableDefinitionsJson: string;
  updatedAtUtc: string;
}

export interface PromptListTemplatesResponseData {
  templates: PromptTemplateDto[];
}

export interface PromptGetTemplateRequest {
  templateId?: string | null;
  purpose?: string | null;
}

export interface PromptGetTemplateResponseData {
  template: PromptTemplateDto;
  content: PromptContentDto;
}

export interface PromptSaveTemplateRequest {
  purpose: string;
  templateText: string;
  name: string;
  templateId: string | null;
}

export interface PromptSaveTemplateResponseData {
  template: PromptTemplateDto;
  content: PromptContentDto;
}

export interface PromptPreviewRequest {
  templateId?: string | null;
  purpose?: string | null;
  templateText?: string | null;
  variables?: Record<string, string> | null;
}

export interface PromptPreviewResponseData {
  renderedText: string;
  templateId: string | null;
  variableCount: number;
}

/**
 * Interface cho cơ chế vận chuyển lệnh bridge.
 * Cho phép các component UI hoặc test suites truyền transport giả lập (test fixture)
 * hoặc sử dụng bridgeClient mặc định.
 */
export interface IPromptTransport {
  invoke<TData = unknown, TPayload = unknown>(
    command: string,
    payload: TPayload,
    options?: { signal?: AbortSignal; timeoutMs?: number }
  ): Promise<TData>;
}

/**
 * Transport mặc định sử dụng bridgeClient qua WebView2.
 */
export const defaultPromptTransport: IPromptTransport = {
  invoke: async <TData = unknown, TPayload = unknown>(
    command: string,
    payload: TPayload,
    options?: { signal?: AbortSignal; timeoutMs?: number }
  ): Promise<TData> => {
    return await bridgeClient.invoke<TData, TPayload>(
      command as G1Command,
      payload,
      options
    );
  },
};

/**
 * Trích xuất danh sách tên biến từ nội dung template theo cú pháp {tên_biến}.
 * Bỏ qua cú pháp thoát {{ và trả về danh sách tên biến không trùng lặp theo thứ tự xuất hiện.
 */
export function extractTemplateVariables(templateText: string | null | undefined): string[] {
  if (!templateText) return [];

  const detected: string[] = [];
  const seen = new Set<string>();
  const len = templateText.length;
  let i = 0;

  while (i < len) {
    const c = templateText[i];
    if (c === "{") {
      // Bỏ qua escape {{
      if (i + 1 < len && templateText[i + 1] === "{") {
        i += 2;
        continue;
      }

      const closeIndex = templateText.indexOf("}", i + 1);
      const nextOpenIndex = templateText.indexOf("{", i + 1);

      // Kiểm tra lồng ngoặc không hợp lệ
      if (nextOpenIndex !== -1 && (closeIndex === -1 || nextOpenIndex < closeIndex)) {
        i = nextOpenIndex;
        continue;
      }

      if (closeIndex === -1) {
        break;
      }

      const varName = templateText.substring(i + 1, closeIndex).trim();
      // Cho phép chữ Unicode để hỗ trợ cả tên biến chuẩn và tên biến cũ có dấu
      if (/^[\p{L}\p{M}\p{N}_]+$/u.test(varName)) {
        if (!seen.has(varName)) {
          seen.add(varName);
          detected.push(varName);
        }
      }

      i = closeIndex + 1;
    } else {
      i++;
    }
  }

  return detected;
}

export interface ClientPromptRenderResult {
  renderedText: string;
  missingVariables: string[];
  variableCount: number;
  detectedVariables: string[];
}

/**
 * Frontend Prompt Rendering — Thực hiện ghép tất định biến số vào template tại client.
 * Thay thế các {ten_bien} bằng giá trị người dùng nhập, giữ nguyên {{ và }} thoát.
 */
export function renderPromptTemplateClient(
  templateText: string,
  variables: Record<string, string>
): ClientPromptRenderResult {
  const detected = extractTemplateVariables(templateText);
  const missing: string[] = [];

  for (const v of detected) {
    const val = variables[v];
    if (val === undefined || val === null || String(val).trim() === "") {
      missing.push(v);
    }
  }

  // Tạm thay thế {{ và }} bằng token bảo vệ
  const ESC_OPEN = "___TITO_ESC_LBRACE___";
  const ESC_CLOSE = "___TITO_ESC_RBRACE___";

  let working = templateText
    .replace(/\{\{/g, ESC_OPEN)
    .replace(/\}\}/g, ESC_CLOSE);

  // Thay thế từng placeholder {ten_bien}
  working = working.replace(/\{([^{}]+)\}/g, (match, rawName) => {
    const trimmed = rawName.trim();
    if (variables[trimmed] !== undefined && variables[trimmed] !== null) {
      return String(variables[trimmed]);
    }
    // Nếu chưa nhập giá trị, giữ nguyên match hoặc trả về chuỗi rỗng
    return match;
  });

  // Khôi phục escape {{ -> {, }} -> }
  working = working
    .replace(new RegExp(ESC_OPEN, "g"), "{")
    .replace(new RegExp(ESC_CLOSE, "g"), "}");

  return {
    renderedText: working,
    missingVariables: missing,
    variableCount: detected.length,
    detectedVariables: detected,
  };
}

/**
 * Adapter API cho bốn lệnh prompt hiện hành.
 */
export const promptApi = {
  /**
   * Lấy danh sách template toàn ứng dụng
   */
  async listTemplates(
    transport: IPromptTransport = defaultPromptTransport,
    signal?: AbortSignal
  ): Promise<PromptTemplateDto[]> {
    const data = await transport.invoke<PromptListTemplatesResponseData, Record<string, never>>(
      "prompt.list_templates",
      {},
      { signal }
    );
    return data?.templates ?? [];
  },

  /**
   * Lấy template hiện hành và nội dung của nó.
   */
  async getTemplate(
    paramsOrTemplateId?: PromptGetTemplateRequest | string | null,
    purposeOrTransport?: string | IPromptTransport | null,
    transportOrSignal?: IPromptTransport | AbortSignal,
    maybeSignal?: AbortSignal
  ): Promise<PromptGetTemplateResponseData> {
    let params: PromptGetTemplateRequest;
    let transport: IPromptTransport = defaultPromptTransport;
    let signal: AbortSignal | undefined;

    if (paramsOrTemplateId && typeof paramsOrTemplateId === "object" && !("invoke" in paramsOrTemplateId)) {
      params = paramsOrTemplateId as PromptGetTemplateRequest;
      if (purposeOrTransport && typeof purposeOrTransport === "object" && "invoke" in purposeOrTransport) {
        transport = purposeOrTransport as IPromptTransport;
      }
      if (transportOrSignal && "invoke" in transportOrSignal) {
        transport = transportOrSignal;
      } else if (transportOrSignal instanceof AbortSignal) {
        signal = transportOrSignal;
      }
    } else {
      params = {
        templateId: typeof paramsOrTemplateId === "string" ? paramsOrTemplateId : null,
        purpose: typeof purposeOrTransport === "string" ? purposeOrTransport : null,
      };
      if (transportOrSignal && typeof transportOrSignal === "object" && "invoke" in transportOrSignal) {
        transport = transportOrSignal;
      }
      if (maybeSignal && maybeSignal instanceof AbortSignal) {
        signal = maybeSignal;
      }
    }

    return await transport.invoke<PromptGetTemplateResponseData, PromptGetTemplateRequest>(
      "prompt.get_template",
      params,
      { signal }
    );
  },

  /**
   * Lưu nội dung hiện hành của template.
   */
  async saveTemplate(
    request: PromptSaveTemplateRequest,
    transport: IPromptTransport = defaultPromptTransport,
    signal?: AbortSignal
  ): Promise<PromptSaveTemplateResponseData> {
    return await transport.invoke<PromptSaveTemplateResponseData, PromptSaveTemplateRequest>(
      "prompt.save_template",
      request,
      { signal }
    );
  },

  async activateTemplate(
    templateId: string,
    transport: IPromptTransport = defaultPromptTransport
  ): Promise<PromptTemplateDto> {
    return await transport.invoke<PromptTemplateDto, { templateId: string }>(
      "prompt.activate_template",
      { templateId }
    );
  },

  /**
   * Render xem trước nội dung prompt (tính toán biến số qua backend T10)
   * Hỗ trợ render cả bản nháp (templateText) chưa lưu
   */
  async preview(
    request: PromptPreviewRequest,
    transport: IPromptTransport = defaultPromptTransport,
    signal?: AbortSignal
  ): Promise<PromptPreviewResponseData> {
    return await transport.invoke<PromptPreviewResponseData, PromptPreviewRequest>(
      "prompt.preview",
      request,
      { signal }
    );
  },

};
