ALTER TABLE "PagePublication" ADD COLUMN "ChangeSummary" varchar(500) NULL;
ALTER TABLE "PagePublication" ADD CONSTRAINT "CK_PagePublication_ChangeSummary"
  CHECK ("ChangeSummary" IS NULL OR (length(btrim("ChangeSummary")) BETWEEN 1 AND 500 AND "ChangeSummary" !~ '[[:cntrl:]]'));
