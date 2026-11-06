import { domWindow } from "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
const mockAssets = [
  {
    assetId: "ast_img_01",
    projectId: "prj_01",
    assetType: "Image",
    storageRootId: "root-default-images",
    relativePath: "projects/prj_01/images/ref_01.png",
    byteSize: 1024 * 500, // 500 KB
    sha256Hash: "abcdef1234567890abcdef1234567890",
    status: "Linked",
    createdAtUtc: "2026-10-26T10:00:00Z",
    mimeType: "image/png",
  },
  {
    assetId: "ast_aud_02",
    projectId: "prj_01",
    assetType: "Audio",
    storageRootId: "root-internal-audio",
    relativePath: "projects/prj_01/audio/voice_01.mp3",
    byteSize: 1024 * 1024 * 2, // 2 MB
    sha256Hash: "123456abcdef123456abcdef123456ab",
    status: "Linked",
    createdAtUtc: "2026-10-26T11:00:00Z",
    mimeType: "audio/mpeg",
  },
  {
    assetId: "ast_vid_03",
    projectId: "prj_02",
    assetType: "Video",
    storageRootId: "root-default-videos",
    relativePath: "projects/prj_02/videos/final_01.mp4",
    byteSize: 1024 * 1024 * 15, // 15 MB
    sha256Hash: "fedcba0987654321fedcba0987654321",
    status: "Linked",
    createdAtUtc: "2026-10-26T12:00:00Z",
    mimeType: "video/mp4",
  },
];

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

      setTimeout(() => {
        let responseData: unknown = null;
        let success = true;

        switch (req.command) {
          case "asset.list": {
            const { assetType, search } = req.payload as {
              assetType?: string;
              search?: string;
            };
            let filtered = [...mockAssets];
            if (assetType && assetType !== "all") {
              filtered = filtered.filter(
                (a) => a.assetType.toLowerCase() === assetType.toLowerCase()
              );
            }
            if (search) {
              const s = search.toLowerCase();
              filtered = filtered.filter(
                (a) =>
                  a.relativePath.toLowerCase().includes(s) ||
                  a.assetId.toLowerCase().includes(s)
              );
            }
            responseData = {
              assets: filtered,
              totalCount: filtered.length,
              page: 1,
              pageSize: 20,
            };
            break;
          }
          case "asset.get_preview": {
            const { assetId } = req.payload as { assetId: string };
            const item = mockAssets.find((a) => a.assetId === assetId);
            if (item) {
              responseData = {
                assetId: item.assetId,
                assetType: item.assetType,
                relativePath: item.relativePath,
                mimeType: item.mimeType,
                byteSize: item.byteSize,
                sha256Hash: item.sha256Hash,
                fileExists: true,
                isRootOffline: false,
                status: item.status,
              };
            } else {
              success = false;
            }
            break;
          }
          case "asset.open_folder": {
            responseData = {
              success: true,
              folderPath: "D:\\Fraylo\\media\\ws_01",
            };
            break;
          }
          case "asset.upload": {
            const { projectId, assetType, fileName } = req.payload as { projectId?: string; assetType: string; fileName?: string; };
            const newAsset = {
              assetId: "ast_uploaded_99",
              projectId: projectId || null,
              assetType: assetType || "Image",
              storageRootId: "root-default-images",
              relativePath: `media/${fileName || "new_upload.png"}`,
              byteSize: 1024 * 120,
              sha256Hash: "999999abcdef123456abcdef12345699",
              status: "Linked",
              createdAtUtc: "2026-10-27T00:00:00Z",
              mimeType: assetType === "Audio" ? "audio/mpeg" : "image/png",
            };
            mockAssets.unshift(newAsset);
            responseData = {
              asset: newAsset,
              canceled: false,
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
          error: success ? null : { code: "NOT_FOUND", message: "Asset không tồn tại" },
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
const { LibraryPage, AssetPicker, AssetPreviewDialog } = await import("./index.ts");
const { bridgeClient } = await import("../../services/bridgeClient.ts");

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

test("T12b: LibraryPage render danh sách tài nguyên và filter loại", async () => {
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  await act(async () => {
    root.render(React.createElement(LibraryPage, {}));
  });
  await act(async () => {
    await sleep(50);
  });

  // Assert các cards tài nguyên được render
  const cards = container.querySelectorAll(".asset-card");
  assert.equal(cards.length, 3, "Phải hiển thị đủ 3 tài nguyên ban đầu");
  assert.equal(cards[0].querySelector("img")?.getAttribute("src"), "https://media.fraylo.local/media/ast_img_01");
  assert.ok(cards[1].querySelector("audio[controls]"));
  assert.ok(cards[2].querySelector("video[controls]"));
  await act(async () => {
    cards[0].querySelector("img")?.dispatchEvent(new domWindow.Event("error") as unknown as Event);
  });
  assert.ok(cards[0].textContent?.includes("Không thể xem trước tệp này"));

  // Kiểm tra tên tệp hiển thị
  assert.ok(
    container.textContent?.includes("ref_01.png"),
    "Phải hiển thị file ref_01.png"
  );
  assert.ok(
    container.textContent?.includes("voice_01.mp3"),
    "Phải hiển thị file voice_01.mp3"
  );
  assert.ok(
    container.textContent?.includes("final_01.mp4"),
    "Phải hiển thị file final_01.mp4"
  );

  // Click filter Âm thanh
  const audioFilterBtn = Array.from(container.querySelectorAll("button")).find(
    (b) => b.textContent?.includes("Âm thanh")
  );
  assert.ok(audioFilterBtn, "Phải có nút filter Âm thanh");

  await act(async () => {
    audioFilterBtn.dispatchEvent(new domWindow.MouseEvent("click", { bubbles: true }) as unknown as Event);
  });
  await act(async () => {
    await sleep(50);
  });

  const filteredCards = container.querySelectorAll(".asset-card");
  assert.equal(filteredCards.length, 1, "Sau khi filter Âm thanh chỉ còn 1 card");
  assert.ok(
    container.textContent?.includes("voice_01.mp3"),
    "Card còn lại là voice_01.mp3"
  );

  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("T12b: AssetPreviewDialog hiển thị metadata và trạng thái tệp", async () => {
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  let closed = false;
  await act(async () => {
    root.render(
      React.createElement(AssetPreviewDialog, {
        assetId: "ast_img_01",
        onClose: () => {
          closed = true;
        },
      })
    );
  });
  await act(async () => {
    await sleep(50);
  });

  const dialog = container.querySelector(".asset-preview-dialog");
  assert.ok(dialog, "Phải render dialog xem trước tài nguyên");
  assert.ok(dialog.querySelector("img"), "Dialog hiển thị ảnh xem trước");
  assert.ok(
    container.textContent?.includes("ast_img_01"),
    "Phải hiển thị assetId"
  );
  assert.ok(
    container.textContent?.includes("image/png"),
    "Phải hiển thị đúng mimeType"
  );
  assert.ok(
    container.textContent?.includes("Tệp vật lý sẵn sàng"),
    "Phải báo trạng thái tệp sẵn sàng"
  );

  // Test nút Đóng
  const closeBtn = Array.from(container.querySelectorAll("button")).find(
    (b) => b.textContent?.trim() === "Đóng"
  );
  assert.ok(closeBtn, "Phải có nút Đóng");

  await act(async () => {
    closeBtn.dispatchEvent(new domWindow.MouseEvent("click", { bubbles: true }) as unknown as Event);
  });
  assert.equal(closed, true, "Click Đóng phải gọi onClose");

  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("T12b: AssetPicker chọn tài nguyên và trả về cho Scene", async () => {
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  let selectedResult: unknown = null;
  let closed = false;

  await act(async () => {
    root.render(
      React.createElement(AssetPicker, {        assetType: "Image",
        onSelect: (item: unknown) => {
          selectedResult = item;
        },
        onClose: () => {
          closed = true;
        },
      })
    );
  });
  await act(async () => {
    await sleep(50);
  });

  const picker = container.querySelector(".asset-picker-dialog");
  assert.ok(picker, "Phải render dialog AssetPicker");
  assert.ok(
    container.textContent?.includes("Chọn hình ảnh từ Thư viện"),
    "Tiêu đề phải đúng cho loại Image"
  );
  assert.ok(
    container.textContent?.includes("ref_01.png"),
    "Phải hiển thị tệp ref_01.png"
  );

  // Nút Xác nhận chọn
  const confirmBtn = Array.from(container.querySelectorAll("button")).find(
    (b) => b.textContent?.includes("Xác nhận chọn")
  );
  assert.ok(confirmBtn, "Phải có nút Xác nhận chọn");

  await act(async () => {
    confirmBtn.dispatchEvent(new domWindow.MouseEvent("click", { bubbles: true }) as unknown as Event);
  });

  assert.ok(selectedResult !== null, "Phải trả về asset đã chọn");
  assert.equal((selectedResult as { assetId: string }).assetId, "ast_img_01");
  assert.equal(closed, true, "Sau khi chọn phải đóng picker");

  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("T12b: AssetPicker không có checkbox Chỉ trong dự án hiện tại và hỗ trợ upload tự lưu vào media", async () => {
  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  let selectedResult: unknown = null;
  let closed = false;

  await act(async () => {
    root.render(
      React.createElement(AssetPicker, {        projectId: "prj_01",
        assetType: "Image",
        onSelect: (item: unknown) => {
          selectedResult = item;
        },
        onClose: () => {
          closed = true;
        },
      })
    );
  });
  await act(async () => {
    await sleep(50);
  });

  const picker = container.querySelector(".asset-picker-dialog");
  assert.ok(picker, "Phải render dialog AssetPicker");

  // 1. Kiểm tra không còn checkbox 'Chỉ trong dự án hiện tại'
  assert.equal(
    container.textContent?.includes("Chỉ trong dự án hiện tại"),
    false,
    "Tuyệt đối không còn hiển thị checkbox 'Chỉ trong dự án hiện tại'"
  );

  // 2. Kiểm tra có nút Tải lên
  const uploadBtn = Array.from(container.querySelectorAll("button")).find(
    (b) => b.textContent?.includes("Tải lên")
  );
  assert.ok(uploadBtn, "Phải có nút Tải lên trong AssetPicker");

  // 3. Kích hoạt upload qua nút Tải lên
  await act(async () => {
    uploadBtn.dispatchEvent(new domWindow.MouseEvent("click", { bubbles: true }) as unknown as Event);
  });
  await act(async () => {
    await sleep(50);
  });

  // Tệp vừa upload ast_uploaded_99 phải được tự lưu vào media và hiển thị/chọn
  assert.ok(
    container.textContent?.includes("new_upload.png"),
    "Phải hiển thị tệp new_upload.png vừa tải lên vào media"
  );

  // Xác nhận chọn tệp vừa upload
  const confirmBtn = Array.from(container.querySelectorAll("button")).find(
    (b) => b.textContent?.includes("Xác nhận chọn")
  );
  assert.ok(confirmBtn, "Phải có nút Xác nhận chọn");

  await act(async () => {
    confirmBtn.dispatchEvent(new domWindow.MouseEvent("click", { bubbles: true }) as unknown as Event);
  });

  assert.ok(selectedResult !== null, "Phải trả về asset đã chọn");
  assert.equal((selectedResult as { assetId: string }).assetId, "ast_uploaded_99");
  assert.equal(closed, true, "Sau khi xác nhận chọn phải đóng picker");

  await act(async () => {
    root.unmount();
  });
  container.remove();
});
