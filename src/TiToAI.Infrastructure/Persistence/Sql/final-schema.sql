CREATE TABLE "AnalyticsSnapshot" (
    "id" TEXT NOT NULL CONSTRAINT "PK_AnalyticsSnapshot" PRIMARY KEY,
    "account_id" TEXT NOT NULL,
    "source" TEXT NOT NULL,
    "period_start" TEXT NULL,
    "period_end" TEXT NULL,
    "record_count" INTEGER NOT NULL,
    "raw_manifest" TEXT NULL,
    "created_at_utc" TEXT NOT NULL
);


CREATE TABLE "AppSettings" (
    "key" TEXT NOT NULL CONSTRAINT "PK_AppSettings" PRIMARY KEY,
    "value" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL
);


CREATE TABLE "BrowserProfile" (
    "id" TEXT NOT NULL CONSTRAINT "PK_BrowserProfile" PRIMARY KEY,
    "kind" TEXT NOT NULL,
    "account_identity" TEXT NULL,
    "visibility_state" TEXT NOT NULL DEFAULT 'Hidden',
    "session_status" TEXT NOT NULL DEFAULT 'Unverified',
    "operational_state" TEXT NOT NULL DEFAULT 'Stopped',
    "provider_key" TEXT NULL,
    "display_name" TEXT NULL,
    "lifecycle" TEXT NOT NULL DEFAULT 'Active',
    "revision" INTEGER NOT NULL DEFAULT 1,
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL
);


CREATE TABLE "PromptTemplate" (
    "id" TEXT NOT NULL CONSTRAINT "PK_PromptTemplate" PRIMARY KEY,
    "purpose" TEXT NOT NULL,
    "name" TEXT NOT NULL,
    "is_active" INTEGER NOT NULL,
    "template_text" TEXT NOT NULL,
    "variable_definitions_json" TEXT NOT NULL DEFAULT '[]',
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL
);


CREATE TABLE "TikTokAnalyticsDailyRecord" (
    "id" TEXT NOT NULL CONSTRAINT "PK_TikTokAnalyticsDailyRecord" PRIMARY KEY,
    "snapshot_id" TEXT NOT NULL,
    "account_id" TEXT NOT NULL,
    "data_type" TEXT NOT NULL,
    "period_date_utc" TEXT NOT NULL,
    "video_views" INTEGER NULL,
    "profile_views" INTEGER NULL,
    "likes" INTEGER NULL,
    "comments" INTEGER NULL,
    "shares" INTEGER NULL,
    "total_viewers" INTEGER NULL,
    "new_viewers" INTEGER NULL,
    "returning_viewers" INTEGER NULL,
    "captured_at_utc" TEXT NOT NULL,
    CONSTRAINT "FK_TikTokAnalyticsDailyRecord_AnalyticsSnapshot_snapshot_id" FOREIGN KEY ("snapshot_id") REFERENCES "AnalyticsSnapshot" ("id") ON DELETE CASCADE
);


CREATE TABLE "VideoAnalyticsRecord" (
    "id" TEXT NOT NULL CONSTRAINT "PK_VideoAnalyticsRecord" PRIMARY KEY,
    "snapshot_id" TEXT NOT NULL,
    "account_id" TEXT NOT NULL,
    "video_key" TEXT NOT NULL,
    "title" TEXT NOT NULL,
    "views" INTEGER NULL,
    "likes" INTEGER NULL,
    "comments" INTEGER NULL,
    "shares" INTEGER NULL,
    "watch_time_avg_sec" REAL NULL,
    "gmv_amount" TEXT NULL,
    "affiliate_clicks" INTEGER NULL,
    "published_at_utc" TEXT NULL,
    "captured_at_utc" TEXT NOT NULL,
    CONSTRAINT "FK_VideoAnalyticsRecord_AnalyticsSnapshot_snapshot_id" FOREIGN KEY ("snapshot_id") REFERENCES "AnalyticsSnapshot" ("id") ON DELETE CASCADE
);


CREATE TABLE "BrowserHandoffCheckpoint" (
    "id" TEXT NOT NULL CONSTRAINT "PK_BrowserHandoffCheckpoint" PRIMARY KEY,
    "profile_id" TEXT NOT NULL,
    "active_job_id" TEXT NULL,
    "previous_state" TEXT NOT NULL,
    "target_visibility_state" TEXT NOT NULL,
    "reason_code" TEXT NOT NULL,
    "created_at_utc" TEXT NOT NULL,
    "resolved_at_utc" TEXT NULL,
    CONSTRAINT "FK_BrowserHandoffCheckpoint_BrowserProfile_profile_id" FOREIGN KEY ("profile_id") REFERENCES "BrowserProfile" ("id") ON DELETE CASCADE
);


CREATE TABLE "ProviderAccount" (
    "id" TEXT NOT NULL CONSTRAINT "PK_ProviderAccount" PRIMARY KEY,
    "provider_key" TEXT NOT NULL,
    "profile_id" TEXT NOT NULL,
    "display_name" TEXT NOT NULL,
    "verified_identity" TEXT NULL,
    "identity_verified_at_utc" TEXT NULL,
    "enabled" INTEGER NOT NULL DEFAULT 0,
    "lifecycle" TEXT NOT NULL DEFAULT 'Active',
    "max_concurrent_jobs" INTEGER NOT NULL DEFAULT 3,
    "max_tabs" INTEGER NOT NULL DEFAULT 3,
    "per_task_limits_json" TEXT NULL,
    "revision" INTEGER NOT NULL DEFAULT 1,
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL,
    CONSTRAINT "FK_ProviderAccount_BrowserProfile_profile_id" FOREIGN KEY ("profile_id") REFERENCES "BrowserProfile" ("id") ON DELETE RESTRICT
);


CREATE TABLE "ActiveSlotOccupancy" (
    "provider_account_id" TEXT NOT NULL,
    "tab_slot_id" TEXT NOT NULL,
    "job_id" TEXT NOT NULL,
    "lease_owner_id" TEXT NOT NULL,
    "lease_token" INTEGER NOT NULL,
    "acquired_at_utc" TEXT NOT NULL,
    CONSTRAINT "PK_ActiveSlotOccupancy" PRIMARY KEY ("provider_account_id", "tab_slot_id"),
    CONSTRAINT "FK_ActiveSlotOccupancy_ProviderAccount_provider_account_id" FOREIGN KEY ("provider_account_id") REFERENCES "ProviderAccount" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ActiveSlotOccupancy_Job_job_id" FOREIGN KEY ("job_id") REFERENCES "Job" ("id") ON DELETE CASCADE
);


CREATE TABLE "Asset" (
    "id" TEXT NOT NULL CONSTRAINT "PK_Asset" PRIMARY KEY,
    "project_id" TEXT NULL,
    "group_id" TEXT NULL,
    "storage_root_id" TEXT NOT NULL,
    "relative_path" TEXT NOT NULL,
    "asset_type" TEXT NOT NULL,
    "mime_type" TEXT NULL,
    "byte_size" INTEGER NOT NULL,
    "sha256_hash" TEXT NOT NULL,
    "status" TEXT NOT NULL DEFAULT 'Linked',
    "created_at_utc" TEXT NOT NULL,
    CONSTRAINT "FK_Asset_ProjectGroup_group_id" FOREIGN KEY ("group_id") REFERENCES "ProjectGroup" ("id") ON DELETE SET NULL,
    CONSTRAINT "FK_Asset_Project_project_id" FOREIGN KEY ("project_id") REFERENCES "Project" ("id")
);


CREATE TABLE "ProjectGroup" (
    "id" TEXT NOT NULL CONSTRAINT "PK_ProjectGroup" PRIMARY KEY,
    "name" TEXT NOT NULL,
    "description" TEXT NOT NULL DEFAULT '',
    "script_guidance" TEXT NOT NULL DEFAULT '',
    "video_guidance" TEXT NOT NULL DEFAULT '',
    "model_asset_id" TEXT NULL,
    "product_asset_id" TEXT NULL,
    "ready_image_asset_id" TEXT NULL,
    "audio_asset_id" TEXT NULL,
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL,
    CONSTRAINT "FK_ProjectGroup_Asset_audio_asset_id" FOREIGN KEY ("audio_asset_id") REFERENCES "Asset" ("id") ON DELETE SET NULL,
    CONSTRAINT "FK_ProjectGroup_Asset_model_asset_id" FOREIGN KEY ("model_asset_id") REFERENCES "Asset" ("id") ON DELETE SET NULL,
    CONSTRAINT "FK_ProjectGroup_Asset_product_asset_id" FOREIGN KEY ("product_asset_id") REFERENCES "Asset" ("id") ON DELETE SET NULL,
    CONSTRAINT "FK_ProjectGroup_Asset_ready_image_asset_id" FOREIGN KEY ("ready_image_asset_id") REFERENCES "Asset" ("id") ON DELETE SET NULL
);


CREATE TABLE "ExecutionBinding" (
    "job_id" TEXT NOT NULL CONSTRAINT "PK_ExecutionBinding" PRIMARY KEY,
    "provider_account_id" TEXT NOT NULL,
    "profile_id" TEXT NOT NULL,
    "identity" TEXT NULL,
    "capacity_config_revision" INTEGER NOT NULL,
    "tab_slot_id" TEXT NOT NULL,
    "assigned_at_utc" TEXT NOT NULL,
    "lease_owner_id" TEXT NULL,
    "lease_token" INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT "FK_ExecutionBinding_BrowserProfile_profile_id" FOREIGN KEY ("profile_id") REFERENCES "BrowserProfile" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ExecutionBinding_ProviderAccount_provider_account_id" FOREIGN KEY ("provider_account_id") REFERENCES "ProviderAccount" ("id") ON DELETE RESTRICT,
    CONSTRAINT "FK_ExecutionBinding_Job_job_id" FOREIGN KEY ("job_id") REFERENCES "Job" ("id") ON DELETE CASCADE
);


CREATE TABLE "Job" (
    "id" TEXT NOT NULL CONSTRAINT "PK_Job" PRIMARY KEY,
    "project_id" TEXT NOT NULL,
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
    CONSTRAINT "CK_Job_TaskType_Scene" CHECK ((task_type = 'Script' AND scene_id IS NULL) OR task_type IN ('Image', 'VideoPrompt', 'VideoRender')),
    CONSTRAINT "FK_Job_Project_project_id" FOREIGN KEY ("project_id") REFERENCES "Project" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_Job_Scene_scene_id_project_id" FOREIGN KEY ("scene_id", "project_id") REFERENCES "Scene" ("id", "project_id") ON DELETE CASCADE
);


CREATE TABLE "Project" (
    "id" TEXT NOT NULL CONSTRAINT "PK_Project" PRIMARY KEY,
    "title" TEXT NOT NULL,
    "brief" TEXT NOT NULL DEFAULT '',
    "status" TEXT NOT NULL DEFAULT 'Draft',
    "current_video_revision_id" TEXT NULL,
    "group_id" TEXT NULL,
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL,
    "revision" INTEGER NOT NULL DEFAULT 1,
    CONSTRAINT "FK_Project_ProjectGroup_group_id" FOREIGN KEY ("group_id") REFERENCES "ProjectGroup" ("id") ON DELETE SET NULL,
    CONSTRAINT "FK_Project_VideoRevision_current_video_revision_id_id" FOREIGN KEY ("current_video_revision_id", "id") REFERENCES "VideoRevision" ("id", "project_id")
);


CREATE TABLE "Scene" (
    "id" TEXT NOT NULL CONSTRAINT "PK_Scene" PRIMARY KEY,
    "project_id" TEXT NOT NULL,
    "sequence_order" INTEGER NOT NULL,
    "name" TEXT NOT NULL,
    "target_duration_seconds" REAL NOT NULL,
    "prompt_text" TEXT NULL,
    "status" TEXT NOT NULL DEFAULT 'Empty',
    "audio_asset_id" TEXT NULL,
    "image_asset_id" TEXT NULL,
    "video_asset_id" TEXT NULL,
    "created_at_utc" TEXT NOT NULL,
    "updated_at_utc" TEXT NOT NULL,
    CONSTRAINT "uq_scene_id_project" UNIQUE ("id", "project_id"),
    CONSTRAINT "FK_Scene_Asset_audio_asset_id" FOREIGN KEY ("audio_asset_id") REFERENCES "Asset" ("id") ON DELETE SET NULL,
    CONSTRAINT "FK_Scene_Asset_image_asset_id" FOREIGN KEY ("image_asset_id") REFERENCES "Asset" ("id") ON DELETE SET NULL,
    CONSTRAINT "FK_Scene_Asset_video_asset_id" FOREIGN KEY ("video_asset_id") REFERENCES "Asset" ("id") ON DELETE SET NULL,
    CONSTRAINT "FK_Scene_Project_project_id" FOREIGN KEY ("project_id") REFERENCES "Project" ("id") ON DELETE CASCADE
);


CREATE TABLE "VideoRevision" (
    "id" TEXT NOT NULL CONSTRAINT "PK_VideoRevision" PRIMARY KEY,
    "project_id" TEXT NOT NULL,
    "revision" INTEGER NOT NULL,
    "state" TEXT NOT NULL,
    "asset_id" TEXT NULL,
    "content_hash" TEXT NULL,
    "duration_seconds" REAL NULL,
    "width" INTEGER NULL,
    "height" INTEGER NULL,
    "provenance_json" TEXT NULL,
    "created_at_utc" TEXT NOT NULL,
    CONSTRAINT "AK_VideoRevision_id_project_id" UNIQUE ("id", "project_id"),
    CONSTRAINT "FK_VideoRevision_Asset_asset_id" FOREIGN KEY ("asset_id") REFERENCES "Asset" ("id") ON DELETE SET NULL,
    CONSTRAINT "FK_VideoRevision_Project_project_id" FOREIGN KEY ("project_id") REFERENCES "Project" ("id") ON DELETE CASCADE
);


CREATE TABLE "PublishRecord" (
    "id" TEXT NOT NULL CONSTRAINT "PK_PublishRecord" PRIMARY KEY,
    "project_id" TEXT NOT NULL,
    "video_revision_id" TEXT NOT NULL,
    "account_id" TEXT NULL,
    "caption_snapshot" TEXT NOT NULL,
    "external_post_url" TEXT NULL,
    "external_post_id" TEXT NULL,
    "state" TEXT NOT NULL,
    "user_entered_at_utc" TEXT NULL,
    "created_at_utc" TEXT NOT NULL,
    CONSTRAINT "FK_PublishRecord_Project_project_id" FOREIGN KEY ("project_id") REFERENCES "Project" ("id") ON DELETE CASCADE,
    CONSTRAINT "FK_PublishRecord_VideoRevision_video_revision_id_project_id" FOREIGN KEY ("video_revision_id", "project_id") REFERENCES "VideoRevision" ("id", "project_id") ON DELETE RESTRICT
);


CREATE UNIQUE INDEX "uq_active_slot_occupancy_job" ON "ActiveSlotOccupancy" ("job_id");


CREATE INDEX "ix_analytics_snapshot_account_created" ON "AnalyticsSnapshot" ("account_id", "created_at_utc");


CREATE INDEX "ix_asset_group_id" ON "Asset" ("group_id");


CREATE INDEX "ix_asset_project" ON "Asset" ("project_id");


CREATE INDEX "ix_browser_handoff_checkpoint_profile" ON "BrowserHandoffCheckpoint" ("profile_id");


CREATE INDEX "ix_execution_binding_account_slot" ON "ExecutionBinding" ("provider_account_id", "tab_slot_id");


CREATE INDEX "ix_execution_binding_profile" ON "ExecutionBinding" ("profile_id");


CREATE INDEX "ix_job_project" ON "Job" ("project_id");


CREATE INDEX "IX_Job_scene_id_project_id" ON "Job" ("scene_id", "project_id");


CREATE INDEX "ix_job_state_lease_retry" ON "Job" ("state", "lease_expires_at_utc", "retry_after_utc");


CREATE INDEX "IX_Project_current_video_revision_id_id" ON "Project" ("current_video_revision_id", "id");


CREATE INDEX "ix_project_group_id" ON "Project" ("group_id");


CREATE INDEX "ix_project_status_updated" ON "Project" ("status");


CREATE INDEX "IX_ProjectGroup_audio_asset_id" ON "ProjectGroup" ("audio_asset_id");


CREATE INDEX "IX_ProjectGroup_model_asset_id" ON "ProjectGroup" ("model_asset_id");


CREATE INDEX "IX_ProjectGroup_product_asset_id" ON "ProjectGroup" ("product_asset_id");


CREATE INDEX "IX_ProjectGroup_ready_image_asset_id" ON "ProjectGroup" ("ready_image_asset_id");


CREATE UNIQUE INDEX "uq_project_group_name" ON "ProjectGroup" ("name");


CREATE UNIQUE INDEX "uq_prompt_template_active_purpose" ON "PromptTemplate" ("purpose") WHERE "is_active" = 1;


CREATE UNIQUE INDEX "ix_provider_account_profile" ON "ProviderAccount" ("profile_id");


CREATE INDEX "ix_provider_account_provider" ON "ProviderAccount" ("provider_key");


CREATE UNIQUE INDEX "ix_provider_account_verified_identity" ON "ProviderAccount" ("provider_key", "verified_identity") WHERE "verified_identity" IS NOT NULL AND "lifecycle" = 'Active';


CREATE INDEX "ix_publish_record_account" ON "PublishRecord" ("account_id");


CREATE INDEX "ix_publish_record_project_created" ON "PublishRecord" ("project_id", "created_at_utc");


CREATE INDEX "IX_PublishRecord_video_revision_id_project_id" ON "PublishRecord" ("video_revision_id", "project_id");


CREATE UNIQUE INDEX "uq_publish_record_external_post" ON "PublishRecord" ("external_post_id") WHERE external_post_id IS NOT NULL;


CREATE INDEX "IX_Scene_audio_asset_id" ON "Scene" ("audio_asset_id");


CREATE INDEX "IX_Scene_image_asset_id" ON "Scene" ("image_asset_id");


CREATE INDEX "IX_Scene_video_asset_id" ON "Scene" ("video_asset_id");


CREATE UNIQUE INDEX "uq_scene_project_sequence" ON "Scene" ("project_id", "sequence_order");


CREATE INDEX "ix_tiktok_analytics_daily_account_type_date" ON "TikTokAnalyticsDailyRecord" ("account_id", "data_type", "period_date_utc");


CREATE INDEX "ix_tiktok_analytics_daily_snapshot_id" ON "TikTokAnalyticsDailyRecord" ("snapshot_id");


CREATE INDEX "ix_video_analytics_account_captured" ON "VideoAnalyticsRecord" ("account_id", "captured_at_utc");


CREATE INDEX "ix_video_analytics_account_video" ON "VideoAnalyticsRecord" ("account_id", "video_key");


CREATE INDEX "ix_video_analytics_snapshot_id" ON "VideoAnalyticsRecord" ("snapshot_id");


CREATE INDEX "ix_video_revision_asset" ON "VideoRevision" ("asset_id");


CREATE UNIQUE INDEX "uq_video_revision_project_revision" ON "VideoRevision" ("project_id", "revision");



PRAGMA user_version = 6;
