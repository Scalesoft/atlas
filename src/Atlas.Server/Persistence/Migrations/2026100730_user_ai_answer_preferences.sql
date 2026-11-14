-- Atlas 2.0 phase 3 Q6: answer in search and default assistant scope, further columns of the U1 per-user preference row.
-- No row or an old row means the defaults: answer on demand ('ask') and the current space as scope ('context').
ALTER TABLE "UserAiPreference"
  ADD COLUMN "SearchAnswer" varchar(8) NOT NULL DEFAULT 'ask' CHECK ("SearchAnswer" IN ('ask','auto','never')),
  ADD COLUMN "AssistantScope" varchar(8) NOT NULL DEFAULT 'context' CHECK ("AssistantScope" IN ('context','wiki'));
