-- 5.2 Ověřený obsah (docs/atlas-5.2-plan.md §2, §8): the space's default interval and one row per page from its first setting. A setting, not
-- content: the installation backup carries it, the wiki archive does not. Nothing existing changes: every space keeps verification off (NULL)
-- and every page has no row, so its state is „off“. The state is computed in code (PageVerificationState), never here.
ALTER TABLE "Space" ADD COLUMN "VerificationIntervalMonths" smallint NULL
  CONSTRAINT "CK_Space_VerificationIntervalMonths" CHECK ("VerificationIntervalMonths" IN (3,6,12));

CREATE TABLE "PageVerification" (
  "PageId" uuid NOT NULL CONSTRAINT "PK_PageVerification" PRIMARY KEY
    CONSTRAINT "FK_PageVerification_Page" REFERENCES "Page"("Id") ON DELETE RESTRICT,
  -- Garant: who is reminded; null = no steward.
  "StewardId" uuid NULL CONSTRAINT "FK_PageVerification_Steward" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
  -- The page's own interval; null = the space's, 0 = the page is not verified.
  "IntervalMonths" smallint NULL CONSTRAINT "CK_PageVerification_IntervalMonths" CHECK ("IntervalMonths" IN (0,3,6,12)),
  "VerifiedById" uuid NULL CONSTRAINT "FK_PageVerification_VerifiedBy" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
  "VerifiedAt" timestamptz NULL,
  "VerifiedUntil" timestamptz NULL,
  -- The published version that was verified.
  "VerifiedVersion" integer NULL,
  -- The channel of the verification (web, api, mcp, assistant): „přes AI“ for the AI ones.
  "VerifiedChannel" varchar(16) NULL,
  -- The last notice the job sent (0 none, 1 expires soon, 2 expired): it never sends a stage twice.
  "NoticeStage" smallint NOT NULL DEFAULT 0 CONSTRAINT "CK_PageVerification_NoticeStage" CHECK ("NoticeStage" BETWEEN 0 AND 2),
  CONSTRAINT "CK_PageVerification_Verified" CHECK (("VerifiedAt" IS NULL) = ("VerifiedUntil" IS NULL)
    AND ("VerifiedAt" IS NULL) = ("VerifiedById" IS NULL) AND ("VerifiedAt" IS NULL) = ("VerifiedVersion" IS NULL))
);
CREATE INDEX "IX_PageVerification_StewardId" ON "PageVerification"("StewardId") WHERE "StewardId" IS NOT NULL;
CREATE INDEX "IX_PageVerification_VerifiedById" ON "PageVerification"("VerifiedById") WHERE "VerifiedById" IS NOT NULL;
