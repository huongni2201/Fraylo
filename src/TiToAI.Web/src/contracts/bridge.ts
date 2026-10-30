export const bridgeContractVersion = "1.1.0" as const;
export const bridgeOrigin = "https://appassets.local" as const;
export const maxBridgeEnvelopeBytes = 4 * 1024 * 1024;

/**
 * Danh mục lệnh được phép trên Bridge (bao gồm Gate G1 và G2 Core Business)
 */
export const bridgeCommandAllowlist = [
  // Gate G1 — Foundation
  "settings.get",
  "settings.save",
  "settings.browse_folder",
  "settings.validate_roots",
  "theme.get_preference",
  "theme.set_preference",

  // Gate G2 / Q1 — Provider Accounts (Settings -> Tài khoản)
  "provider_account.list",
  "provider_account.create",
  "provider_account.update",
  "provider_account.probe_session",
  "provider_account.enable",
  "provider_account.delete",

  // Gate G2 — Prompts
  "prompt.list_templates",
  "prompt.get_template",
  "prompt.save_template",
  "prompt.activate_template",
  "prompt.preview",

  // Gate G2 — Projects & Scenes
  "project.list",
  "project.get",
  "project.create",
  "project.update",
  "project.delete",
  "projectgroup.list",
  "projectgroup.save",
  "projectgroup.delete",
  "scene.list_by_project",
  "scene.get",
  "scene.create",
  "scene.update",
  "scene.delete",
  "scene.reorder",

  // Gate G2 — Browser Management
  "browser.list_profiles",
  "browser.request_login",
  "browser.cancel_login",
  "browser.check_session",

  // Gate G2 — Asset Storage & Documents
  "asset.import_audio",
  "asset.select_reference_image",
  "asset.get_metadata",
  "asset.open_folder",
  "asset.list",
  "asset.get_preview",
  "asset.upload",
  "document.export",

  // Gate G2 — Job Queue & Scheduling
  "job.enqueue",
  "job.get_status",
  "job.list_active",
  "job.list_history",
  "job.cancel",
  "job.reconcile",
  "job.recover_output",
  "job.resume",
  "job.retry",
  "job.get_handoff",
  "job.import_result",

  // Gate G3 / T18 — Providers & Capabilities
  "provider.list",
  "provider.get_capabilities",

  // Gate G3 / T18 — Workflow Definition & Versioning
  "workflow.get_active",
  "workflow.save_version",
  "workflow.activate",

  // Content Creation (AI Web — ChatGPT/Gemini Web)
  "content.prepare_context",
  "content.preview_request",
  "content.select_idea",
  "content.apply_script",
  "content.apply_video_prompts",

  // Video Production from Images Workflow
  "workflow.get_config",
  "workflow.get_storyboard",
  "workflow.save_config",
  "workflow.start",
  "workflow.list_scenes",
  "workflow.update_scene_input",
  "workflow.pause",
  "workflow.resume",
  "workflow.preview_ai_prompt",
  "workflow.list_content_items",
  "workflow.update_content_item",
  "workflow.create_scene_video",

  // Gate G3 / T25 — TikTok Analytics & Affiliate Dashboard
  "analytics.get_summary",
  "analytics.list_tiktok_accounts",
  "analytics.get_timeseries",
  "analytics.list_top_videos",
  "analytics.import_data",
  "analytics.export_data",
  "analytics.check_tiktok_session",
  "analytics.open_tiktok_login",
  "analytics.sync_tiktok_channel",

  "publishing.list",
  "publishing.publish",
  "publishing.reconcile",
] as const;

export const g1CommandAllowlist = bridgeCommandAllowlist;
export type BridgeCommand = (typeof bridgeCommandAllowlist)[number];
export type G1Command = BridgeCommand;

export type RequestEnvelope<TPayload = unknown> = {
  id: string;
  type: "request";
  command: BridgeCommand | string;
  payload: TPayload;
  clientTimestampUtc: string;
  contractVersion: typeof bridgeContractVersion;
};

export type ResponseEnvelope<TData = unknown> = {
  id: string;
  type: "response";
  success: boolean;
  data: TData | null;
  error: ErrorDto | null;
  serverTimestampUtc: string;
  contractVersion: typeof bridgeContractVersion;
};

export type EventEnvelope<TPayload = unknown> = {
  id: string;
  type: "event";
  eventName: string;
  seq: number;
  payload: TPayload;
  serverTimestampUtc: string;
  contractVersion: typeof bridgeContractVersion;
};

export type ErrorDto = {
  code: string;
  message: string;
  retryable: boolean;
  correlationId: string;
  details: Record<string, unknown> | null;
};

export const ErrorCodes = {
  UNKNOWN_COMMAND: "UNKNOWN_COMMAND",
  INVALID_ARGUMENT: "INVALID_ARGUMENT",
  ENTITY_NOT_FOUND: "ENTITY_NOT_FOUND",
  VALIDATION_FAILED: "VALIDATION_FAILED",
  STORAGE_ROOT_UNAVAILABLE: "STORAGE_ROOT_UNAVAILABLE",
  PROVIDER_SESSION_REQUIRED: "PROVIDER_SESSION_REQUIRED",
  AUTOMATION_BLOCKED: "AUTOMATION_BLOCKED",
  JOB_ALREADY_SUBMITTED: "JOB_ALREADY_SUBMITTED",
  CONCURRENCY_CONFLICT: "CONCURRENCY_CONFLICT",
  INTERNAL_ERROR: "INTERNAL_ERROR",
  VERSION_MISMATCH: "VERSION_MISMATCH",
  PAYLOAD_TOO_LARGE: "PAYLOAD_TOO_LARGE",
  BRIDGE_UNAVAILABLE: "BRIDGE_UNAVAILABLE",
  REQUEST_TIMEOUT: "REQUEST_TIMEOUT",
} as const;
