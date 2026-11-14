-- Atlas 2.0 phase 2 U1 (G4): per-user AI preferences, server side like UserAppearancePreference.
-- Temporal: one current row per user, closed intervals as history. No row means the defaults (suggestions on, no type disabled).
-- Phase 3 adds its preferences (answer in search, assistant scope) as further columns.
CREATE TABLE "UserAiPreference" (
    "Id" uuid PRIMARY KEY,
    "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "EditorSuggestions" varchar(8) NOT NULL DEFAULT 'on',
    "DisabledSuggestionTypes" jsonb NOT NULL DEFAULT '[]'::jsonb,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CONSTRAINT "CK_UserAiPreference_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "CK_UserAiPreference_EditorSuggestions" CHECK ("EditorSuggestions" IN ('on','rules','off')),
    CONSTRAINT "CK_UserAiPreference_DisabledTypes" CHECK (jsonb_typeof("DisabledSuggestionTypes") = 'array'),
    CONSTRAINT "EX_UserAiPreference_NoOverlap" EXCLUDE USING gist ("UserId" WITH =, tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_UserAiPreference_Current" ON "UserAiPreference"("UserId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_UserAiPreference_History" ON "UserAiPreference"("UserId", "ActiveFrom" DESC);
