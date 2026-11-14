-- Reference nodes of the space tree (spec 2026-10-27 space contents §13.1 point 3, objects spec §6): a placement of any object (a page,
-- a page section, a board, a contact) under a page or at the space root, in the one sibling order of pages and boards. A node is a link,
-- not a copy: removing it deletes the row, never the target; the target (a ContentResource id, soft: it may be trashed or gone and the
-- node then shows a lock) may live in another space or the wiki scope. A page subtree's trash batch takes its nodes along, as boards.
CREATE TABLE "TreeReference" (
    "Id" uuid PRIMARY KEY,
    "SpaceId" uuid NOT NULL REFERENCES "Space"("Id"),
    "ParentPageId" uuid NULL REFERENCES "Page"("Id"),
    "SortOrder" integer NOT NULL CONSTRAINT "CK_TreeReference_SortOrder" CHECK ("SortOrder" > 0),
    "TargetId" uuid NOT NULL,
    "Anchor" varchar(200) NULL,
    "CreatedBy" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "CreatedAt" timestamptz NOT NULL,
    "IsTrashed" boolean NOT NULL DEFAULT false,
    "TrashedAt" timestamptz NULL,
    "TrashedById" uuid NULL REFERENCES "UserAccount"("Id"),
    "TrashBatchId" uuid NULL,
    CONSTRAINT "CK_TreeReference_Trash" CHECK (
        (NOT "IsTrashed" AND "TrashedAt" IS NULL AND "TrashedById" IS NULL AND "TrashBatchId" IS NULL)
        OR ("IsTrashed" AND "TrashedAt" IS NOT NULL AND "TrashedById" IS NOT NULL AND "TrashBatchId" IS NOT NULL))
);
CREATE INDEX "IX_TreeReference_Tree" ON "TreeReference"("SpaceId", "ParentPageId", "SortOrder", "Id") WHERE NOT "IsTrashed";
CREATE INDEX "IX_TreeReference_Parent" ON "TreeReference"("ParentPageId") WHERE "ParentPageId" IS NOT NULL;
CREATE INDEX "IX_TreeReference_Target" ON "TreeReference"("TargetId");
CREATE INDEX "IX_TreeReference_Trash" ON "TreeReference"("SpaceId", "TrashedAt" DESC, "Id") WHERE "IsTrashed";

-- The tree's concurrency token moves with every node placed, moved, trashed, restored or removed, as with pages and boards.
CREATE FUNCTION atlas_version_tree_reference_organization() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        UPDATE "SpaceOrganization" SET "Version" = "Version" + 1 WHERE "SpaceId" = OLD."SpaceId";
        RETURN NULL;
    END IF;
    IF TG_OP = 'UPDATE' AND ROW(NEW."SpaceId", NEW."ParentPageId", NEW."SortOrder", NEW."IsTrashed")
        IS NOT DISTINCT FROM ROW(OLD."SpaceId", OLD."ParentPageId", OLD."SortOrder", OLD."IsTrashed") THEN
        RETURN NULL;
    END IF;
    UPDATE "SpaceOrganization" SET "Version" = "Version" + 1 WHERE "SpaceId" = NEW."SpaceId";
    IF TG_OP = 'UPDATE' AND OLD."SpaceId" <> NEW."SpaceId" THEN
        UPDATE "SpaceOrganization" SET "Version" = "Version" + 1 WHERE "SpaceId" = OLD."SpaceId";
    END IF;
    RETURN NULL;
END $$;
CREATE TRIGGER "TR_TreeReference_Organization" AFTER INSERT OR DELETE OR UPDATE OF "SpaceId", "ParentPageId", "SortOrder", "IsTrashed" ON "TreeReference"
    FOR EACH ROW EXECUTE FUNCTION atlas_version_tree_reference_organization();
