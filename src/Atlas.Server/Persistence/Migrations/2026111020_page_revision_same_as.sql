-- Atlas 3.6: a version that ends exactly where it started (a character typed and deleted again) is no version. Its newest revision names
-- the listed revision it equals (SavePageHandler) and the history leaves it out. Existing revisions keep NULL: their versions stay listed.
ALTER TABLE "PageRevision" ADD COLUMN "SameAsRevisionId" uuid NULL;
