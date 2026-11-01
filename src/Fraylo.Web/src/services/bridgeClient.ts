import {
  bridgeContractVersion,
  g1CommandAllowlist,
  maxBridgeEnvelopeBytes,
  ErrorCodes,
  type ErrorDto,
  type EventEnvelope,
  type G1Command,
  type RequestEnvelope,
  type ResponseEnvelope,
} from "../contracts/bridge";

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage: (message: unknown) => void;
        addEventListener: (
          type: "message",
          listener: (event: MessageEvent) => void,
        ) => void;
        removeEventListener: (
          type: "message",
          listener: (event: MessageEvent) => void,
        ) => void;
      };
    };
  }
}

export type InvokeOptions = {
  timeoutMs?: number;
  signal?: AbortSignal;
};

type PendingRequest = {
  resolve: (data: unknown) => void;
  reject: (error: ErrorDto) => void;
  timer?: ReturnType<typeof setTimeout>;
  command: G1Command;
  correlationId: string;
  cleanup: () => void;
};

class BridgeClient {
  private pendingRequests = new Map<string, PendingRequest>();
  private eventListeners = new Map<
    string,
    Set<(event: EventEnvelope) => void>
  >();
  private lastSeenSeq = new Map<string, number>();
  private isInitialized = false;

  constructor() {
    this.initMessageListener();
  }

  private initMessageListener(): void {
    if (this.isInitialized) return;
    if (typeof window !== "undefined" && window.chrome?.webview) {
      window.chrome.webview.addEventListener(
        "message",
        this.handleHostMessage.bind(this),
      );
      this.isInitialized = true;
    }
  }

  private handleHostMessage(event: MessageEvent): void {
    try {
      const rawData =
        typeof event.data === "string" ? JSON.parse(event.data) : event.data;

      if (!rawData || typeof rawData !== "object") return;

      if (rawData.type === "response") {
        this.handleResponseEnvelope(rawData as ResponseEnvelope);
      } else if (rawData.type === "event") {
        this.handleEventEnvelope(rawData as EventEnvelope);
      }
    } catch (err) {
      console.error("[Fraylo Bridge] Lỗi phân tích thông điệp từ Host:", err);
    }
  }

  private handleResponseEnvelope(envelope: ResponseEnvelope): void {
    const pending = this.pendingRequests.get(envelope.id);
    if (!pending) return;

    pending.cleanup();

    // Xác thực phiên bản hợp đồng
    if (envelope.contractVersion !== bridgeContractVersion) {
      pending.reject({
        code: ErrorCodes.VERSION_MISMATCH,
        message: `Phiên bản hợp đồng không khớp: mong đợi ${bridgeContractVersion}, nhận được ${envelope.contractVersion}`,
        retryable: false,
        correlationId: envelope.id,
        details: { receivedVersion: envelope.contractVersion },
      });
      return;
    }

    if (envelope.success) {
      pending.resolve(envelope.data);
    } else {
      pending.reject(
        envelope.error ?? {
          code: ErrorCodes.INTERNAL_ERROR,
          message: "Phản hồi thất bại không kèm chi tiết lỗi từ Host.",
          retryable: false,
          correlationId: envelope.id,
          details: null,
        },
      );
    }
  }

  private handleEventEnvelope(envelope: EventEnvelope): void {
    if (envelope.contractVersion !== bridgeContractVersion) {
      console.warn(
        `[Fraylo Bridge] Bỏ qua event sai contractVersion: ${envelope.contractVersion}`,
      );
      return;
    }

    // Kiểm tra seq đơn điệu tránh stale events theo từng luồng event.
    const streamKey = envelope.eventName;
    const lastSeq = this.lastSeenSeq.get(streamKey) ?? -1;
    if (envelope.seq <= lastSeq) {
      console.warn(
        `[Fraylo Bridge] Bỏ qua stale event ${envelope.eventName} trên luồng ${streamKey} (seq ${envelope.seq} <= ${lastSeq})`,
      );
      return;
    }
    this.lastSeenSeq.set(streamKey, envelope.seq);

    const listeners = this.eventListeners.get(envelope.eventName);
    if (listeners) {
      listeners.forEach((listener) => {
        try {
          listener(envelope);
        } catch (e) {
          console.error(
            `[Fraylo Bridge] Lỗi trong listener của event ${envelope.eventName}:`,
            e,
          );
        }
      });
    }
  }

  /**
   * Gọi lệnh G1 qua Bridge WebView2
   */
  public invoke<TData = unknown, TPayload = unknown>(
    command: G1Command,
    payload: TPayload,
    options?: number | InvokeOptions,
  ): Promise<TData> {
    return new Promise<TData>((resolve, reject) => {
      const timeoutMs =
        typeof options === "number"
          ? options
          : options?.timeoutMs ?? 15000;
      const signal =
        typeof options === "object" && options !== null
          ? options.signal
          : undefined;

      // 1. Kiểm tra Allowlist
      if (!g1CommandAllowlist.includes(command)) {
        reject({
          code: ErrorCodes.UNKNOWN_COMMAND,
          message: `Lệnh '${command}' không nằm trong allow-list của Gate G1.`,
          retryable: false,
          correlationId: "",
          details: { command },
        });
        return;
      }

      // 2. Kiểm tra signal đã bị hủy từ trước
      if (signal?.aborted) {
        reject({
          code: ErrorCodes.INTERNAL_ERROR,
          message: `Yêu cầu '${command}' đã bị hủy qua AbortSignal trước khi gửi.`,
          retryable: false,
          correlationId: "",
          details: { command, reason: signal.reason ? String(signal.reason) : "aborted" },
        });
        return;
      }

      // 3. Kiểm tra kích thước payload
      let serializedPayload: string;
      try {
        serializedPayload = JSON.stringify(payload ?? {});
      } catch {
        reject({
          code: ErrorCodes.INVALID_ARGUMENT,
          message: "Payload không thể serialize thành JSON hợp lệ.",
          retryable: false,
          correlationId: "",
          details: null,
        });
        return;
      }

      if (
        new TextEncoder().encode(serializedPayload).length >
        maxBridgeEnvelopeBytes
      ) {
        reject({
          code: ErrorCodes.PAYLOAD_TOO_LARGE,
          message: `Kích thước payload vượt quá giới hạn tối đa 4 MiB.`,
          retryable: false,
          correlationId: "",
          details: null,
        });
        return;
      }

      const id = crypto.randomUUID();
      const requestEnvelope: RequestEnvelope<TPayload> = {
        id,
        type: "request",
        command,
        payload,
        clientTimestampUtc: new Date().toISOString(),
        contractVersion: bridgeContractVersion,
      };

      // 4. Nếu đang trong WebView2 Desktop Host
      if (typeof window !== "undefined" && window.chrome?.webview?.postMessage) {
        let timer: ReturnType<typeof setTimeout> | undefined;
        let onAbort: (() => void) | undefined;

        const cleanup = () => {
          if (timer !== undefined) {
            clearTimeout(timer);
            timer = undefined;
          }
          if (signal && onAbort) {
            signal.removeEventListener("abort", onAbort);
          }
          this.pendingRequests.delete(id);
        };

        timer = setTimeout(() => {
          cleanup();
          reject({
            code: ErrorCodes.REQUEST_TIMEOUT,
            message: `Yêu cầu '${command}' đã hết thời gian chờ phản hồi (${timeoutMs}ms).`,
            retryable: true,
            correlationId: id,
            details: { command, timeoutMs },
          });
        }, timeoutMs);

        if (signal) {
          onAbort = () => {
            cleanup();
            reject({
              code: ErrorCodes.INTERNAL_ERROR,
              message: `Yêu cầu '${command}' đã bị hủy qua AbortSignal.`,
              retryable: false,
              correlationId: id,
              details: { command, reason: signal.reason ? String(signal.reason) : "aborted" },
            });
          };
          signal.addEventListener("abort", onAbort, { once: true });
        }

        this.pendingRequests.set(id, {
          resolve: resolve as (data: unknown) => void,
          reject,
          timer,
          command,
          correlationId: id,
          cleanup,
        });

        window.chrome.webview.postMessage(requestEnvelope);
        return;
      }

      // 5. Khi WebView2 Host không có window.chrome.webview: reject ErrorDto BRIDGE_UNAVAILABLE
      reject({
        code: ErrorCodes.BRIDGE_UNAVAILABLE,
        message: `WebView2 Host bridge chưa khả dụng hoặc chưa kết nối cho lệnh '${command}'.`,
        retryable: false,
        correlationId: id,
        details: { command },
      });
    });
  }

  /**
   * Đăng ký lắng nghe sự kiện từ Host
   */
  public subscribeEvent<TPayload = unknown>(
    eventName: string,
    handler: (event: EventEnvelope<TPayload>) => void,
  ): () => void {
    if (!this.eventListeners.has(eventName)) {
      this.eventListeners.set(eventName, new Set());
    }
    const listeners = this.eventListeners.get(eventName)!;
    const typedHandler = handler as (event: EventEnvelope) => void;
    listeners.add(typedHandler);

    return () => {
      listeners.delete(typedHandler);
    };
  }
}

export const bridgeClient = new BridgeClient();
