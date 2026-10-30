import { bridgeClient } from "../../services/bridgeClient";

export interface UpdateInfo {
  currentVersion: string;
  latestVersion: string;
  hasUpdate: boolean;
  releaseNotes: string[];
  downloadUrl: string | null;
  sha256: string | null;
}

export const updatesApi = {
  check: () => bridgeClient.invoke<UpdateInfo>("updates.check", {}),
  // Host tải gói, xác minh SHA-256 rồi tự thoát để launcher cài bản mới.
  apply: () => bridgeClient.invoke<{ started: boolean }>("updates.apply", {}, 15 * 60 * 1000),
};
