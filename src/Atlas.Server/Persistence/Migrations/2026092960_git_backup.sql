-- Continuous git backup: operational configuration and what the repository already holds, not temporal business records.
CREATE TABLE "GitBackupSettings" (
    "Id" integer NOT NULL CONSTRAINT "pk_GitBackupSettings" PRIMARY KEY CONSTRAINT "ck_GitBackupSettings_singleton" CHECK ("Id" = 1),
    "Enabled" boolean NOT NULL DEFAULT false,
    "RemoteUrl" text NOT NULL DEFAULT '',
    "Branch" varchar(100) NOT NULL DEFAULT 'main',
    -- Written to .atlas/repository.json: the sync never writes into a repository another installation (or nobody) created.
    "RepositoryId" uuid NOT NULL DEFAULT gen_random_uuid(),
    "ProtectedPrivateKey" text NULL,
    "PublicKey" text NULL,
    "KnownHosts" text NOT NULL DEFAULT '',
    "LastSyncAt" timestamptz NULL,
    "LastCommit" varchar(64) NULL,
    "LastError" text NULL,
    "LastErrorAt" timestamptz NULL,
    "Failures" integer NOT NULL DEFAULT 0 CONSTRAINT "ck_GitBackupSettings_failures" CHECK ("Failures" >= 0),
    "NextAttemptAt" timestamptz NULL
);
INSERT INTO "GitBackupSettings" ("Id") VALUES (1);

-- The last pushed state of each page: its path without extension (NULL once deleted from the tree), the last committed
-- revision number and the published revision number shown in the front matter.
CREATE TABLE "GitBackupPage" (
    "PageId" uuid NOT NULL CONSTRAINT "pk_GitBackupPage" PRIMARY KEY
        CONSTRAINT "fk_GitBackupPage_page" REFERENCES "Page" ("Id") ON DELETE CASCADE,
    "Path" text NULL,
    "Version" integer NOT NULL CONSTRAINT "ck_GitBackupPage_version" CHECK ("Version" >= 0),
    "PublishedVersion" integer NULL
);
