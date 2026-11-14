-- Tags are immutable shared catalog values. Removing an assignment closes its period.
-- Downgrade is refused by EmbeddedSchemaMigration to protect assignment history.
CREATE TABLE "Tag" (
  "Id" uuid NOT NULL CONSTRAINT "PK_Tag" PRIMARY KEY,
  "Name" varchar(48) NOT NULL,
  "NormalizedName" varchar(48) NOT NULL,
  "CreatedAt" timestamptz NOT NULL,
  "CreatedById" uuid NOT NULL,
  CONSTRAINT "FK_Tag_UserAccount" FOREIGN KEY ("CreatedById") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
  CONSTRAINT "UX_Tag_NormalizedName" UNIQUE ("NormalizedName"),
  CONSTRAINT "CK_Tag_Name" CHECK (length(btrim("Name")) BETWEEN 1 AND 48),
  CONSTRAINT "CK_Tag_NormalizedName" CHECK (length(btrim("NormalizedName")) BETWEEN 1 AND 48)
);
CREATE INDEX "IX_Tag_CreatedById" ON "Tag"("CreatedById");

CREATE TABLE "PageTag" (
  "Id" uuid NOT NULL CONSTRAINT "PK_PageTag" PRIMARY KEY,
  "PageId" uuid NOT NULL,
  "TagId" uuid NOT NULL,
  "CreatedById" uuid NOT NULL,
  "CreatedAt" timestamptz NOT NULL,
  "ActiveFrom" timestamptz NOT NULL,
  "ActiveTo" timestamptz NULL,
  CONSTRAINT "FK_PageTag_Page" FOREIGN KEY ("PageId") REFERENCES "Page"("Id") ON DELETE RESTRICT,
  CONSTRAINT "FK_PageTag_Tag" FOREIGN KEY ("TagId") REFERENCES "Tag"("Id") ON DELETE RESTRICT,
  CONSTRAINT "FK_PageTag_UserAccount" FOREIGN KEY ("CreatedById") REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
  CONSTRAINT "CK_PageTag_Period" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
  CONSTRAINT "EX_PageTag_Period" EXCLUDE USING gist
    ("PageId" WITH =, "TagId" WITH =, tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_PageTag_Current" ON "PageTag"("PageId","TagId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_PageTag_PageId" ON "PageTag"("PageId","ActiveFrom");
CREATE INDEX "IX_PageTag_TagId" ON "PageTag"("TagId","PageId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_PageTag_TagHistory" ON "PageTag"("TagId");
CREATE INDEX "IX_PageTag_CreatedById" ON "PageTag"("CreatedById");
