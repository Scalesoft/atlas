-- Proposals of agent writes (Atlas AI agent spec §6, WP-20): a write tool the agent called in a turn is not run; the call is stored
-- here with the preview the server built, and runs only after the owner of the conversation confirmed the chosen items.
-- One proposal per answer (MessageId), deleted with it (cascade from AiMessage and AiConversation: every deletion path and the
-- retention purge of conversations take it along). "Arguments" are exactly what the model sent (never anything from a client);
-- "Items" the preview cards (page, title, path, before → after), "Results" one entry per item once decided.
-- States: pending → applying (claimed, the tool runs outside the transaction) → applied | failed; pending → declined | expired |
-- superseded (a new message in the conversation). At most one pending proposal per conversation.
-- No backfill: an upgrade gets an empty table.
CREATE TABLE "AiToolProposal" (
  "Id" uuid PRIMARY KEY,
  "MessageId" uuid NOT NULL CONSTRAINT "FK_AiToolProposal_Message" REFERENCES "AiMessage"("Id") ON DELETE CASCADE,
  "ConversationId" uuid NOT NULL CONSTRAINT "FK_AiToolProposal_Conversation" REFERENCES "AiConversation"("Id") ON DELETE CASCADE,
  "Tool" varchar(64) NOT NULL CONSTRAINT "CK_AiToolProposal_Tool" CHECK ("Tool" ~ '^[a-z_]{3,64}$'),
  "Arguments" jsonb NOT NULL CONSTRAINT "CK_AiToolProposal_Arguments" CHECK (jsonb_typeof("Arguments") = 'object'),
  "Items" jsonb NOT NULL CONSTRAINT "CK_AiToolProposal_Items"
    CHECK (jsonb_typeof("Items") = 'array' AND jsonb_array_length("Items") BETWEEN 1 AND 100),
  "State" varchar(16) NOT NULL CONSTRAINT "CK_AiToolProposal_State"
    CHECK ("State" IN ('pending','applying','applied','failed','declined','expired','superseded')),
  "Results" jsonb NULL CONSTRAINT "CK_AiToolProposal_Results" CHECK ("Results" IS NULL OR jsonb_typeof("Results") = 'array'),
  "CreatedAt" timestamptz NOT NULL,
  "ExpiresAt" timestamptz NOT NULL,
  "DecidedAt" timestamptz NULL,
  CONSTRAINT "UX_AiToolProposal_Message" UNIQUE ("MessageId"),
  CONSTRAINT "CK_AiToolProposal_Expiry" CHECK ("ExpiresAt" > "CreatedAt"),
  CONSTRAINT "CK_AiToolProposal_Decided" CHECK (("State" = 'pending') = ("DecidedAt" IS NULL)),
  -- Only an executed proposal has results.
  CONSTRAINT "CK_AiToolProposal_Executed" CHECK (("State" IN ('applied','failed')) = ("Results" IS NOT NULL)));
-- One open proposal per conversation (spec §6.2); also serves superseding it at the next question.
CREATE UNIQUE INDEX "UX_AiToolProposal_Pending" ON "AiToolProposal"("ConversationId") WHERE "State" = 'pending';
CREATE INDEX "IX_AiToolProposal_Conversation" ON "AiToolProposal"("ConversationId");
