-- Kdo co umí (docs/skills-design.md): a topic is a page tag. A tag merged into another keeps its row (its page tag history points to it)
-- and names the tag it went into; its name becomes an alias of that tag. New tables only: nothing existing changes.
ALTER TABLE "Tag" ADD COLUMN "MergedIntoId" uuid NULL CONSTRAINT "FK_Tag_MergedInto" REFERENCES "Tag"("Id") ON DELETE RESTRICT;
ALTER TABLE "Tag" ADD CONSTRAINT "CK_Tag_MergedInto" CHECK ("MergedIntoId" IS NULL OR "MergedIntoId" <> "Id");
CREATE INDEX "IX_Tag_MergedIntoId" ON "Tag"("MergedIntoId") WHERE "MergedIntoId" IS NOT NULL;

-- Another spelling of a topic („dotnet“ for „.NET“); a name resolves to its tag when a page is tagged or a skill is added.
CREATE TABLE "TagAlias" (
  "Id" uuid NOT NULL CONSTRAINT "PK_TagAlias" PRIMARY KEY,
  "TagId" uuid NOT NULL CONSTRAINT "FK_TagAlias_Tag" REFERENCES "Tag"("Id") ON DELETE CASCADE,
  "Name" varchar(48) NOT NULL,
  "NormalizedName" varchar(48) NOT NULL CONSTRAINT "UX_TagAlias_NormalizedName" UNIQUE,
  "CreatedAt" timestamptz NOT NULL,
  "CreatedById" uuid NOT NULL CONSTRAINT "FK_TagAlias_UserAccount" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
  CONSTRAINT "CK_TagAlias_Name" CHECK (length(btrim("Name")) BETWEEN 1 AND 48)
);
CREATE INDEX "IX_TagAlias_TagId" ON "TagAlias"("TagId");

-- C# is part of .NET: a topic and the broader one; who looks for .NET also finds the people who know C#.
CREATE TABLE "TagRelation" (
  "TagId" uuid NOT NULL CONSTRAINT "FK_TagRelation_Tag" REFERENCES "Tag"("Id") ON DELETE CASCADE,
  "ParentId" uuid NOT NULL CONSTRAINT "FK_TagRelation_Parent" REFERENCES "Tag"("Id") ON DELETE CASCADE,
  "CreatedAt" timestamptz NOT NULL,
  "CreatedById" uuid NOT NULL CONSTRAINT "FK_TagRelation_UserAccount" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
  CONSTRAINT "PK_TagRelation" PRIMARY KEY ("TagId","ParentId"),
  CONSTRAINT "CK_TagRelation_Self" CHECK ("TagId" <> "ParentId")
);
CREATE INDEX "IX_TagRelation_ParentId" ON "TagRelation"("ParentId");

-- One rater's rating of one person (a directory contact) in one topic. The rater whose own contact it is rates themself.
-- private: only the rater (and nobody else) sees it; shared: counts in the anonymous summary; signed: the summary and the rater's name.
CREATE TABLE "SkillRating" (
  "Id" uuid NOT NULL CONSTRAINT "PK_SkillRating" PRIMARY KEY,
  "ContactId" uuid NOT NULL CONSTRAINT "FK_SkillRating_Contact" REFERENCES "ContentResource"("Id") ON DELETE CASCADE,
  "TagId" uuid NOT NULL CONSTRAINT "FK_SkillRating_Tag" REFERENCES "Tag"("Id") ON DELETE RESTRICT,
  "RaterId" uuid NOT NULL CONSTRAINT "FK_SkillRating_Rater" REFERENCES "UserAccount"("Id") ON DELETE CASCADE,
  "Level" smallint NOT NULL CONSTRAINT "CK_SkillRating_Level" CHECK ("Level" BETWEEN 1 AND 5),
  "Visibility" text NOT NULL CONSTRAINT "CK_SkillRating_Visibility" CHECK ("Visibility" IN ('private','shared','signed')),
  "Note" varchar(200) NULL,
  "UpdatedAt" timestamptz NOT NULL,
  CONSTRAINT "UX_SkillRating" UNIQUE ("ContactId","TagId","RaterId")
);
CREATE INDEX "IX_SkillRating_Tag" ON "SkillRating"("TagId","ContactId");
CREATE INDEX "IX_SkillRating_Rater" ON "SkillRating"("RaterId");
