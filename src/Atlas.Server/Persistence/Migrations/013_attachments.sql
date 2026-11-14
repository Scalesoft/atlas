CREATE TABLE "Attachment" (
    "Id" uuid PRIMARY KEY,
    "PageId" uuid NOT NULL REFERENCES "Page"("Id"),
    "UploadedBy" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "Name" varchar(240) NOT NULL,
    "MimeType" varchar(100) NOT NULL,
    "Size" bigint NOT NULL CHECK ("Size" BETWEEN 0 AND 26214400),
    "Sha256" varchar(64) NOT NULL CHECK ("Sha256" ~ '^[a-f0-9]{64}$'),
    "IsImage" boolean NOT NULL,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    UNIQUE ("PageId", "Id")
);
CREATE INDEX "IX_Attachment_Page" ON "Attachment"("PageId", "ActiveFrom" DESC);
-- Immutable reference membership belongs to a revision, never to the current live editor.
CREATE TABLE "PageRevisionAttachment" (
    "PageId" uuid NOT NULL,
    "RevisionId" uuid NOT NULL,
    "AttachmentId" uuid NOT NULL,
    PRIMARY KEY ("RevisionId", "AttachmentId"),
    FOREIGN KEY ("PageId", "RevisionId") REFERENCES "PageRevision"("PageId", "Id"),
    FOREIGN KEY ("PageId", "AttachmentId") REFERENCES "Attachment"("PageId", "Id")
);
CREATE INDEX "IX_PageRevisionAttachment_Attachment" ON "PageRevisionAttachment"("AttachmentId", "RevisionId");
