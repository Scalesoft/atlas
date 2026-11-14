-- Atlas 2.0 G5 "Zahrnout prostor do AI" (D11 a): a space admin switches a space out of AI retrieval and embedding.
-- Temporal like the other settings: one current row per space, closed intervals as history, ActiveFrom is the CAS version.
-- No row means the default, Zahrnuto.
CREATE TABLE "SpaceAiSetting" (
    "Id" uuid PRIMARY KEY,
    "SpaceId" uuid NOT NULL REFERENCES "Space"("Id") ON DELETE CASCADE,
    "Included" boolean NOT NULL,
    "ChangedById" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "CK_SpaceAiSetting_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_SpaceAiSetting_Interval" EXCLUDE USING gist
        ("SpaceId" WITH =, tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_SpaceAiSetting_Current" ON "SpaceAiSetting"("SpaceId") WHERE "ActiveTo" IS NULL;

-- The one definition every read over the AI index filters by (search, related pages, suggestions, the indexer's
-- eligibility). It reads the current row in the caller's snapshot, so a switch takes effect at the next query; deleting
-- the space's vectors is follow-up work of the index worker. Ordinary search and everything without AI ignore it.
CREATE FUNCTION atlas_space_in_ai(space uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT NOT EXISTS (
        SELECT 1 FROM "SpaceAiSetting" s
        WHERE s."SpaceId" = space AND s."ActiveTo" IS NULL AND NOT s."Included")
$$;
