-- Atlas 2.0 S7b: wiki-level switch of the module "Návrhy při psaní" (rule-based editor suggestions; no model, no AI setting).
-- Temporal like the other module settings: one current row, closed intervals as history. No row means the default, Zapnuto.
CREATE TABLE "EditorAssistSetting" (
    "Id" uuid PRIMARY KEY,
    "Enabled" boolean NOT NULL,
    "ChangedById" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "CK_EditorAssistSetting_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_EditorAssistSetting_Interval" EXCLUDE USING gist
        (tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_EditorAssistSetting_Current" ON "EditorAssistSetting" ((true)) WHERE "ActiveTo" IS NULL;
