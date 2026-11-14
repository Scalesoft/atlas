-- Refresh 2.5: the card image of a space in Knihovna. Temporal like SpacePin: an upload, a tint change and a removal close the
-- current row and insert its successor with the actor, so the rows are the history of the setting (who changed it and when). A
-- removal is a row without a file. The bytes live in attachment storage under FileId; only the current row's file is kept, the
-- previous one is deleted once the change has committed. Spaces are never deleted, so the reference does not cascade: a future
-- deletion path has to remove the stored file as well.
CREATE TABLE "SpaceCover" (
  "Id" uuid PRIMARY KEY,
  "SpaceId" uuid NOT NULL REFERENCES "Space"("Id"),
  "FileId" uuid NULL,
  "MimeType" varchar(20) NULL CONSTRAINT "CK_SpaceCover_MimeType" CHECK ("MimeType" IN ('image/png','image/jpeg','image/webp')),
  "Size" bigint NULL CONSTRAINT "CK_SpaceCover_Size" CHECK ("Size" BETWEEN 1 AND 5242880),
  "Sha256" varchar(64) NULL CONSTRAINT "CK_SpaceCover_Sha256" CHECK ("Sha256" ~ '^[a-f0-9]{64}$'),
  "Tinted" boolean NOT NULL DEFAULT true,
  "ChangedBy" uuid NULL REFERENCES "UserAccount"("Id"),
  "ActiveFrom" timestamptz NOT NULL,
  "ActiveTo" timestamptz NULL,
  CONSTRAINT "CK_SpaceCover_File" CHECK (num_nulls("FileId","MimeType","Size","Sha256") IN (0,4)),
  CHECK ("ActiveTo" IS NULL OR "ActiveTo">"ActiveFrom"),
  EXCLUDE USING gist ("SpaceId" WITH =,tstzrange("ActiveFrom","ActiveTo",'[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_SpaceCover_Current" ON "SpaceCover"("SpaceId") WHERE "ActiveTo" IS NULL;
