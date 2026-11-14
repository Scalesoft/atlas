-- Optional shared appearance has no historical-query requirement: one mutable row per page.
-- A reset keeps RevisionId so a previous no-row token cannot succeed after a set/reset cycle.
CREATE TABLE "PageDecoration" (
  "PageId" uuid NOT NULL CONSTRAINT "PK_PageDecoration" PRIMARY KEY,
  "RevisionId" uuid NOT NULL,
  "Icon" varchar(32) NULL,
  "Color" varchar(16) NULL,
  CONSTRAINT "FK_PageDecoration_Page" FOREIGN KEY ("PageId") REFERENCES "Page"("Id") ON DELETE RESTRICT,
  CONSTRAINT "CK_PageDecoration_Selection" CHECK (
    ("Icon" IS NULL AND "Color" IS NULL)
    OR ("Icon" IS NOT NULL AND "Color" IS NOT NULL
      AND "Icon" IN ('folder','book-open','file-text','home','star','compass','users','user',
        'code-xml','square-code','database','box','lightbulb','shield-check','lock','monitor',
        'settings-2','list-checks','bar-chart-3','line-chart','image','link','megaphone','clock')
      AND "Color" IN ('slate','blue','teal','green','amber','orange','rose','violet'))
  )
);
