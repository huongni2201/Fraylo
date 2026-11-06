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
