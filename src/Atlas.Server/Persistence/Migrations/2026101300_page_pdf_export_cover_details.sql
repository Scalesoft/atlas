-- What an automatic PDF states on its cover about the revision (PdfCoverDetails): existing exports keep stating all of it.
ALTER TABLE "PagePdfExport" ADD COLUMN "CoverDetails" text NOT NULL DEFAULT 'revision,state,revisionDate,exportedAt,pageId'
  CONSTRAINT "CK_PagePdfExport_CoverDetails" CHECK ("CoverDetails" ~ '^((revision|state|revisionDate|exportedAt|pageId)(,(revision|state|revisionDate|exportedAt|pageId))*)?$');
