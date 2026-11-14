-- Operational configuration and run journal, not temporal business records.
CREATE TABLE "BackupSettings" (
    "Id" integer NOT NULL CONSTRAINT "pk_BackupSettings" PRIMARY KEY CONSTRAINT "ck_BackupSettings_singleton" CHECK ("Id" = 1),
    "Enabled" boolean NOT NULL DEFAULT false,
    "IntervalMinutes" integer NOT NULL DEFAULT 1440 CONSTRAINT "ck_BackupSettings_interval" CHECK ("IntervalMinutes" BETWEEN 5 AND 525600),
    "Mode" varchar(10) NOT NULL DEFAULT 'full' CONSTRAINT "ck_BackupSettings_mode" CHECK ("Mode" IN ('full', 'latest')),
    "Destination" text NOT NULL DEFAULT '',
    "Username" text NOT NULL DEFAULT '',
    "Domain" text NOT NULL DEFAULT '',
    "ProtectedPassword" text NULL,
    "RetentionCount" integer NOT NULL DEFAULT 10 CONSTRAINT "ck_BackupSettings_retention" CHECK ("RetentionCount" BETWEEN 1 AND 1000),
    "NextRunAt" timestamptz NULL
);
INSERT INTO "BackupSettings" ("Id") VALUES (1);

-- Destination/mode capture the actual run inputs even after settings change.
CREATE TABLE "BackupRun" (
    "Id" uuid NOT NULL CONSTRAINT "pk_BackupRun" PRIMARY KEY,
    "SettingsId" integer NOT NULL CONSTRAINT "fk_BackupRun_settings" REFERENCES "BackupSettings" ("Id") ON DELETE RESTRICT,
    "StartedAt" timestamptz NOT NULL,
    "FinishedAt" timestamptz NULL,
    "Status" varchar(16) NOT NULL CONSTRAINT "ck_BackupRun_status" CHECK ("Status" IN ('running','succeeded','skipped','failed')),
    "Trigger" varchar(16) NOT NULL CONSTRAINT "ck_BackupRun_trigger" CHECK ("Trigger" IN ('manual','scheduled')),
    "Mode" varchar(10) NOT NULL CONSTRAINT "ck_BackupRun_mode" CHECK ("Mode" IN ('full','latest')),
    "Destination" text NOT NULL,
    "ContentToken" text NULL,
    "FileName" text NULL,
    "Size" bigint NOT NULL DEFAULT 0 CONSTRAINT "ck_BackupRun_size" CHECK ("Size" >= 0),
    "Message" text NOT NULL DEFAULT '',
    CONSTRAINT "ck_BackupRun_finished" CHECK ("FinishedAt" IS NULL OR "FinishedAt" >= "StartedAt")
);
CREATE INDEX "ix_BackupRun_settings_started" ON "BackupRun" ("SettingsId", "StartedAt" DESC);
