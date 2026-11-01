import { domWindow } from "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";

// Mock control state
type MockMode = "empty" | "null_metrics" | "populated";
let currentMockMode: MockMode = "empty";
let mockTikTokSyncError: string | null = null;
let mockTikTokSessionOverride: {
  isLoggedIn: boolean;
  profileId: string | null;
  accountIdentity: string | null;
  sessionStatus: string;
  errorMessage: string | null;
} | null = null;
const capturedBridgeRequests: { command: string; payload: Record<string, unknown> }[] = [];

// Setup mock window.chrome.webview
window.chrome = {
  webview: {
    postMessage: (envelope: unknown) => {
      const req = envelope as {
        id: string;
        type: string;
        command: string;
        payload: Record<string, unknown>;
        contractVersion: string;
      };

      capturedBridgeRequests.push({ command: req.command, payload: req.payload });

      setTimeout(() => {
        let responseData: unknown = null;
        let success = true;

        switch (req.command) {
          case "analytics.list_tiktok_accounts": {
            responseData = {
              accounts: [{
                accountId: "acc_test_01",
                accountIdentity: "@shopify4304",
                profileId: "prof_tiktok_01",
                sessionStatus: "Connected",
              }],
            };
            break;
          }
          case "analytics.get_summary": {
            const { periodDays } = req.payload as { periodDays?: number };
            if (currentMockMode === "empty") {
              responseData = {
                periodDays: periodDays ?? 30,
                startDate: null,
                endDate: null,
                totalVideos: 0,
                totalViews: null,
                totalLikes: 0,
                totalComments: 0,
                totalShares: 0,
                averageEngagementRate: null,
                totalGmvAmount: null,
                currency: "₫",
                totalAffiliateClicks: null,
                lastCapturedAtUtc: null,
                source: null,
              };
            } else if (currentMockMode === "null_metrics") {
              responseData = {
                periodDays: periodDays ?? 30,
                startDate: "2026-10-01",
                endDate: "2026-10-27",
                totalVideos: 5,
                totalViews: 120000,
                totalLikes: 8500,
                totalComments: null, // null test
                totalShares: null,   // null test
                averageEngagementRate: null, // null test
                totalGmvAmount: null, // null test -> trigger affiliate empty state
                currency: "₫",
                totalAffiliateClicks: null, // null test
                lastCapturedAtUtc: "2026-10-27T08:00:00Z",
                source: "tiktok_csv_test",
              };
            } else {
              responseData = {
                periodDays: periodDays ?? 30,
                startDate: "2026-10-01",
                endDate: "2026-10-27",
                totalVideos: 10,
                totalViews: 500000,
                totalLikes: 45000,
                totalComments: 1200,
                totalShares: 800,
                averageEngagementRate: 9.24,
                totalGmvAmount: 25000000,
                currency: "₫",
                totalAffiliateClicks: 450,
                lastCapturedAtUtc: "2026-10-27T08:00:00Z",
                source: "tiktok_api_live",
              };
            }
            break;
          }

          case "analytics.get_timeseries": {
            if (currentMockMode === "empty") {
              responseData = [];
            } else {
              responseData = [
                { date: "2026-10-20", metric: req.payload.metric, value: 1200 },
                { date: "2026-10-21", metric: req.payload.metric, value: 2500 },
                { date: "2026-10-22", metric: req.payload.metric, value: 4100 },
              ];
            }
            break;
          }

          case "analytics.list_top_videos": {
            if (currentMockMode === "empty") {
              responseData = [];
            } else if (currentMockMode === "null_metrics") {
              responseData = [
                {
                  id: "v_null_01",
                  videoKey: "tk_v_null",
                  title: "Video thử nghiệm null metrics",
                  views: 45000,
                  likes: 1200,
                  comments: null, // Bắt buộc render "—"
                  shares: null,   // Bắt buộc render "—"
                  gmvAmount: null, // Bắt buộc render "—"
                  publishedAtUtc: "2026-10-20T12:00:00Z",
                },
              ];
            } else {
              responseData = [
                {
                  id: "v_pop_01",
                  videoKey: "tk_v_01",
                  title: "Video TikTok Top 1",
                  views: 150000,
                  likes: 15000,
                  comments: 800,
                  shares: 320,
                  gmvAmount: 12000000,
                  publishedAtUtc: "2026-10-22T10:00:00Z",
                },
              ];
            }
            break;
          }

          case "analytics.import_data": {
            responseData = {
              snapshotId: "snap_test_99",
              recordCount: 5,
              success: true,
              message: "Đã nhập thành công 5 bản ghi video!",
              periodStart: "2026-10-01",
              periodEnd: "2026-10-27",
              capturedAtUtc: "2026-10-27T08:30:00Z",
            };
            break;
          }

          case "analytics.export_data": {
            responseData = {
              format: "csv",
              content: "video_key,title,views\nv1,Sample,100",
              fileName: "tiktok_export.csv",
            };
            break;
          }

          case "analytics.check_tiktok_session": {
            if (mockTikTokSessionOverride) {
              responseData = { ...mockTikTokSessionOverride };
            } else if (currentMockMode === "empty") {
              responseData = {
                isLoggedIn: false,
                profileId: null,
                accountIdentity: null,
                sessionStatus: "unauthenticated",
                errorMessage: null,
              };
            } else {
              responseData = {
                isLoggedIn: true,
                profileId: "prof_tiktok_01",
                accountIdentity: "@shopify4304",
                sessionStatus: "active",
                errorMessage: null,
              };
            }
            break;
          }

          case "analytics.open_tiktok_login": {
            responseData = {
              success: true,
              profileId: (req.payload.profileId as string) || "prof_tiktok_01",
              visibilityState: "visible",
              message:
                "Trình duyệt TikTok đang mở trên máy tính của bạn. Vui lòng hoàn tất đăng nhập tài khoản kênh TikTok, sau đó bấm nút Kéo dữ liệu bên dưới.",
            };
            break;
          }

          case "analytics.sync_tiktok_channel": {
            if (mockTikTokSyncError) {
              responseData = { success: false, message: mockTikTokSyncError, requiresLogin: true };
              break;
            }
            responseData = {
              success: true,
              snapshotId: "snap_sync_001",
              recordCount: 15,
              channelHandle: "@shopify4304",
              message: "Đã kéo thành công 15 video từ kênh @shopify4304!",
              requiresLogin: false,
              profileId: "prof_tiktok_01",
            };
            break;
          }

          default:
            responseData = null;
        }

        const respEnvelope = {
          id: req.id,
          type: "response",
          success,
          data: responseData,
          error: success ? null : { code: "UNKNOWN_COMMAND", message: "Command not found" },
          contractVersion: req.contractVersion,
        };

        const event = new domWindow.MessageEvent("message", {
          data: JSON.stringify(respEnvelope),
        });
        window.dispatchEvent(event as unknown as Event);
      }, 5);
    },
    addEventListener: (type: string, listener: (ev: MessageEvent) => void) => {
      window.addEventListener(type, listener as EventListener);
    },
    removeEventListener: (type: string, listener: (ev: MessageEvent) => void) => {
      window.removeEventListener(type, listener as EventListener);
    },
  },
};

const React = await import("react");
const ReactDOM = await import("react-dom/client");
const { act } = React;
const { AnalyticsPage } = await import("./AnalyticsPage.tsx");
const { bridgeClient } = await import("../../services/bridgeClient.ts");

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

async function selectFirstAnalyticsAccount(container: HTMLElement) {
  const accountButton = container.querySelector<HTMLButtonElement>('[data-testid^="analytics-select-account-"]');
  assert.ok(accountButton, "Phải hiển thị tài khoản TikTok để chọn");
  await act(async () => {
    accountButton.click();
    await sleep(60);
  });
}

function setTextareaValue(element: HTMLTextAreaElement, value: string) {
  const proto = Object.getPrototypeOf(element);
  const setter = Object.getOwnPropertyDescriptor(proto, "value")?.set;
  if (setter) {
    setter.call(element, value);
  } else {
    element.value = value;
  }
  element.dispatchEvent(new Event("input", { bubbles: true }));
  element.dispatchEvent(new Event("change", { bubbles: true }));
}

test("T25-Test 1: Render đúng Empty State khi chưa có dữ liệu (totalVideos === 0)", async () => {
  currentMockMode = "empty";

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(AnalyticsPage, { active: true }));
  });

  await act(async () => {
    await sleep(60);
  });
  await selectFirstAnalyticsAccount(container);

  // Kiểm tra empty state xuất hiện
  const emptyState = container.querySelector('[data-testid="analytics-account-empty-state"]');
  assert.ok(emptyState, "Phải hiển thị khối Empty State khi tài khoản chưa có dữ liệu");
  assert.ok(
    container.textContent?.includes("Chưa có dữ liệu phân tích cho kênh này"),
    "Phải hiển thị thông báo hướng dẫn người dùng"
  );

  const importCta = container.querySelector('[data-testid="empty-state-import-cta"]');
  assert.ok(importCta, "Phải có nút CTA nhập dữ liệu phân tích");

  act(() => root.unmount());
  container.remove();
});

test("T25-Test 2: Xử lý giá trị null hiển thị ký tự '—' (không hiển thị 0 hay NaN)", async () => {
  currentMockMode = "null_metrics";

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(AnalyticsPage, { active: true }));
  });

  await act(async () => {
    await sleep(60);
  });
  await selectFirstAnalyticsAccount(container);
  const contentTab = container.querySelector<HTMLButtonElement>('[data-testid="analytics-dataset-content"]');
  assert.ok(contentTab, "Phải có tab Nội dung để xem số liệu Affiliate.");
  await act(async () => {
    contentTab.click();
    await sleep(60);
  });

  // KPI đang tải hiển thị skeleton; đợi giá trị thật thay vì ngủ cố định.
  for (let attempt = 0; attempt < 50 && !container.querySelector('[data-testid="kpi-value-comments"]'); attempt++) {
    await act(async () => {
      await sleep(20);
    });
  }

  // 1. Kiểm tra KPI Cards với trường null
  const commentsKpi = container.querySelector('[data-testid="kpi-value-comments"]');
  assert.ok(commentsKpi, "Phải có thẻ KPI comments");
  assert.equal(commentsKpi.textContent?.trim(), "—", "Metric null trong KPI phải hiển thị '—'");

  const sharesKpi = container.querySelector('[data-testid="kpi-value-shares"]');
  assert.equal(sharesKpi?.textContent?.trim(), "—", "Shares null phải hiển thị '—'");

  const engagementKpi = container.querySelector('[data-testid="kpi-value-engagement"]');
  assert.equal(engagementKpi?.textContent?.trim(), "—", "Engagement null phải hiển thị '—'");

  // 2. Kiểm tra Affiliate Panel Empty State khi GMV và Clicks đều null
  const affiliateEmpty = container.querySelector('[data-testid="affiliate-empty"]');
  assert.ok(affiliateEmpty, "Phải hiển thị empty state affiliate khi GMV và Clicks là null");
  assert.ok(
    container.textContent?.includes("Chưa có liên kết Affiliate / TikTok Shop"),
    "Phải giải thích rõ ràng trạng thái chưa liên kết bán hàng"
  );

  // 3. Kiểm tra Table Video metrics khi null
  const videoComments = container.querySelector('[data-testid="video-metric-comments"]');
  assert.equal(videoComments?.textContent?.trim(), "—", "Cột bình luận của video null phải hiển thị '—'");

  const videoShares = container.querySelector('[data-testid="video-metric-shares"]');
  assert.equal(videoShares?.textContent?.trim(), "—", "Cột chia sẻ của video null phải hiển thị '—'");

  const videoGmv = container.querySelector('[data-testid="video-metric-gmv"]');
  assert.equal(videoGmv?.textContent?.trim(), "—", "Cột GMV của video null phải hiển thị '—'");

  // Đảm bảo không chứa NaN
  assert.ok(
    !container.textContent?.includes("NaN"),
    "Giao diện tuyệt đối không được hiển thị NaN"
  );

  act(() => root.unmount());
  container.remove();
});

test("T25-Test 3: Thay đổi bộ lọc kỳ ngày (7 ngày -> 30 ngày) kích hoạt gọi lại API", async () => {
  currentMockMode = "populated";
  capturedBridgeRequests.length = 0;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(AnalyticsPage, { active: true }));
  });

  await act(async () => {
    await sleep(60);
  });
  await selectFirstAnalyticsAccount(container);

  // Ban đầu gọi với default 30 ngày
  const initialCall = capturedBridgeRequests.find(
    (r) => r.command === "analytics.get_summary" && r.payload.periodDays === 30
  );
  assert.ok(initialCall, "Mặc định phải gọi analytics.get_summary với 30 ngày");

  // Bấm vào nút lọc 7 ngày
  capturedBridgeRequests.length = 0;
  const filter7Btn = container.querySelector('[data-testid="period-filter-7"]') as HTMLButtonElement;
  assert.ok(filter7Btn, "Phải có nút lọc 7 ngày");

  await act(async () => {
    filter7Btn.click();
    await sleep(60);
  });

  // Kiểm tra request mới được gửi đi với periodDays = 7
  const filter7Call = capturedBridgeRequests.find(
    (r) => r.command === "analytics.get_summary" && r.payload.periodDays === 7
  );
  assert.ok(filter7Call, "Bấm lọc 7 ngày phải kích hoạt bridge call analytics.get_summary với periodDays = 7");

  act(() => root.unmount());
  container.remove();
});

test("T25-Test 4: Mở dialog Import, nhập dữ liệu và submit thành công", async () => {
  currentMockMode = "populated";
  capturedBridgeRequests.length = 0;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(AnalyticsPage, { active: true }));
  });

  await act(async () => {
    await sleep(60);
  });
  await selectFirstAnalyticsAccount(container);

  // Bấm mở Dialog Import
  const openImportBtn = container.querySelector('[data-testid="open-import-dialog-btn"]') as HTMLButtonElement;
  assert.ok(openImportBtn, "Phải có nút Nhập dữ liệu");

  await act(async () => {
    openImportBtn.click();
    await sleep(50);
  });

  // Kiểm tra Dialog đã mở trong document
  const textarea = document.querySelector('[data-testid="import-textarea"]') as HTMLTextAreaElement;
  assert.ok(textarea, "Textarea nhập dữ liệu phải xuất hiện khi mở modal");

  // Nhập nội dung CSV
  const sampleCsv = "video_key,title,views,likes\nv_01,TikTok Review,10000,500\nv_02,TikTok Trend,20000,1200";
  await act(async () => {
    setTextareaValue(textarea, sampleCsv);
    await sleep(30);
  });

  // Bấm nút Lưu dữ liệu
  const submitBtn = document.querySelector('[data-testid="import-submit-btn"]') as HTMLButtonElement;
  assert.ok(submitBtn, "Phải có nút submit Lưu dữ liệu");

  await act(async () => {
    submitBtn.click();
    await sleep(80);
  });

  // Kiểm tra request import đã được gửi qua Bridge
  const importReq = capturedBridgeRequests.find((r) => r.command === "analytics.import_data");
  assert.ok(importReq, "Phải kích hoạt RPC command analytics.import_data");
  assert.equal(importReq.payload.accountId, "acc_test_01");
  assert.equal(importReq.payload.format, "csv");
  assert.equal(importReq.payload.rawContent, sampleCsv);

  // Kiểm tra feedback thành công xuất hiện
  const successNotice = document.querySelector('[data-testid="import-success"]');
  assert.ok(successNotice, "Phải hiển thị thông báo thành công sau khi import");

  act(() => root.unmount());
  container.remove();
});

test("T25-Test 5: Mở modal TikTok Sync Dialog và hiển thị trạng thái kết nối tài khoản", async () => {
  currentMockMode = "populated";
  mockTikTokSessionOverride = null;
  capturedBridgeRequests.length = 0;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(AnalyticsPage, { active: true }));
  });

  await act(async () => {
    await sleep(60);
  });
  await selectFirstAnalyticsAccount(container);

  // Bấm mở Dialog Kéo dữ liệu từ kênh TikTok
  const syncBtn = container.querySelector('[data-testid="sync-tiktok-btn"]') as HTMLButtonElement;
  assert.ok(syncBtn, "Phải có nút 'Kéo dữ liệu từ kênh TikTok' trên thanh header");

  await act(async () => {
    syncBtn.click();
    await sleep(60);
  });

  // Kiểm tra RPC check_tiktok_session được gọi
  const checkSessionReq = capturedBridgeRequests.find(
    (r) => r.command === "analytics.check_tiktok_session"
  );
  assert.ok(checkSessionReq, "Phải gọi bridge command analytics.check_tiktok_session");
  assert.equal(checkSessionReq.payload.accountId, "acc_test_01");

  // Kiểm tra Dialog đã mở và hiển thị badge đã kết nối
  const connectedBadge = document.querySelector('[data-testid="tiktok-session-connected-badge"]');
  assert.ok(connectedBadge, "Phải hiển thị badge Đã kết nối phiên TikTok");
  assert.ok(
    document.body.textContent?.includes("@shopify4304"),
    "Phải hiển thị handle tài khoản TikTok @shopify4304"
  );

  const startSyncBtn = document.querySelector('[data-testid="tiktok-start-sync-btn"]') as HTMLButtonElement;
  assert.ok(startSyncBtn, "Phải có nút Bắt đầu kéo dữ liệu về");

  act(() => root.unmount());
  container.remove();
});

test("T25-Test 6: TikTok Sync Dialog xử lý trạng thái chưa đăng nhập và mở trình duyệt login", async () => {
  currentMockMode = "populated";
  mockTikTokSessionOverride = {
    isLoggedIn: false,
    profileId: "prof_tiktok_unauth",
    accountIdentity: null,
    sessionStatus: "unauthenticated",
    errorMessage: null,
  };
  capturedBridgeRequests.length = 0;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(AnalyticsPage, { active: true }));
  });

  await act(async () => {
    await sleep(60);
  });
  await selectFirstAnalyticsAccount(container);

  // Mở Dialog Sync
  const syncBtn = container.querySelector('[data-testid="sync-tiktok-btn"]') as HTMLButtonElement;
  assert.ok(syncBtn, "Phải có nút 'Kéo dữ liệu từ kênh TikTok'");

  await act(async () => {
    syncBtn.click();
    await sleep(60);
  });

  // Kiểm tra badge chưa đăng nhập
  const disconnectedBadge = document.querySelector('[data-testid="tiktok-session-disconnected-badge"]');
  assert.ok(disconnectedBadge, "Phải hiển thị badge Chưa đăng nhập TikTok");

  // Kiểm tra nút Mở trình duyệt đăng nhập
  const openLoginBtn = document.querySelector('[data-testid="tiktok-open-login-btn"]') as HTMLButtonElement;
  assert.ok(openLoginBtn, "Phải hiển thị nút Mở trình duyệt đăng nhập");

  // Bấm nút Mở trình duyệt đăng nhập
  await act(async () => {
    openLoginBtn.click();
    await sleep(60);
  });

  // Kiểm tra RPC open_tiktok_login được gọi
  const openLoginReq = capturedBridgeRequests.find(
    (r) => r.command === "analytics.open_tiktok_login"
  );
  assert.ok(openLoginReq, "Phải kích hoạt bridge command analytics.open_tiktok_login");
  assert.equal(openLoginReq.payload.accountId, "acc_test_01");

  act(() => root.unmount());
  container.remove();
  mockTikTokSessionOverride = null;
});

test("T25-Test 7: Kích hoạt nút Kéo dữ liệu từ kênh TikTok và nhận kết quả thành công", async () => {
  currentMockMode = "populated";
  mockTikTokSessionOverride = {
    isLoggedIn: true,
    profileId: "prof_tiktok_01",
    accountIdentity: "@shopify4304",
    sessionStatus: "active",
    errorMessage: null,
  };
  capturedBridgeRequests.length = 0;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(AnalyticsPage, { active: true }));
  });

  await act(async () => {
    await sleep(60);
  });
  await selectFirstAnalyticsAccount(container);

  // Mở Dialog Sync
  const syncBtn = container.querySelector('[data-testid="sync-tiktok-btn"]') as HTMLButtonElement;
  await act(async () => {
    syncBtn.click();
    await sleep(60);
  });

  // Bấm nút Bắt đầu kéo dữ liệu về
  const startSyncBtn = document.querySelector('[data-testid="tiktok-start-sync-btn"]') as HTMLButtonElement;
  assert.ok(startSyncBtn, "Phải có nút 'Bắt đầu kéo dữ liệu về'");

  capturedBridgeRequests.length = 0;
  await act(async () => {
    startSyncBtn.click();
    await sleep(100);
  });

  // Kiểm tra RPC sync_tiktok_channel được gọi
  const syncReq = capturedBridgeRequests.find(
    (r) => r.command === "analytics.sync_tiktok_channel"
  );
  assert.ok(syncReq, "Phải kích hoạt bridge command analytics.sync_tiktok_channel");
  assert.equal(syncReq.payload.accountId, "acc_test_01");

  // Kiểm tra thông báo thành công hiển thị
  const successNotice = document.querySelector('[data-testid="tiktok-sync-success"]');
  assert.ok(successNotice, "Phải hiển thị thông báo đồng bộ thành công");
  assert.ok(
    document.body.textContent?.includes("Đã kéo thành công 15 video từ kênh @shopify4304!"),
    "Phải hiển thị chi tiết số lượng video và handle kênh trả về"
  );

  act(() => root.unmount());
  container.remove();
  mockTikTokSessionOverride = null;
});

test("T25-Test 8: Giữ lỗi xác minh phiên và xóa lỗi cũ khi kiểm tra lại thành công", async () => {
  currentMockMode = "populated";
  const detail = "Không thể xác minh phiên TikTok vì profile đang được sử dụng.";
  mockTikTokSessionOverride = {
    isLoggedIn: false, profileId: "prof_tiktok_01", accountIdentity: "@shopify4304",
    sessionStatus: "Unverified", errorMessage: detail,
  };
  mockTikTokSyncError = detail;
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);
  try {
    await act(async () => {
      root.render(React.createElement(AnalyticsPage, { active: true }));
    });
    await act(async () => { await sleep(60); });
    await selectFirstAnalyticsAccount(container);
    await act(async () => {
      container.querySelector<HTMLButtonElement>('[data-testid="sync-tiktok-btn"]')!.click();
      await sleep(60);
    });
    assert.ok(document.body.textContent?.includes(detail), "Phải hiển thị lỗi probe thay vì che bằng hướng dẫn đăng nhập chung");
    assert.ok(document.querySelector('[data-testid="tiktok-session-disconnected-badge"]')?.textContent?.includes("Chưa xác minh"));
    await act(async () => {
      document.querySelector<HTMLButtonElement>('[data-testid="tiktok-start-sync-btn"]')!.click();
      await sleep(60);
    });
    assert.ok(document.querySelector('[data-testid="tiktok-sync-error"]')?.textContent?.includes(detail), "Sync phải giữ nguyên thông báo backend");
    mockTikTokSessionOverride = { ...mockTikTokSessionOverride!, isLoggedIn: true, sessionStatus: "Connected", errorMessage: null };
    await act(async () => {
      document.querySelector<HTMLButtonElement>('[data-testid="tiktok-recheck-session-btn"]')!.click();
      await sleep(60);
    });
    assert.ok(document.querySelector('[data-testid="tiktok-session-connected-badge"]'));
    assert.equal(document.querySelector('[data-testid="tiktok-sync-error"]'), null);
  } finally {
    await act(async () => root.unmount());
    container.remove();
    mockTikTokSessionOverride = null;
    mockTikTokSyncError = null;
  }
});
