-- The tool steps of an agent turn (Atlas AI agent spec §4.1, WP-14): one jsonb array per answer, [{"tool":"atlas_get_page","ok":true}],
-- so a reopened conversation folds "Použité kroky". Only the tool name and whether it succeeded: never arguments, output or the
-- display names the live stream shows (a page title would outlive the reader's access). Only an answered message carries steps,
-- like its "Answer". Existing messages keep NULL (no steps): ADD COLUMN without a default rewrites nothing.
ALTER TABLE "AiMessage"
    ADD COLUMN "Steps" jsonb NULL,
    ADD CONSTRAINT "CK_AiMessage_Steps" CHECK ("Steps" IS NULL OR ("State" = 'answered' AND jsonb_typeof("Steps") = 'array'));
