-- Atlas 2.0 phase 3 Q5: the thumb under an answer (plan phase 3 §9; UX design §5 "zpětná vazba"). One row per answer message:
-- rating, an optional reason of a thumb down and an optional private note. No user, no question, no answer text and no page:
-- the only link to a person is the message, and a deleted conversation removes it (ON DELETE SET NULL), so what stays is
-- anonymous operating data for the quality overview of phase 4 (task, prompt version, profile revision, rating, reason).
--
-- The note is the only free text. It is private (an administrator sees aggregates without identity only, D9) and goes away with
-- the message (O20): a BEFORE DELETE trigger on "AiMessage" clears it, so every path that deletes a message (one conversation,
-- all of them, retention, cascade of a deleted user) scrubs it without each caller having to remember.
--
-- No backfill: an upgrade gets an empty table.

CREATE TABLE "AiFeedback" (
  "Id" uuid PRIMARY KEY,
  "MessageId" uuid NULL CONSTRAINT "FK_AiFeedback_Message" REFERENCES "AiMessage"("Id") ON DELETE SET NULL,
  "Task" varchar(48) NOT NULL,
  "PromptVersion" varchar(32) NULL,
  "ProfileRevisionId" uuid NULL CONSTRAINT "FK_AiFeedback_Profile" REFERENCES "AiProfileRevision"("Id"),
  "Rating" varchar(4) NOT NULL CONSTRAINT "CK_AiFeedback_Rating" CHECK ("Rating" IN ('up','down')),
  "Reason" varchar(16) NULL CONSTRAINT "CK_AiFeedback_Reason"
    CHECK ("Reason" IN ('inaccurate','missing_info','wrong_sources','unsuitable','other')),
  "Note" varchar(500) NULL,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL,
  -- A thumb up carries neither a reason nor a note.
  CONSTRAINT "CK_AiFeedback_Up" CHECK ("Rating" = 'down' OR ("Reason" IS NULL AND "Note" IS NULL)));
CREATE UNIQUE INDEX "UX_AiFeedback_Message" ON "AiFeedback"("MessageId") WHERE "MessageId" IS NOT NULL;
CREATE INDEX "IX_AiFeedback_Task" ON "AiFeedback"("Task","CreatedAt");
CREATE INDEX "IX_AiFeedback_Profile" ON "AiFeedback"("ProfileRevisionId") WHERE "ProfileRevisionId" IS NOT NULL;

CREATE FUNCTION atlas_ai_feedback_scrub_note() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  UPDATE "AiFeedback" SET "Note" = NULL WHERE "MessageId" = OLD."Id" AND "Note" IS NOT NULL;
  RETURN OLD;
END
$$;
CREATE TRIGGER "TR_AiMessage_ScrubFeedbackNote" BEFORE DELETE ON "AiMessage"
  FOR EACH ROW EXECUTE FUNCTION atlas_ai_feedback_scrub_note();
