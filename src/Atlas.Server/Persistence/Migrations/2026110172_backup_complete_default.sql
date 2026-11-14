-- GAP2-1: the ZIP backup holds the whole installation (format v7) by default: only it brings back boards, contacts, comments and
-- notes. An installation that never set its ZIP backup up (off, no destination, no run) gets it as the shown choice; one that did keeps
-- what it chose.
ALTER TABLE "BackupSettings" ALTER COLUMN "Mode" SET DEFAULT 'complete';
UPDATE "BackupSettings" SET "Mode" = 'complete'
WHERE NOT "Enabled" AND "Destination" = '' AND "StorageProfileId" IS NULL AND NOT EXISTS (SELECT 1 FROM "BackupRun");
