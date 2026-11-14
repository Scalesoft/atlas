CREATE TABLE "StorageProfile" (
    "Id" uuid NOT NULL PRIMARY KEY,
    "Name" text NOT NULL,
    "Destination" text NOT NULL,
    "Username" text NOT NULL,
    "Domain" text NOT NULL,
    "ProtectedPassword" text NULL,
    "Enabled" boolean NOT NULL DEFAULT true,
    "AllowPageExports" boolean NOT NULL DEFAULT true,
    "AllowBackups" boolean NOT NULL DEFAULT true,
    "AllSpaces" boolean NOT NULL DEFAULT false,
    "Version" integer NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX "UX_StorageProfile_name" ON "StorageProfile" (lower("Name"));
CREATE UNIQUE INDEX "UX_StorageProfile_destination" ON "StorageProfile" (lower(regexp_replace(replace("Destination", chr(92), '/'), '/+$', '')));
CREATE TABLE "StorageProfileSpace" (
    "StorageProfileId" uuid NOT NULL REFERENCES "StorageProfile" ("Id") ON DELETE CASCADE,
    "SpaceId" uuid NOT NULL REFERENCES "Space" ("Id") ON DELETE CASCADE,
    PRIMARY KEY ("StorageProfileId", "SpaceId")
);
CREATE INDEX "IX_StorageProfileSpace_space" ON "StorageProfileSpace" ("SpaceId");
ALTER TABLE "BackupSettings" ADD COLUMN "StorageProfileId" uuid NULL REFERENCES "StorageProfile" ("Id") ON DELETE RESTRICT;
ALTER TABLE "BackupSettings" ADD COLUMN "Folder" text NOT NULL DEFAULT '';
CREATE INDEX "IX_BackupSettings_profile" ON "BackupSettings" ("StorageProfileId");
-- Same DataProtection purpose as the old backup service: copy ciphertext, never decrypt in SQL.
WITH migrated AS (
    INSERT INTO "StorageProfile" ("Id", "Name", "Destination", "Username", "Domain", "ProtectedPassword", "AllowPageExports", "AllSpaces")
    SELECT gen_random_uuid(), 'Zálohy wiki', btrim("Destination"), "Username", "Domain", "ProtectedPassword", false, false
    FROM "BackupSettings" WHERE btrim("Destination") <> ''
    RETURNING "Id"
)
UPDATE "BackupSettings" SET "StorageProfileId" = migrated."Id" FROM migrated;
