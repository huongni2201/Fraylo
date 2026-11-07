-- migration:6
CREATE TABLE "Project_upgrade" (
    "id" TEXT NOT NULL CONSTRAINT "PK_Project" PRIMARY KEY,
    "title" TEXT NOT NULL,
    "brief" TEXT NOT NULL DEFAULT '',
    "status" TEXT NOT NULL DEFAULT 'Draft',
    "current_video_revision_id" TEXT NULL,
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL,
    "revision" INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT "FK_Project_VideoRevision_current_video_revision_id_id" FOREIGN KEY ("current_video_revision_id", "id") REFERENCES "VideoRevision" ("id", "project_id")
);
INSERT INTO "Project_upgrade" ("id","title","brief","status","current_video_revision_id","created_at_utc","updated_at_utc","revision") SELECT "id","title","brief","status","current_video_revision_id","created_at_utc","updated_at_utc","revision" FROM "Project";
DROP TABLE "Project";
ALTER TABLE "Project_upgrade" RENAME TO "Project";
CREATE INDEX "IX_Project_current_video_revision_id_id" ON "Project" ("current_video_revision_id", "id");
CREATE INDEX "ix_project_status_updated" ON "Project" ("status");
CREATE TABLE "Asset_upgrade" (
    "id" TEXT NOT NULL CONSTRAINT "PK_Asset" PRIMARY KEY,
    "project_id" TEXT NULL,
    "storage_root_id" TEXT NOT NULL,
    "relative_path" TEXT NOT NULL,
    "asset_type" TEXT NOT NULL,
    "mime_type" TEXT NULL,
    "byte_size" INTEGER NOT NULL,
    "sha256_hash" TEXT NOT NULL,
    "status" TEXT NOT NULL DEFAULT 'Linked',
    "created_at_utc" TEXT NOT NULL,
    CONSTRAINT "FK_Asset_Project_project_id" FOREIGN KEY ("project_id") REFERENCES "Project" ("id")
);
INSERT INTO "Asset_upgrade" ("id","project_id","storage_root_id","relative_path","asset_type","mime_type","byte_size","sha256_hash","status","created_at_utc") SELECT "id","project_id","storage_root_id","relative_path","asset_type","mime_type","byte_size","sha256_hash","status","created_at_utc" FROM "Asset";
DROP TABLE "Asset";
ALTER TABLE "Asset_upgrade" RENAME TO "Asset";
CREATE INDEX "ix_asset_project" ON "Asset" ("project_id");
DROP TABLE IF EXISTS "ProjectGroup";

-- migration:7
CREATE TABLE "FacebookPostBatch" (
    "id" TEXT NOT NULL CONSTRAINT "PK_FacebookPostBatch" PRIMARY KEY,
    "profile_id" TEXT NOT NULL,
    "request_hash" TEXT NOT NULL,
    "created_at_utc" TEXT NOT NULL,
    CONSTRAINT "FK_FacebookPostBatch_BrowserProfile_profile_id" FOREIGN KEY ("profile_id") REFERENCES "BrowserProfile" ("id") ON DELETE RESTRICT
);

CREATE TABLE "FacebookTarget" (
    "id" TEXT NOT NULL CONSTRAINT "PK_FacebookTarget" PRIMARY KEY,
    "profile_id" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "url" TEXT NOT NULL,
    "lifecycle" TEXT NOT NULL,
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL,
    CONSTRAINT "AK_FacebookTarget_id_profile_id" UNIQUE ("id", "profile_id"),
    CONSTRAINT "FK_FacebookTarget_BrowserProfile_profile_id" FOREIGN KEY ("profile_id") REFERENCES "BrowserProfile" ("id") ON DELETE RESTRICT
);

CREATE TABLE "FacebookPostTask" (
    "id" TEXT NOT NULL CONSTRAINT "PK_FacebookPostTask" PRIMARY KEY,
    "batch_id" TEXT NOT NULL,
    "target_id" TEXT NOT NULL,
    "profile_id" TEXT NOT NULL,
    "account_identity_snapshot" TEXT NOT NULL,
    "group_name_snapshot" TEXT NOT NULL,
    "group_url_snapshot" TEXT NOT NULL,
    "text_snapshot" TEXT NOT NULL,
    "state" TEXT NOT NULL,
    "reason_code" TEXT NULL,
    "message" TEXT NOT NULL,
    "submitted_at_utc" TEXT NULL,
    "lease_owner_id" TEXT NULL,
    "lease_expires_at_utc" TEXT NULL,
    "cancel_requested" INTEGER NOT NULL,
    "revision" INTEGER NOT NULL,
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL,
    CONSTRAINT "ck_facebook_task_state" CHECK (state IN ('Queued','Preparing','Submitting','Published','PendingApproval','Failed','Unknown','NeedsUser','Canceled')),
    CONSTRAINT "FK_FacebookPostTask_FacebookPostBatch_batch_id" FOREIGN KEY ("batch_id") REFERENCES "FacebookPostBatch" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_FacebookPostTask_FacebookTarget_target_id_profile_id" FOREIGN KEY ("target_id", "profile_id") REFERENCES "FacebookTarget" ("id", "profile_id") ON DELETE RESTRICT
);

CREATE TABLE "FacebookPostTaskMedia" (
    "task_id" TEXT NOT NULL,
    "sort_order" INTEGER NOT NULL,
    "asset_id" TEXT NOT NULL,
    "sha256" TEXT NOT NULL,
    CONSTRAINT "PK_FacebookPostTaskMedia" PRIMARY KEY ("task_id", "sort_order"),
    CONSTRAINT "FK_FacebookPostTaskMedia_Asset_asset_id" FOREIGN KEY ("asset_id") REFERENCES "Asset" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_FacebookPostTaskMedia_FacebookPostTask_task_id" FOREIGN KEY ("task_id") REFERENCES "FacebookPostTask" ("id") ON DELETE CASCADE
);

CREATE INDEX "IX_FacebookPostBatch_profile_id" ON "FacebookPostBatch" ("profile_id");

CREATE INDEX "ix_facebook_task_profile_submitted" ON "FacebookPostTask" ("profile_id", "submitted_at_utc");

CREATE INDEX "ix_facebook_task_state_created" ON "FacebookPostTask" ("state", "created_at_utc");

CREATE INDEX "IX_FacebookPostTask_batch_id" ON "FacebookPostTask" ("batch_id");

CREATE INDEX "IX_FacebookPostTask_target_id_profile_id" ON "FacebookPostTask" ("target_id", "profile_id");

CREATE UNIQUE INDEX "uq_facebook_task_active_profile" ON "FacebookPostTask" ("profile_id") WHERE state IN ('Preparing','Submitting');

CREATE INDEX "IX_FacebookPostTaskMedia_asset_id" ON "FacebookPostTaskMedia" ("asset_id");

CREATE UNIQUE INDEX "uq_facebook_target_profile_url" ON "FacebookTarget" ("profile_id", "url");

-- migration:8
CREATE TABLE "FacebookDraft" (
    "id" TEXT NOT NULL CONSTRAINT "PK_FacebookDraft" PRIMARY KEY,
    "name" TEXT NOT NULL,
    "profile_id" TEXT NOT NULL,
    "text" TEXT NOT NULL,
    "target_ids_json" TEXT NOT NULL,
    "image_asset_ids_json" TEXT NOT NULL,
    "revision" INTEGER NOT NULL,
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL
);

CREATE TABLE "FacebookTaskSchedule" (
    "task_id" TEXT NOT NULL CONSTRAINT "PK_FacebookTaskSchedule" PRIMARY KEY,
    "due_at_utc" TEXT NOT NULL,
    CONSTRAINT "FK_FacebookTaskSchedule_FacebookPostTask_task_id" FOREIGN KEY ("task_id") REFERENCES "FacebookPostTask" ("id") ON DELETE CASCADE
);

CREATE INDEX "ix_facebook_draft_updated" ON "FacebookDraft" ("updated_at_utc");

-- migration:9
ALTER TABLE "FacebookPostBatch" ADD COLUMN "name" TEXT NOT NULL DEFAULT '';
ALTER TABLE "FacebookPostBatch" ADD COLUMN "paused" INTEGER NOT NULL DEFAULT 0;
ALTER TABLE "FacebookPostBatch" ADD COLUMN "revision" INTEGER NOT NULL DEFAULT 1;
ALTER TABLE "FacebookPostTask" ADD COLUMN "sort_order" INTEGER NOT NULL DEFAULT 0;
ALTER TABLE "FacebookDraft" ADD COLUMN "due_at_utc" TEXT NULL;

-- migration:10
CREATE TABLE "FacebookAnalyticsPage" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_FacebookAnalyticsPage" PRIMARY KEY,
    "ProfileId" TEXT NOT NULL,
    "FacebookPageId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Url" TEXT NOT NULL,
    "VerifiedAtUtc" TEXT NOT NULL,
    CONSTRAINT "FK_FacebookAnalyticsPage_BrowserProfile_ProfileId" FOREIGN KEY ("ProfileId") REFERENCES "BrowserProfile" ("id") ON DELETE RESTRICT
);

CREATE TABLE "FacebookAnalyticsCapture" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_FacebookAnalyticsCapture" PRIMARY KEY,
    "PageId" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "StartedAtUtc" TEXT NOT NULL,
    "FinishedAtUtc" TEXT NULL,
    "Message" TEXT NOT NULL,
    "RecordCount" INTEGER NOT NULL,
    "Source" TEXT NOT NULL,
    "Scope" TEXT NOT NULL,
    "Owner" TEXT NOT NULL,
    CONSTRAINT "ck_fba_capture_status" CHECK ("Status" IN ('Running','Complete','Partial','Failed','Canceled')),
    CONSTRAINT "FK_FacebookAnalyticsCapture_FacebookAnalyticsPage_PageId" FOREIGN KEY ("PageId") REFERENCES "FacebookAnalyticsPage" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "FacebookContentMetricSnapshot" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_FacebookContentMetricSnapshot" PRIMARY KEY,
    "CaptureId" TEXT NOT NULL,
    "ContentId" TEXT NOT NULL,
    "DataJson" TEXT NOT NULL,
    CONSTRAINT "FK_FacebookContentMetricSnapshot_FacebookAnalyticsCapture_CaptureId" FOREIGN KEY ("CaptureId") REFERENCES "FacebookAnalyticsCapture" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX "IX_FacebookAnalyticsPage_ProfileId_FacebookPageId" ON "FacebookAnalyticsPage" ("ProfileId", "FacebookPageId");
CREATE INDEX "IX_FacebookAnalyticsCapture_PageId_StartedAtUtc" ON "FacebookAnalyticsCapture" ("PageId", "StartedAtUtc");
CREATE UNIQUE INDEX "IX_FacebookContentMetricSnapshot_CaptureId_ContentId" ON "FacebookContentMetricSnapshot" ("CaptureId", "ContentId");
CREATE INDEX "IX_FacebookContentMetricSnapshot_ContentId" ON "FacebookContentMetricSnapshot" ("ContentId");

-- migration:11
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

-- migration:12
ALTER TABLE "FacebookPostBatch" ADD COLUMN "requires_rewrite" INTEGER NOT NULL DEFAULT 0;
ALTER TABLE "FacebookPostBatch" ADD COLUMN "original_text_snapshot" TEXT NULL;
CREATE TABLE "FacebookRewriteChunk" (
    "id" TEXT NOT NULL CONSTRAINT "PK_FacebookRewriteChunk" PRIMARY KEY,
    "batch_id" TEXT NOT NULL,
    "ordinal" INTEGER NOT NULL,
    "task_ids_json" TEXT NOT NULL,
    "applied_at_utc" TEXT NULL,
    "apply_error" TEXT NULL,
    CONSTRAINT "FK_FacebookRewriteChunk_FacebookPostBatch_batch_id" FOREIGN KEY ("batch_id") REFERENCES "FacebookPostBatch" ("id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX "ux_facebook_rewrite_chunk" ON "FacebookRewriteChunk" ("batch_id", "ordinal");
CREATE TABLE "Job_v13" (
    "id" TEXT NOT NULL CONSTRAINT "PK_Job" PRIMARY KEY,
    "project_id" TEXT NULL,
    "analytics_report_id" TEXT NULL,
    "facebook_rewrite_chunk_id" TEXT NULL,
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
    CONSTRAINT "CK_Job_TaskType_Scene" CHECK ((task_type = 'AnalyticsAnalysis' AND project_id IS NULL AND scene_id IS NULL AND analytics_report_id IS NOT NULL AND facebook_rewrite_chunk_id IS NULL) OR (task_type = 'FacebookContentRewrite' AND project_id IS NULL AND scene_id IS NULL AND analytics_report_id IS NULL AND facebook_rewrite_chunk_id IS NOT NULL) OR (project_id IS NOT NULL AND analytics_report_id IS NULL AND facebook_rewrite_chunk_id IS NULL AND ((task_type = 'Script' AND scene_id IS NULL) OR task_type IN ('Image', 'VideoPrompt', 'VideoRender')))),
    CONSTRAINT "FK_Job_FacebookRewriteChunk_facebook_rewrite_chunk_id" FOREIGN KEY ("facebook_rewrite_chunk_id") REFERENCES "FacebookRewriteChunk" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_Job_AnalyticsAnalysisReport_analytics_report_id" FOREIGN KEY ("analytics_report_id") REFERENCES "AnalyticsAnalysisReport" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_Job_Project_project_id" FOREIGN KEY ("project_id") REFERENCES "Project" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_Job_Scene_scene_id_project_id" FOREIGN KEY ("scene_id", "project_id") REFERENCES "Scene" ("id", "project_id") ON DELETE CASCADE
);
INSERT INTO "Job_v13" ("id", "project_id", "analytics_report_id", "scene_id", "task_type", "state", "reason_code", "provider_key", "config_snapshot_json", "prompt_snapshot_text", "storage_root_id", "attempts", "max_attempts", "lease_owner_id", "fencing_token", "lease_expires_at_utc", "retry_after_utc", "side_effect_submitted", "side_effect_at_utc", "result_reference", "result_text", "cancel_requested", "last_heartbeat_at_utc", "created_at_utc", "updated_at_utc", "revision") SELECT "id", "project_id", "analytics_report_id", "scene_id", "task_type", "state", "reason_code", "provider_key", "config_snapshot_json", "prompt_snapshot_text", "storage_root_id", "attempts", "max_attempts", "lease_owner_id", "fencing_token", "lease_expires_at_utc", "retry_after_utc", "side_effect_submitted", "side_effect_at_utc", "result_reference", "result_text", "cancel_requested", "last_heartbeat_at_utc", "created_at_utc", "updated_at_utc", "revision" FROM "Job";
DROP TABLE "Job";
ALTER TABLE "Job_v13" RENAME TO "Job";
CREATE INDEX "ix_job_project" ON "Job" ("project_id");
CREATE INDEX "IX_Job_scene_id_project_id" ON "Job" ("scene_id", "project_id");
CREATE INDEX "ix_job_state_lease_retry" ON "Job" ("state", "lease_expires_at_utc", "retry_after_utc");
CREATE UNIQUE INDEX "ux_job_analysis_report" ON "Job" ("analytics_report_id");

CREATE UNIQUE INDEX "ux_job_facebook_rewrite_chunk" ON "Job" ("facebook_rewrite_chunk_id");

-- migration:13
ALTER TABLE "FacebookDraft" ADD "rewrite_content" INTEGER NOT NULL DEFAULT 1;

-- migration:14
ALTER TABLE "FacebookPostTask" ADD "activity_log_json" TEXT NOT NULL DEFAULT '[]';
ALTER TABLE "FacebookAnalyticsCapture" ADD "activity_log_json" TEXT NOT NULL DEFAULT '[]';
