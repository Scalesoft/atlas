-- The trash of the objects the platform stores (unified trash spec §2): who moved one to the trash, for the trash list's "kdo a kdy",
-- and the audit of a permanent delete, the one thing left of a purged object. Pages and boards keep their own TrashedById; their
-- envelope triggers do not copy it. An object trashed before this has no known author: it stays NULL.
ALTER TABLE "ContentResource" ADD COLUMN "TrashedById" uuid NULL REFERENCES "UserAccount"("Id");

-- The trash list, newest first (ListObjectTrashHandler).
CREATE INDEX "IX_ContentResource_Trash" ON "ContentResource"("TrashedAt" DESC, "Id") WHERE "TrashedAt" IS NOT NULL;

-- An audit log, operational like AccessAuditEvent: no foreign key to the object (it is gone) nor to its space (which may go later).
CREATE TABLE "ObjectPurgeEvent" (
    "Id" uuid PRIMARY KEY,
    "ObjectId" uuid NOT NULL,
    "TypeId" varchar(64) NOT NULL,
    "Scope" varchar(16) NOT NULL,
    "SpaceId" uuid NULL,
    "Title" varchar(400) NULL,
    "PurgedById" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "PurgedAt" timestamptz NOT NULL
);
CREATE INDEX "IX_ObjectPurgeEvent_PurgedAt" ON "ObjectPurgeEvent"("PurgedAt" DESC, "Id");
