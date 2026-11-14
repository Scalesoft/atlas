-- PER-S-8: the contact runs' primary keys by name, like the constraints around them (PK_/CK_/FK_), so a next change of one does not
-- depend on the name PostgreSQL chose. StartedById stays without a foreign key on purpose: a run whose admin is gone ends as failed
-- (ContactLinkRunner).
ALTER TABLE "ContactLinkRun" RENAME CONSTRAINT "ContactLinkRun_pkey" TO "PK_ContactLinkRun";
ALTER TABLE "ContactSplitRun" RENAME CONSTRAINT "ContactSplitRun_pkey" TO "PK_ContactSplitRun";
