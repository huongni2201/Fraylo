import { domWindow } from "../../test/setupDom.ts";
import assert from "node:assert/strict";
import test from "node:test";
const initialScenes: Array<{
  sceneId: string;
  projectId: string;
  name: string;
  sceneSequence: number;
  targetDurationSeconds: number;
  promptText: string;
  status: string;
  audioAssetId: string | null;
  imageAssetId: string | null;
  videoAssetId: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  revision: string;
}> = [
  {
    sceneId: "scn_01",
    projectId: "prj_01",
    name: "Phân cảnh mở đầu",
    sceneSequence: 1,
    targetDurationSeconds: 15,
    promptText: "Cận cảnh nhân vật mỉm cười chào người xem",
    status: "PromptReady",
    audioAssetId: "ast_aud_01",
    imageAssetId: "ast_img_01",
    videoAssetId: null,
    createdAtUtc: "2026-10-26T10:00:00Z",
    updatedAtUtc: "2026-10-26T10:00:00Z",
    revision: "rev_01",
  },
];

let lastUpdateRequest: Record<string, any> | null = null;

// Mock window.chrome.webview
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
          case "scene.update": {
            lastUpdateRequest = req.payload;
            const scn = { ...initialScenes[0] };
            if (req.payload.audioAssetId !== undefined) {
              scn.audioAssetId = req.payload.audioAssetId === "" ? null : (req.payload.audioAssetId as string);
            }
            if (req.payload.imageAssetId !== undefined) {
              scn.imageAssetId = req.payload.imageAssetId === "" ? null : (req.payload.imageAssetId as string);
            }
            scn.revision = "rev_02";
            responseData = { scene: scn };
            break;
          }
          case "asset.list": {
            responseData = {
              assets: [
                {
                  assetId: "ast_img_02",
                  projectId: "prj_01",
                  assetType: "Image",
                  storageRootId: "root-default-images",
                  relativePath: "ws_01/projects/prj_01/images/new_ref.png",
                  byteSize: 1024 * 300,
                  sha256Hash: "hash999",
                  status: "Linked",
                  createdAtUtc: "2026-10-26T12:00:00Z",
                  mimeType: "image/png",
                },
              ],
              totalCount: 1,
              page: 1,
              pageSize: 20,
            };
            break;
          }
          case "asset.get_preview": {
            responseData = {
              assetId: "ast_aud_01",
              assetType: "Audio",
              relativePath: "ws_01/projects/prj_01/audio/voice.mp3",
              mimeType: "audio/mpeg",
              byteSize: 1024 * 500,
              sha256Hash: "hash123",
              fileExists: true,
              isRootOffline: false,
              status: "Linked",
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
          error: null,
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
const { SceneTable } = await import("./SceneTable.tsx");
const { bridgeClient } = await import("../../services/bridgeClient.ts");

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

test("T12a: SceneTable Detach Audio gửi chuỗi rỗng và revision", async () => {
  lastUpdateRequest = null;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  let refreshed = false;
  await act(async () => {
    root.render(
      React.createElement(SceneTable, {
        projectId: "prj_01",
        scenes: initialScenes,
        isLoading: false,
        onRefresh: () => {
          refreshed = true;
        },
        onEditScene: () => {},
      })
    );
  });

  await sleep(50);

  // Tìm nút Gỡ audio
  const detachAudioBtn = container.querySelector('button[title*="Gỡ audio"]');
  assert.ok(detachAudioBtn, "Phải có nút Gỡ audio khi scene có audioAssetId");

  await act(async () => {
    detachAudioBtn.dispatchEvent(new domWindow.MouseEvent("click", { bubbles: true }) as unknown as Event);
    await sleep(50);
  });

  assert.ok(lastUpdateRequest !== null, "Phải gọi scene.update");
  assert.equal((lastUpdateRequest as any).audioAssetId, "", "AudioAssetId phải được gửi là chuỗi rỗng để detach");
  assert.equal((lastUpdateRequest as any).expectedRevision, "rev_01", "Phải gửi kèm expectedRevision");
  assert.equal(refreshed, true, "Phải gọi onRefresh sau khi update thành công");

  await act(async () => {
    root.unmount();
  });
  container.remove();
});

test("T12a: SceneTable Detach Image gửi chuỗi rỗng và revision", async () => {
  lastUpdateRequest = null;

  const container = document.createElement("div");
  document.body.appendChild(container);
  const root = ReactDOM.createRoot(container);

  let refreshed = false;
  await act(async () => {
    root.render(
      React.createElement(SceneTable, {
        projectId: "prj_01",
        scenes: initialScenes,
        isLoading: false,
        onRefresh: () => {
          refreshed = true;
        },
        onEditScene: () => {},
      })
    );
  });

  await sleep(50);

  const detachImgBtn = container.querySelector('button[title*="Gỡ ảnh"]');
  assert.ok(detachImgBtn, "Phải có nút Gỡ ảnh khi scene có imageAssetId");

  await act(async () => {
    detachImgBtn.dispatchEvent(new domWindow.MouseEvent("click", { bubbles: true }) as unknown as Event);
    await sleep(50);
  });

  assert.ok(lastUpdateRequest !== null, "Phải gọi scene.update");
  assert.equal((lastUpdateRequest as any).imageAssetId, "", "ImageAssetId phải là chuỗi rỗng để detach");
  assert.equal((lastUpdateRequest as any).expectedRevision, "rev_01", "Phải gửi kèm expectedRevision");
  assert.equal(refreshed, true, "Phải gọi onRefresh");

  await act(async () => {
    root.unmount();
  });
  container.remove();
});
