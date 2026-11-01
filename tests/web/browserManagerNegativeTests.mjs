/**
 * Fraylo — Negative Integration Tests for Browser Manager API
 *
 * Xác minh:
 * 1. Khi Host trả về lỗi UNKNOWN_COMMAND: listProfiles() và checkSession() PHẢI reject với UNKNOWN_COMMAND.
 *    Tuyệt đối KHÔNG trả về 3 profiles giả hoặc Connected giả.
 * 2. Khi Host timeout: promise bị reject với REQUEST_TIMEOUT, tuyệt đối không rơi vào fallback mock.
 * 3. Các API requestLogin() và cancelLogin() cũng propagate lỗi thật từ Host.
 * 4. Production API không còn phương thức mô phỏng simulateCloseLogin.
 */

import assert from "node:assert/strict";
import { register } from "node:module";
import { mock } from "node:test";

// 1. Cấu hình module loader hook cho Node ESM để phân giải relative import TypeScript (.ts, .tsx)
const loaderCode = `
export async function resolve(specifier, context, nextResolve) {
  try {
    return await nextResolve(specifier, context);
  } catch (err) {
    if (specifier.startsWith('.')) {
      for (const ext of ['.ts', '.tsx', '/index.ts']) {
        try {
          return await nextResolve(specifier + ext, context);
        } catch {}
      }
    }
    throw err;
  }
}
`;
register("data:text/javascript," + encodeURIComponent(loaderCode), import.meta.url);

// 2. Khởi tạo Mock WebView2 Host Bridge trước khi import browserManagerApi
const messageListeners = new Set();
let currentHostResponder = null;

globalThis.window = {
  chrome: {
    webview: {
      addEventListener(type, listener) {
        if (type === "message") {
          messageListeners.add(listener);
        }
      },
      removeEventListener(type, listener) {
        if (type === "message") {
          messageListeners.delete(listener);
        }
      },
      postMessage(envelope) {
        if (typeof currentHostResponder === "function") {
          currentHostResponder(envelope);
        }
      },
    },
  },
};

function dispatchHostMessage(messageData) {
  const event = { data: messageData };
  for (const listener of messageListeners) {
    try {
      listener(event);
    } catch (err) {
      console.error("[Test Host] Lỗi phân phối message:", err);
    }
  }
}

// 3. Dynamic import browserManagerApi sau khi window.chrome.webview đã sẵn sàng
const { browserManagerApi } = await import(
  "../../src/Fraylo.Web/src/features/browsers/browserManagerApi.ts"
);

console.log("=== BẮT ĐẦU CHẠY NEGATIVE INTEGRATION TESTS BROWSER MANAGER API ===");

let passedTests = 0;
let failedTests = 0;

async function runTestCase(name, testFn) {
  try {
    currentHostResponder = null;
    await testFn();
    console.log(`[PASS] ${name}`);
    passedTests++;
  } catch (err) {
    console.error(`[FAIL] ${name}`);
    console.error(err);
    failedTests++;
  }
}

// ==========================================
// TEST 1: listProfiles() khi Host trả về UNKNOWN_COMMAND
// ==========================================
await runTestCase(
  "browserManagerApi.listProfiles() phải reject khi Host trả về UNKNOWN_COMMAND, không trả về 3 profiles giả",
  async () => {
    currentHostResponder = (request) => {
      assert.strictEqual(request.command, "browser.list_profiles");
      queueMicrotask(() => {
        dispatchHostMessage({
          id: request.id,
          type: "response",
          success: false,
          data: null,
          error: {
            code: "UNKNOWN_COMMAND",
            message: "Lệnh 'browser.list_profiles' chưa được hỗ trợ trên Host.",
            retryable: false,
            correlationId: request.id,
            details: { command: request.command },
          },
          serverTimestampUtc: new Date().toISOString(),
          contractVersion: "1.0.4",
        });
      });
    };

    let resolvedData = null;
    let rejectedError = null;

    try {
      resolvedData = await browserManagerApi.listProfiles();
    } catch (err) {
      rejectedError = err;
    }

    assert.strictEqual(
      resolvedData,
      null,
      "LỖI NGHIÊM TRỌNG: listProfiles() không được resolve khi Host trả về UNKNOWN_COMMAND!",
    );
    assert.ok(
      rejectedError !== null,
      "listProfiles() PHẢI reject khi Host trả về lỗi UNKNOWN_COMMAND.",
    );
    assert.strictEqual(
      rejectedError.code,
      "UNKNOWN_COMMAND",
      `Mã lỗi phải là UNKNOWN_COMMAND, thực tế: ${rejectedError.code}`,
    );
  },
);

// ==========================================
// TEST 2: checkSession() khi Host trả về UNKNOWN_COMMAND
// ==========================================
await runTestCase(
  "browserManagerApi.checkSession() phải reject khi Host trả về UNKNOWN_COMMAND, không trả về Connected giả",
  async () => {
    currentHostResponder = (request) => {
      assert.strictEqual(request.command, "browser.check_session");
      queueMicrotask(() => {
        dispatchHostMessage({
          id: request.id,
          type: "response",
          success: false,
          data: null,
          error: {
            code: "UNKNOWN_COMMAND",
            message: "Lệnh 'browser.check_session' chưa được hỗ trợ trên Host.",
            retryable: false,
            correlationId: request.id,
            details: { command: request.command },
          },
          serverTimestampUtc: new Date().toISOString(),
          contractVersion: "1.0.4",
        });
      });
    };

    let resolvedData = null;
    let rejectedError = null;

    try {
      resolvedData = await browserManagerApi.checkSession("shared_ai");
    } catch (err) {
      rejectedError = err;
    }

    assert.strictEqual(
      resolvedData,
      null,
      "LỖI NGHIÊM TRỌNG: checkSession() không được resolve Connected giả khi Host trả về UNKNOWN_COMMAND!",
    );
    assert.ok(
      rejectedError !== null,
      "checkSession() PHẢI reject khi Host trả về lỗi UNKNOWN_COMMAND.",
    );
    assert.strictEqual(
      rejectedError.code,
      "UNKNOWN_COMMAND",
      `Mã lỗi phải là UNKNOWN_COMMAND, thực tế: ${rejectedError.code}`,
    );
  },
);

// ==========================================
// TEST 3: listProfiles() khi Host bị timeout
// ==========================================
await runTestCase(
  "browserManagerApi.listProfiles() phải reject REQUEST_TIMEOUT khi Host không phản hồi, không fallback mock",
  async () => {
    // Kích hoạt mock timer để tua nhanh 15000ms timeout
    mock.timers.enable({ apis: ["setTimeout"] });

    try {
      currentHostResponder = (_request) => {
        // Host treo, không gửi bất kỳ phản hồi nào
      };

      const promise = browserManagerApi.listProfiles();

      // Tiến hành tua thời gian quá 15s timeout
      mock.timers.tick(16000);

      let resolvedData = null;
      let rejectedError = null;

      try {
        resolvedData = await promise;
      } catch (err) {
        rejectedError = err;
      }

      assert.strictEqual(
        resolvedData,
        null,
        "LỖI NGHIÊM TRỌNG: listProfiles() không được fallback về mock profiles khi timeout!",
      );
      assert.ok(
        rejectedError !== null,
        "listProfiles() PHẢI reject khi bị timeout.",
      );
      assert.strictEqual(
        rejectedError.code,
        "REQUEST_TIMEOUT",
        `Mã lỗi phải là REQUEST_TIMEOUT, thực tế: ${rejectedError.code}`,
      );
    } finally {
      mock.timers.reset();
    }
  },
);

// ==========================================
// TEST 4: checkSession() khi Host bị timeout
// ==========================================
await runTestCase(
  "browserManagerApi.checkSession() phải reject REQUEST_TIMEOUT khi Host không phản hồi, không fallback mock",
  async () => {
    mock.timers.enable({ apis: ["setTimeout"] });

    try {
      currentHostResponder = (_request) => {
        // Host treo, không gửi bất kỳ phản hồi nào
      };

      const promise = browserManagerApi.checkSession("shared_ai");

      // Tiến hành tua thời gian quá 15s timeout
      mock.timers.tick(16000);

      let resolvedData = null;
      let rejectedError = null;

      try {
        resolvedData = await promise;
      } catch (err) {
        rejectedError = err;
      }

      assert.strictEqual(
        resolvedData,
        null,
        "LỖI NGHIÊM TRỌNG: checkSession() không được fallback về Connected giả khi timeout!",
      );
      assert.ok(
        rejectedError !== null,
        "checkSession() PHẢI reject khi bị timeout.",
      );
      assert.strictEqual(
        rejectedError.code,
        "REQUEST_TIMEOUT",
        `Mã lỗi phải là REQUEST_TIMEOUT, thực tế: ${rejectedError.code}`,
      );
    } finally {
      mock.timers.reset();
    }
  },
);

// ==========================================
// TEST 5: requestLogin() và cancelLogin() propagate lỗi thật từ Host
// ==========================================
await runTestCase(
  "browserManagerApi.requestLogin() và cancelLogin() propagate đúng lỗi từ Host",
  async () => {
    // Test requestLogin
    currentHostResponder = (request) => {
      assert.strictEqual(request.command, "browser.request_login");
      queueMicrotask(() => {
        dispatchHostMessage({
          id: request.id,
          type: "response",
          success: false,
          data: null,
          error: {
            code: "AUTOMATION_BLOCKED",
            message: "Một cửa sổ đăng nhập khác đang mở.",
            retryable: false,
            correlationId: request.id,
            details: null,
          },
          serverTimestampUtc: new Date().toISOString(),
          contractVersion: "1.0.4",
        });
      });
    };

    let loginError = null;
    try {
      await browserManagerApi.requestLogin("shared_ai");
    } catch (err) {
      loginError = err;
    }
    assert.ok(loginError !== null);
    assert.strictEqual(loginError.code, "AUTOMATION_BLOCKED");

    // Test cancelLogin
    currentHostResponder = (request) => {
      assert.strictEqual(request.command, "browser.cancel_login");
      queueMicrotask(() => {
        dispatchHostMessage({
          id: request.id,
          type: "response",
          success: false,
          data: null,
          error: {
            code: "ENTITY_NOT_FOUND",
            message: "Không tìm thấy phiên đăng nhập để hủy.",
            retryable: false,
            correlationId: request.id,
            details: null,
          },
          serverTimestampUtc: new Date().toISOString(),
          contractVersion: "1.0.4",
        });
      });
    };

    let cancelError = null;
    try {
      await browserManagerApi.cancelLogin("non_existent_profile");
    } catch (err) {
      cancelError = err;
    }
    assert.ok(cancelError !== null);
    assert.strictEqual(cancelError.code, "ENTITY_NOT_FOUND");
  },
);

// ==========================================
// TEST 6: simulateCloseLogin đã bị loại bỏ hoàn toàn
// ==========================================
await runTestCase(
  "browserManagerApi không còn chứa hàm mô phỏng simulateCloseLogin",
  async () => {
    assert.strictEqual(
      typeof browserManagerApi.simulateCloseLogin,
      "undefined",
      "simulateCloseLogin phải bị xóa bỏ khỏi production API!",
    );
  },
);

console.log("\n=== TỔNG KẾT KẾT QUẢ TEST ===");
console.log(`Tổng số tests: ${passedTests + failedTests}`);
console.log(`Thành công (PASS): ${passedTests}`);
console.log(`Thất bại (FAIL): ${failedTests}`);

if (failedTests > 0) {
  process.exit(1);
} else {
  console.log("TẤT CẢ CÁC BÀI TEST TIÊU CỰC ĐÃ VƯỢT QUA XUẤT SẮC (EXIT CODE 0)!\n");
  process.exit(0);
}
