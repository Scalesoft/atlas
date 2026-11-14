-- The graph of what content names (audit engine §5, P15): every person, page and YouTrack issue a page revision, a comment or a board
-- item names, linked (a chip or a link to a page), unresolved (a chip of several people nobody picked) or suggested (plain text the
-- recognition core knows, Atlas.Markdown.Sync/Recognition). Only ContentReferences.Extract writes it, a source's rows replaced whole:
-- from ContentRevised of a page (outbox) and from the sweep (ContentReferenceWorker), which also extracts what the lexicon of the place
-- changed for, comments, board items and, right after this migration, every existing page. Identities only, no rights: the reads decide
-- what the reader may see. Derived (BackupCoverage): a restore leaves it empty and the sweep fills it again.
CREATE TABLE "ContentReference" (
  "SourceId" uuid NOT NULL,
  "SourcePart" text NOT NULL,
  "RevisionKind" text NOT NULL CONSTRAINT "CK_ContentReference_RevisionKind" CHECK ("RevisionKind" IN ('working', 'published', 'none')),
  "Ordinal" integer NOT NULL,
  "Kind" text NOT NULL CONSTRAINT "CK_ContentReference_Kind" CHECK ("Kind" IN ('person', 'page', 'issue')),
  "Relation" text NOT NULL CONSTRAINT "CK_ContentReference_Relation" CHECK ("Relation" IN ('mention', 'link')),
  "State" text NOT NULL CONSTRAINT "CK_ContentReference_State" CHECK ("State" IN ('linked', 'unresolved', 'suggested')),
  "TargetId" uuid NULL,
  "TargetKey" text NULL,
  "Candidates" text[] NULL,
  "Text" varchar(200) NULL,
  "Anchor" text NULL,
  "Context" varchar(200) NULL,
  "Recognizer" text NULL,
  CONSTRAINT "PK_ContentReference" PRIMARY KEY ("SourceId", "SourcePart", "Ordinal")
);
CREATE INDEX "IX_ContentReference_Target" ON "ContentReference" ("Kind", "TargetId") WHERE "State" = 'linked';
CREATE INDEX "IX_ContentReference_TargetKey" ON "ContentReference" ("Kind", "TargetKey");

-- What is extracted: one row per source (a page with its comments, a board with its items and comments), the state it was read in
-- (Stamp) and the lexicon version of its place. A source whose stamp or place lexicon differs is extracted again by the sweep.
CREATE TABLE "ContentReferenceSource" (
  "SourceId" uuid NOT NULL CONSTRAINT "PK_ContentReferenceSource" PRIMARY KEY,
  "SpaceId" uuid NOT NULL,
  "Stamp" text NOT NULL,
  "LexiconVersion" text NOT NULL,
  "ExtractedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX "IX_ContentReferenceSource_Space" ON "ContentReferenceSource" ("SpaceId", "LexiconVersion");

-- „Není to on“ and „Převést na text“ of the overview and the MCP (owner decision R1: no mark in the document): the page, the kind and
-- the folded text are not offered again; once the text is another, it may be. Operational (BackupCoverage): a restore offers again.
CREATE TABLE "ContentReferenceDismissal" (
  "PageId" uuid NOT NULL REFERENCES "Page"("Id") ON DELETE CASCADE,
  "Kind" text NOT NULL,
  "TextKey" text NOT NULL,
  "DismissedById" uuid NOT NULL,
  "DismissedAt" timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT "PK_ContentReferenceDismissal" PRIMARY KEY ("PageId", "Kind", "TextKey")
);
