-- The link migration of the contacts written into pages as a background run (contacts spec §6, K4): the admin's apply records the run
-- with its report; the worker's first round decides every identity again and creates the missing contacts as that admin (Identities:
-- identity key → contact id, NULL until then; Report: what it decided), then it rewrites the revisions page by page after AfterPageId,
-- each batch its own transaction, so a restart continues where the last round committed and a failed one is tried again.
-- Operational state, not content (BackupCoverage.Operational). One run at a time.
CREATE TABLE "ContactLinkRun" (
  "Id" uuid PRIMARY KEY,
  "State" text NOT NULL CHECK ("State" IN ('running', 'done')),
  "StartedById" uuid NOT NULL,
  "StartedAt" timestamptz NOT NULL,
  "FinishedAt" timestamptz NULL,
  "Pages" int NOT NULL,
  "PagesDone" int NOT NULL DEFAULT 0,
  "Revisions" int NOT NULL DEFAULT 0,
  "AfterPageId" uuid NULL,
  "Identities" jsonb NULL,
  "Report" jsonb NOT NULL
);
CREATE UNIQUE INDEX "UX_ContactLinkRun_Running" ON "ContactLinkRun" ("State") WHERE "State" = 'running';
