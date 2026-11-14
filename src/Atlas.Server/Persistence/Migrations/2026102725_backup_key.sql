-- Atlas 3 backup format v7 (docs/backup-format-v7.md): the backup key that encrypts the secure part, protected by the host's Data
-- Protection like the other stored secrets (the administrator keeps the key itself outside Atlas), and the "complete" mode of
-- the ZIP backups, which archives the whole installation in format v7. Existing rows keep their mode and get no key.
-- Numbered 2026102720 on feature/atlas-3, where develop gave that number to the search alias migration: a database that
-- recorded it under the old number runs it again, so every step is safe to repeat.
ALTER TABLE "BackupSettings"
    ADD COLUMN IF NOT EXISTS "ProtectedBackupKey" text NULL,
    ADD COLUMN IF NOT EXISTS "BackupKeyId" varchar(16) NULL,
    ADD COLUMN IF NOT EXISTS "BackupKeyCreatedAt" timestamptz NULL,
    DROP CONSTRAINT IF EXISTS "ck_BackupSettings_key",
    ADD CONSTRAINT "ck_BackupSettings_key" CHECK (("ProtectedBackupKey" IS NULL) = ("BackupKeyId" IS NULL) AND ("BackupKeyId" IS NULL) = ("BackupKeyCreatedAt" IS NULL)),
    DROP CONSTRAINT "ck_BackupSettings_mode",
    ADD CONSTRAINT "ck_BackupSettings_mode" CHECK ("Mode" IN ('full', 'latest', 'complete'));
ALTER TABLE "BackupRun"
    DROP CONSTRAINT "ck_BackupRun_mode",
    ADD CONSTRAINT "ck_BackupRun_mode" CHECK ("Mode" IN ('full', 'latest', 'complete'));
