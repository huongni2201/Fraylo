CREATE TABLE "AnalyticsAnalysisReport" (
    "id" TEXT NOT NULL CONSTRAINT "PK_AnalyticsAnalysisReport" PRIMARY KEY,
    "platform" TEXT NOT NULL,
    "account_id" TEXT NOT NULL,
    "page_id" TEXT NULL,
    "source_id" TEXT NOT NULL,
    "created_at_utc" TEXT NOT NULL,
    "provider_key" TEXT NULL,
    "settings_revision" INTEGER NOT NULL,
    "preparation_reason" TEXT NULL,
    "input_json" TEXT NULL,
    "input_hash" TEXT NULL,
    CONSTRAINT "CK_AnalyticsAnalysisReport_Scope" CHECK ((platform = 'tiktok' AND page_id IS NULL) OR (platform = 'facebook' AND page_id IS NOT NULL))
);
CREATE INDEX "ix_analysis_report_scope" ON "AnalyticsAnalysisReport" ("platform", "account_id", "page_id", "created_at_utc");

CREATE TABLE "Job_v12" (
    "id" TEXT NOT NULL CONSTRAINT "PK_Job" PRIMARY KEY,
    "project_id" TEXT NULL,
    "analytics_report_id" TEXT NULL,
    "scene_id" TEXT NULL,
    "task_type" TEXT NOT NULL,
    "state" TEXT NOT NULL DEFAULT 'Queued',
    "reason_code" TEXT NULL,
    "provider_key" TEXT NOT NULL,
    "config_snapshot_json" TEXT NULL,
    "prompt_snapshot_text" TEXT NULL,
    "storage_root_id" TEXT NOT NULL,
    "attempts" INTEGER NOT NULL DEFAULT 0,
    "max_attempts" INTEGER NOT NULL DEFAULT 3,
    "lease_owner_id" TEXT NULL,
    "fencing_token" INTEGER NOT NULL DEFAULT 0,
    "lease_expires_at_utc" TEXT NULL,
    "retry_after_utc" TEXT NULL,
    "side_effect_submitted" INTEGER NOT NULL DEFAULT 0,
    "side_effect_at_utc" TEXT NULL,
    "result_reference" TEXT NULL,
    "result_text" TEXT NULL,
    "cancel_requested" INTEGER NOT NULL DEFAULT 0,
    "last_heartbeat_at_utc" TEXT NULL,
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL,
    "revision" INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT "CK_Job_TaskType_Scene" CHECK ((task_type = 'AnalyticsAnalysis' AND project_id IS NULL AND scene_id IS NULL AND analytics_report_id IS NOT NULL) OR (project_id IS NOT NULL AND analytics_report_id IS NULL AND ((task_type = 'Script' AND scene_id IS NULL) OR task_type IN ('Image', 'VideoPrompt', 'VideoRender')))),
    CONSTRAINT "FK_Job_AnalyticsAnalysisReport_analytics_report_id" FOREIGN KEY ("analytics_report_id") REFERENCES "AnalyticsAnalysisReport" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_Job_Project_project_id" FOREIGN KEY ("project_id") REFERENCES "Project" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_Job_Scene_scene_id_project_id" FOREIGN KEY ("scene_id", "project_id") REFERENCES "Scene" ("id", "project_id") ON DELETE CASCADE
);
INSERT INTO "Job_v12" ("id", "project_id", "scene_id", "task_type", "state", "reason_code", "provider_key", "config_snapshot_json", "prompt_snapshot_text", "storage_root_id", "attempts", "max_attempts", "lease_owner_id", "fencing_token", "lease_expires_at_utc", "retry_after_utc", "side_effect_submitted", "side_effect_at_utc", "result_reference", "result_text", "cancel_requested", "last_heartbeat_at_utc", "created_at_utc", "updated_at_utc", "revision") SELECT "id", "project_id", "scene_id", "task_type", "state", "reason_code", "provider_key", "config_snapshot_json", "prompt_snapshot_text", "storage_root_id", "attempts", "max_attempts", "lease_owner_id", "fencing_token", "lease_expires_at_utc", "retry_after_utc", "side_effect_submitted", "side_effect_at_utc", "result_reference", "result_text", "cancel_requested", "last_heartbeat_at_utc", "created_at_utc", "updated_at_utc", "revision" FROM "Job";
DROP TABLE "Job";
ALTER TABLE "Job_v12" RENAME TO "Job";
CREATE INDEX "ix_job_project" ON "Job" ("project_id");
CREATE INDEX "IX_Job_scene_id_project_id" ON "Job" ("scene_id", "project_id");
CREATE INDEX "ix_job_state_lease_retry" ON "Job" ("state", "lease_expires_at_utc", "retry_after_utc");
CREATE UNIQUE INDEX "ux_job_analysis_report" ON "Job" ("analytics_report_id");
