-- Every account has one directory contact (owner decision R3, 2026-10-31): the contact whose directoryId is the account's id, as the
-- editor's account contact already stores it (AccountContacts). The contacts' background sweep matches each active account without one,
-- the accounts of this installation right after the deploy and every new one after it: one live contact with the account's address is
-- linked, none of the same name is created, and what it cannot decide (the same name only, several contacts of one address) waits here
-- for a wiki admin as a suggested pair, open or dismissed („Není to on“, remembered). Operational (BackupCoverage): a restore matches again.
CREATE TABLE "AccountContactSuggestion" (
  "AccountId" uuid NOT NULL REFERENCES "UserAccount"("Id") ON DELETE CASCADE,
  "ContactId" uuid NOT NULL REFERENCES "ContentResource"("Id") ON DELETE CASCADE,
  "Reason" text NOT NULL CONSTRAINT "CK_AccountContactSuggestion_Reason" CHECK ("Reason" IN ('email', 'name')),
  "CreatedAt" timestamptz NOT NULL DEFAULT now(),
  "DismissedAt" timestamptz NULL,
  "DismissedById" uuid NULL,
  CONSTRAINT "PK_AccountContactSuggestion" PRIMARY KEY ("AccountId", "ContactId")
);
CREATE INDEX "IX_AccountContactSuggestion_ContactId" ON "AccountContactSuggestion" ("ContactId");
-- The sweep's question "has this account its contact" for every account, and the match of one account's contact.
CREATE INDEX "IX_ContentResource_ContactDirectoryId" ON "ContentResource" ((lower("Data"->>'directoryId')))
  WHERE "TypeId" = 'atlas.contact' AND "TrashedAt" IS NULL AND "Data"->>'directoryId' IS NOT NULL;
