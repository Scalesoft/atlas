-- KeePass vault registry (WP4 V2). Additive: no vault exists after upgrade and no content path reads these tables.
-- Passwords are stored only as ASP.NET Core Data Protection payloads (location SMB password and the KDBX master
-- password, purposes ScaleAtlas.VaultLocation.Password.v1 and ScaleAtlas.Vault.KdbxPassword.v1). No hash of a
-- password or key is stored anywhere; LastSnapshotSha256 is the SHA-256 of the encrypted file.

-- Where KDBX files live: an absolute local directory or smb://host/share/folder with its own credentials.
-- Removing a location archives it; history in VaultLocationRevision.
CREATE TABLE "VaultLocation" (
    "Id" uuid PRIMARY KEY,
    "Name" varchar(200) NOT NULL,
    "Kind" varchar(8) NOT NULL,
    "Root" varchar(1000) NOT NULL,
    "Username" varchar(200) NOT NULL DEFAULT '',
    "Domain" varchar(200) NOT NULL DEFAULT '',
    "ProtectedPassword" text NULL,
    "ArchivedAt" timestamptz NULL,
    CONSTRAINT "CK_VaultLocation_Name" CHECK (length(btrim("Name")) > 0),
    CONSTRAINT "CK_VaultLocation_Kind" CHECK ("Kind" IN ('local','smb')),
    CONSTRAINT "CK_VaultLocation_Root" CHECK (length(btrim("Root")) > 0 AND (("Kind" = 'smb') = ("Root" ILIKE 'smb://%'))),
    CONSTRAINT "CK_VaultLocation_LocalCredentials" CHECK ("Kind" = 'smb' OR ("Username" = '' AND "Domain" = '' AND "ProtectedPassword" IS NULL))
);
CREATE UNIQUE INDEX "UX_VaultLocation_Name" ON "VaultLocation"(lower("Name")) WHERE "ArchivedAt" IS NULL;

-- Trigger-written history of a location, as UserAccountRevision. The protected password itself is not copied:
-- the revision only records whether one is stored and whether this change replaced it.
CREATE TABLE "VaultLocationRevision" (
    "Id" uuid PRIMARY KEY,
    "LocationId" uuid NOT NULL,
    "Name" varchar(200) NOT NULL,
    "Kind" varchar(8) NOT NULL,
    "Root" varchar(1000) NOT NULL,
    "Username" varchar(200) NOT NULL,
    "Domain" varchar(200) NOT NULL,
    "HasPassword" boolean NOT NULL,
    "PasswordChanged" boolean NOT NULL,
    "ArchivedAt" timestamptz NULL,
    "ChangedBy" uuid NULL,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "FK_VaultLocationRevision_Location" FOREIGN KEY ("LocationId") REFERENCES "VaultLocation"("Id"),
    CONSTRAINT "FK_VaultLocationRevision_ChangedBy" FOREIGN KEY ("ChangedBy") REFERENCES "UserAccount"("Id"),
    CONSTRAINT "CK_VaultLocationRevision_History" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_VaultLocationRevision_Current" ON "VaultLocationRevision"("LocationId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_VaultLocationRevision_History" ON "VaultLocationRevision"("LocationId","ActiveFrom" DESC);
ALTER TABLE "VaultLocationRevision" ADD CONSTRAINT "EX_VaultLocationRevision_NoOverlap"
    EXCLUDE USING gist ("LocationId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&);

CREATE FUNCTION atlas_version_vault_location() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE boundary timestamptz; previous timestamptz; replaced boolean := false;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF (NEW."Name",NEW."Kind",NEW."Root",NEW."Username",NEW."Domain",NEW."ProtectedPassword",NEW."ArchivedAt")
            IS NOT DISTINCT FROM (OLD."Name",OLD."Kind",OLD."Root",OLD."Username",OLD."Domain",OLD."ProtectedPassword",OLD."ArchivedAt") THEN
            RETURN NEW;
        END IF;
        replaced := NEW."ProtectedPassword" IS DISTINCT FROM OLD."ProtectedPassword";
    END IF;
    SELECT "ActiveFrom" INTO previous FROM "VaultLocationRevision" WHERE "LocationId"=NEW."Id" AND "ActiveTo" IS NULL;
    boundary := greatest(clock_timestamp(),previous+interval '1 microsecond');
    UPDATE "VaultLocationRevision" SET "ActiveTo"=boundary WHERE "LocationId"=NEW."Id" AND "ActiveTo" IS NULL;
    INSERT INTO "VaultLocationRevision" VALUES(gen_random_uuid(),NEW."Id",NEW."Name",NEW."Kind",NEW."Root",NEW."Username",
        NEW."Domain",NEW."ProtectedPassword" IS NOT NULL,replaced,NEW."ArchivedAt",
        NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,boundary,NULL);
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_VaultLocation_History" AFTER INSERT OR UPDATE ON "VaultLocation"
    FOR EACH ROW EXECUTE FUNCTION atlas_version_vault_location();
CREATE TRIGGER "TR_VaultLocationRevision_Immutable" BEFORE UPDATE OR DELETE ON "VaultLocationRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_history_guard();

-- One KeePass file of a space. State is the last persisted outcome: unlocked (the stored password opened the file;
-- LastFailure then means the file was temporarily unavailable), locked (no working password: the master key changed
-- or the stored password cannot be decrypted; an administrator enters it again), error (the file is damaged,
-- unsupported or over a limit).
CREATE TABLE "Vault" (
    "Id" uuid PRIMARY KEY,
    "SpaceId" uuid NOT NULL,
    "Name" varchar(200) NOT NULL,
    "LocationId" uuid NOT NULL,
    "FileName" varchar(255) NOT NULL,
    "ProtectedKdbxPassword" text NULL,
    "State" varchar(16) NOT NULL,
    "LastFailure" varchar(1000) NULL,
    "LastSnapshotSha256" bytea NULL,
    "LastRefreshAt" timestamptz NULL,
    "CreatedBy" uuid NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "ArchivedAt" timestamptz NULL,
    CONSTRAINT "FK_Vault_Space" FOREIGN KEY ("SpaceId") REFERENCES "Space"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_Vault_Location" FOREIGN KEY ("LocationId") REFERENCES "VaultLocation"("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_Vault_CreatedBy" FOREIGN KEY ("CreatedBy") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    CONSTRAINT "CK_Vault_Name" CHECK (length(btrim("Name")) > 0),
    CONSTRAINT "CK_Vault_FileName" CHECK (length(btrim("FileName")) > 0 AND "FileName" !~ '[/\\]'),
    CONSTRAINT "CK_Vault_State" CHECK ("State" IN ('unlocked','locked','error')),
    CONSTRAINT "CK_Vault_UnlockedHasPassword" CHECK ("State" <> 'unlocked' OR "ProtectedKdbxPassword" IS NOT NULL),
    CONSTRAINT "CK_Vault_Snapshot" CHECK ("LastSnapshotSha256" IS NULL OR octet_length("LastSnapshotSha256") = 32)
);
-- SMB and Windows file names are case-insensitive: one file must not be registered twice under different casing.
CREATE UNIQUE INDEX "UX_Vault_LocationFile" ON "Vault"("LocationId",lower("FileName")) WHERE "ArchivedAt" IS NULL;
CREATE UNIQUE INDEX "UX_Vault_SpaceName" ON "Vault"("SpaceId",lower("Name")) WHERE "ArchivedAt" IS NULL;

-- Stable Atlas secret identity (documents reference Id) for one KeePass entry UUID of a vault. Metadata only:
-- title, group path and which fields are non-empty (bit 1 user name, 2 password, 4 URL, 8 notes). An entry that
-- disappears or moves to the recycle bin gets RemovedAt; when it comes back the same row is restored.
CREATE TABLE "VaultSecret" (
    "Id" uuid PRIMARY KEY,
    "VaultId" uuid NOT NULL,
    "EntryUuid" uuid NOT NULL,
    "Title" varchar(500) NOT NULL,
    "GroupPath" varchar(1000) NOT NULL,
    "AvailableFields" smallint NOT NULL,
    "FirstSeenAt" timestamptz NOT NULL,
    "RemovedAt" timestamptz NULL,
    CONSTRAINT "FK_VaultSecret_Vault" FOREIGN KEY ("VaultId") REFERENCES "Vault"("Id") ON DELETE RESTRICT,
    CONSTRAINT "UX_VaultSecret_Entry" UNIQUE ("VaultId","EntryUuid"),
    CONSTRAINT "CK_VaultSecret_Fields" CHECK ("AvailableFields" BETWEEN 0 AND 15)
);
