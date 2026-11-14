-- The link migration's runs become runs of the job contacts.link (3.3 plan §7 wave 2): a finished run is succeeded (done), failed or
-- cancelled, the one still running is queued again and goes on from its checkpoint (JobRun.Progress: the cursor, the identities its
-- first round decided, the report; no committed batch yet is the cursor of the empty Guid, as the job starts). The runner publishes a queued row after the start. ContactLinkRun stays one release for reading
-- and nothing writes it any more; the release after drops it.
INSERT INTO "JobRun"("JobId", "Trigger", "Reason", "RequestedBy", "State", "Attempt", "QueuedAt", "StartedAt", "FinishedAt", "Progress", "Summary", "Error")
SELECT 'contacts.link', 'manual', 'Převedeno z dřívějšího propojování kontaktů', r."StartedById",
  CASE r."State" WHEN 'done' THEN 'succeeded' WHEN 'running' THEN 'queued' ELSE r."State" END,
  CASE WHEN r."State" = 'running' THEN 0 ELSE 1 END,
  r."StartedAt",
  CASE WHEN r."State" <> 'running' THEN r."StartedAt" END,
  CASE WHEN r."State" <> 'running' THEN COALESCE(r."FinishedAt", r."StartedAt") END,
  jsonb_build_object('afterPageId', COALESCE(r."AfterPageId", '00000000-0000-0000-0000-000000000000'::uuid), 'pagesDone', r."PagesDone", 'revisions', r."Revisions", 'identities', r."Identities", 'report', r."Report"),
  jsonb_build_object('pages', r."Pages", 'pagesDone', r."PagesDone", 'revisions', r."Revisions",
    'linked', COALESCE(jsonb_array_length(NULLIF(r."Report"->'linked', 'null'::jsonb)), 0),
    'created', COALESCE(jsonb_array_length(NULLIF(r."Report"->'created', 'null'::jsonb)), 0),
    'unclear', COALESCE(jsonb_array_length(NULLIF(r."Report"->'unclear', 'null'::jsonb)), 0),
    'restricted', COALESCE(jsonb_array_length(NULLIF(r."Report"->'restricted', 'null'::jsonb)), 0)),
  CASE WHEN r."State" = 'failed' THEN 'Běh skončil jako selhaný ještě před verzí 3.3.' END
FROM "ContactLinkRun" r
ORDER BY r."StartedAt", r."Id";

COMMENT ON TABLE "ContactLinkRun" IS 'Read-only since 3.3: its rows were converted to JobRun (contacts.link); dropped in the release after.';
