-- Informational author-supplied marker, never authorization or proof of a provider call.
ALTER TABLE "PageRevision" ADD COLUMN "AiAssisted" boolean NOT NULL DEFAULT false;
