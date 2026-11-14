-- Cleanup of orphaned items (unified trash spec §4b): what lies in the trash and objects nobody uses go for good after N days, only
-- where an admin switched it on. Temporal like the other settings: one current row, closed intervals as history, ActiveFrom is the
-- CAS version, ChangedById is the account the automatic cleanup acts as. No row means the default: each space decides, manually.

-- The wiki's rule (a wiki admin): Scope 'space' lets each space decide; 'wiki' is one rule (Automatic, Days) for every space and
-- the wiki's own objects.
CREATE TABLE "TrashCleanupSetting" (
    "Id" uuid CONSTRAINT "PK_TrashCleanupSetting" PRIMARY KEY,
    "Scope" varchar(8) NOT NULL CONSTRAINT "CK_TrashCleanupSetting_Scope" CHECK ("Scope" IN ('space','wiki')),
    "Automatic" boolean NOT NULL,
    "Days" integer NOT NULL CONSTRAINT "CK_TrashCleanupSetting_Days" CHECK ("Days" BETWEEN 7 AND 3650),
    "ChangedById" uuid NOT NULL CONSTRAINT "FK_TrashCleanupSetting_ChangedBy" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "CK_TrashCleanupSetting_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_TrashCleanupSetting_Interval" EXCLUDE USING gist (tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_TrashCleanupSetting_Current" ON "TrashCleanupSetting"((true)) WHERE "ActiveTo" IS NULL;

-- A space's own rule (a space admin), read only while the wiki's rule is 'space'.
CREATE TABLE "SpaceTrashCleanupSetting" (
    "Id" uuid CONSTRAINT "PK_SpaceTrashCleanupSetting" PRIMARY KEY,
    "SpaceId" uuid NOT NULL CONSTRAINT "FK_SpaceTrashCleanupSetting_Space" REFERENCES "Space"("Id") ON DELETE CASCADE,
    "Automatic" boolean NOT NULL,
    "Days" integer NOT NULL CONSTRAINT "CK_SpaceTrashCleanupSetting_Days" CHECK ("Days" BETWEEN 7 AND 3650),
    "ChangedById" uuid NOT NULL CONSTRAINT "FK_SpaceTrashCleanupSetting_ChangedBy" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "CK_SpaceTrashCleanupSetting_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_SpaceTrashCleanupSetting_Interval" EXCLUDE USING gist ("SpaceId" WITH =, tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_SpaceTrashCleanupSetting_Current" ON "SpaceTrashCleanupSetting"("SpaceId") WHERE "ActiveTo" IS NULL;
