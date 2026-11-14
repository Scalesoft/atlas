-- One contact per block (owner 2026-10-30, Atlas 3.2): the split of every contactCard listing several contacts into consecutive
-- single-contact cards, over the working and the published revision of every live page, as a background run (ContactSplitRunner)
-- that this migration starts once: a pass rewrites the pages after AfterPageId, a batch per transaction, so a restart continues
-- where the last one committed. A page with unsaved live edits waits (Deferred) for a next pass from NextPassAt; the run is done
-- after a pass that deferred none. A page without such a card is never written, so a fresh installation's run ends at once.
-- Operational state, not content (BackupCoverage.Operational).
CREATE TABLE "ContactSplitRun" (
  "Id" uuid PRIMARY KEY,
  "State" text NOT NULL CONSTRAINT "CK_ContactSplitRun_State" CHECK ("State" IN ('running', 'done', 'failed')),
  "StartedAt" timestamptz NOT NULL DEFAULT now(),
  "FinishedAt" timestamptz NULL,
  "AfterPageId" uuid NULL,
  "PagesDone" int NOT NULL DEFAULT 0,
  "Revisions" int NOT NULL DEFAULT 0,
  "Deferred" int NOT NULL DEFAULT 0,
  "NextPassAt" timestamptz NULL
);
INSERT INTO "ContactSplitRun"("Id", "State") VALUES (gen_random_uuid(), 'running');
