-- The projects' short names (issue ID prefixes) by stable ID, so that the place lexicon recognises a typed issue key (PX2-1235)
-- without asking YouTrack. Existing configurations start empty and get their names on the next „Test spojení“ or the next YouTrack
-- read of a space's projects (StoreYouTrackProjectKeys); a save carries them forward to the new row.
ALTER TABLE "YouTrackConfiguration" ADD COLUMN "ProjectKeysJson" text NOT NULL DEFAULT '{}';
