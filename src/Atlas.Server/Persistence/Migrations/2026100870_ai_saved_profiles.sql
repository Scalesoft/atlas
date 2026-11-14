-- Additive library over immutable configuration revisions. Runtime assignments are unchanged.
-- Forward-only like the other embedded migrations: removing this table would lose named profile history.
CREATE TABLE "AiSavedProfile" (
    "Id" uuid PRIMARY KEY,
    "ProfileId" uuid NOT NULL,
    "Kind" varchar(16) NOT NULL CHECK ("Kind" IN ('chat', 'quick', 'embeddings')),
    "Name" varchar(200) NOT NULL,
    "RevisionId" uuid NOT NULL,
    "ChangedById" uuid NOT NULL,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "FK_AiSavedProfile_ChangedBy" FOREIGN KEY ("ChangedById") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    -- Kind is an enforced discriminator, not an independently editable copy of revision metadata.
    -- RevisionId intentionally pins an exact immutable configuration, including its protected credential.
    CONSTRAINT "FK_AiSavedProfile_Revision" FOREIGN KEY ("RevisionId", "Kind")
        REFERENCES "AiProfileRevision"("Id", "Kind") ON DELETE RESTRICT,
    CONSTRAINT "CK_AiSavedProfile_Name" CHECK ("Name" = btrim("Name") AND length("Name") > 0),
    CONSTRAINT "CK_AiSavedProfile_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_AiSavedProfile_Interval" EXCLUDE USING gist
        ("ProfileId" WITH =, tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_AiSavedProfile_Current" ON "AiSavedProfile" ("ProfileId") WHERE "ActiveTo" IS NULL;
CREATE UNIQUE INDEX "UX_AiSavedProfile_Name" ON "AiSavedProfile" ("Kind", lower("Name")) WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_AiSavedProfile_Revision" ON "AiSavedProfile" ("RevisionId");
CREATE INDEX "IX_AiSavedProfile_ChangedBy" ON "AiSavedProfile" ("ChangedById");

-- Retain original assignment/revision IDs and ciphertext. No live configuration or index is changed.
INSERT INTO "AiSavedProfile" ("Id", "ProfileId", "Kind", "Name", "RevisionId", "ChangedById", "ActiveFrom")
SELECT uuidv7(), uuidv7(), "Kind",
    CASE "Kind" WHEN 'chat' THEN 'Výchozí Chat' WHEN 'quick' THEN 'Výchozí Rychlé úlohy' ELSE 'Výchozí Embeddingy' END,
    "RevisionId", "ChangedById", "ActiveFrom"
FROM "AiProfileAssignment" WHERE "ActiveTo" IS NULL;
