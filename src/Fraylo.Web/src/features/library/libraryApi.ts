import { bridgeClient } from "../../services/bridgeClient";

export interface AssetItem {
  assetId: string;
  projectId?: string | null;
  assetType: string;
  storageRootId: string;
  relativePath: string;
  byteSize: number;
  sha256Hash: string;
  status: string;
  createdAtUtc: string;
  mimeType?: string | null;
}

export interface ListAssetsRequest {
  includeShared?: boolean;
  projectId?: string;
  assetType?: string;
  page?: number;
  pageSize?: number;
  search?: string;
}

export interface ListAssetsResponse {
  assets: AssetItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface GetAssetPreviewRequest {
  assetId: string;
}

export interface GetAssetPreviewResponse {
  assetId: string;
  assetType: string;
  relativePath: string;
  mimeType?: string | null;
  byteSize: number;
  sha256Hash: string;
  fileExists: boolean;
  isRootOffline: boolean;
  status: string;
}

export interface OpenFolderRequest {
  assetId: string;
}

export interface OpenFolderResponse {
  success: boolean;
  folderPath?: string | null;
}

export async function listAssets(request: ListAssetsRequest): Promise<ListAssetsResponse> {
  return await bridgeClient.invoke<ListAssetsResponse>("asset.list", {
    projectId: request.projectId || undefined,
    includeShared: request.includeShared || undefined,
    assetType: request.assetType || undefined,
    page: request.page ?? 1,
    pageSize: request.pageSize ?? 20,
    search: request.search || undefined,
  });
}

export async function getAssetPreview(request: GetAssetPreviewRequest): Promise<GetAssetPreviewResponse> {
  return await bridgeClient.invoke<GetAssetPreviewResponse>("asset.get_preview", {
    assetId: request.assetId,
  });
}

export async function openAssetFolder(request: OpenFolderRequest): Promise<OpenFolderResponse> {
  return await bridgeClient.invoke<OpenFolderResponse>("asset.open_folder", {
    assetId: request.assetId,
  });
}

export interface UploadAssetRequest {
  projectId?: string;
  assetType: "Image" | "Audio" | "Video";
  fileName?: string;
  base64Data?: string;
  sourceFilePath?: string;
  /** Chỉ cho Image khi host mở hộp thoại: cho chọn nhiều tệp một lần. */
  multiple?: boolean;
}

export interface UploadAssetResponse {
  asset?: AssetItem | null;
  canceled?: boolean;
  /** Có khi multiple: mọi tệp tải thành công. */
  assets?: AssetItem[] | null;
  /** Có khi multiple: lỗi của từng tệp không tải được. */
  errors?: string[] | null;
}

export async function uploadAsset(request: UploadAssetRequest): Promise<UploadAssetResponse> {
  return await bridgeClient.invoke<UploadAssetResponse>("asset.upload", {
    projectId: request.projectId || undefined,
    assetType: request.assetType,
    fileName: request.fileName || undefined,
    base64Data: request.base64Data || undefined,
    sourceFilePath: request.sourceFilePath || undefined,
    multiple: request.multiple || undefined,
  });
}
