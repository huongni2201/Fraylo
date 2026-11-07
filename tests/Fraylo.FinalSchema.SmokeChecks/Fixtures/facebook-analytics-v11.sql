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
