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
