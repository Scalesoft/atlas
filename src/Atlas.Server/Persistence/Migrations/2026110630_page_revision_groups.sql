-- Atlas 3.6 (3.6-C): autosave checkpoints stay immutable revisions and the history groups them into versions (plan O2).
-- Existing revisions become versions of their own; their change summary stays NULL (the history shows them without one).
ALTER TABLE "PageRevision"
  ADD COLUMN "GroupId" uuid NULL,
  ADD COLUMN "GroupStartedAt" timestamptz NULL,
  ADD COLUMN "CheckpointCount" integer NOT NULL DEFAULT 1 CONSTRAINT "CK_PageRevision_CheckpointCount" CHECK ("CheckpointCount" >= 1),
  ADD COLUMN "IsExplicit" boolean NOT NULL DEFAULT false,
  ADD COLUMN "Label" varchar(500) NULL CONSTRAINT "CK_PageRevision_Label"
    CHECK ("Label" IS NULL OR (length(btrim("Label")) BETWEEN 1 AND 500 AND "Label" !~ '[[:cntrl:]]')),
  ADD COLUMN "BaseRevisionId" uuid NULL,
  ADD COLUMN "AiConversationId" uuid NULL,
  ADD COLUMN "ChangeSummary" jsonb NULL;

-- Only the new columns are set, so no trigger of PageRevision (UPDATE OF content, title or validity) fires. The installation
-- restore runs the same statement over the rows of an older dump (InstallationModules.DataMigrations), whose rows come in
-- without these columns and with triggers off: that is why GroupId and GroupStartedAt stay nullable.
UPDATE "PageRevision" r SET "GroupId" = r."Id", "GroupStartedAt" = r."ActiveFrom", "BaseRevisionId" = p."Previous"
FROM (SELECT "Id", lag("Id") OVER (PARTITION BY "PageId" ORDER BY "Version") AS "Previous" FROM "PageRevision") p
WHERE p."Id" = r."Id" AND r."GroupId" IS NULL;

CREATE INDEX "IX_PageRevision_Group" ON "PageRevision"("PageId", "GroupId", "Version" DESC);

-- A row written without the version columns (bare SQL, an installation dump from before 3.6) is a version of its own.
CREATE FUNCTION atlas_page_revision_group() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  NEW."GroupId" := coalesce(NEW."GroupId", NEW."Id");
  NEW."GroupStartedAt" := coalesce(NEW."GroupStartedAt", NEW."ActiveFrom");
  RETURN NEW;
END $$;
CREATE TRIGGER "TR_PageRevision_Group" BEFORE INSERT ON "PageRevision" FOR EACH ROW EXECUTE FUNCTION atlas_page_revision_group();
