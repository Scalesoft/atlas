-- One inbox per account (inbox spec 2026-10-29): one row per recipient and aggregation key, identities only, decided against the
-- recipient's current rights when read. Operational (BackupCoverage): a restore starts with an empty inbox.
CREATE TABLE "Notification" (
  "Id" uuid NOT NULL CONSTRAINT "PK_Notification" PRIMARY KEY,
  "RecipientId" uuid NOT NULL CONSTRAINT "FK_Notification_Recipient" REFERENCES "UserAccount"("Id") ON DELETE CASCADE,
  "Kind" text NOT NULL,
  -- No foreign key, as PageObjectMention's object: a purged subject simply reads as missing.
  "SubjectId" uuid NOT NULL,
  "Anchor" text NULL,
  "AggregationKey" text NOT NULL,
  "ActorId" uuid NULL CONSTRAINT "FK_Notification_Actor" REFERENCES "UserAccount"("Id") ON DELETE SET NULL,
  "EventCount" integer NOT NULL DEFAULT 1,
  -- The outbox id of the last message counted: a repeated delivery (same or lower id, one key in order) counts nothing.
  "LastMessageId" bigint NOT NULL DEFAULT 0,
  "CreatedAt" timestamptz NOT NULL,
  "UpdatedAt" timestamptz NOT NULL,
  "ReadAt" timestamptz NULL,
  CONSTRAINT "UX_Notification_Aggregation" UNIQUE ("RecipientId","AggregationKey"));
CREATE INDEX "IX_Notification_Recipient" ON "Notification"("RecipientId","UpdatedAt" DESC,"Id" DESC);
CREATE INDEX "IX_Notification_Unread" ON "Notification"("RecipientId","UpdatedAt" DESC) WHERE "ReadAt" IS NULL;
CREATE INDEX "IX_Notification_Retention" ON "Notification"("UpdatedAt");

-- A kind the account switched off; no row is on. Backed up with the accounts' other settings.
CREATE TABLE "NotificationPreference" (
  "UserId" uuid NOT NULL CONSTRAINT "FK_NotificationPreference_User" REFERENCES "UserAccount"("Id") ON DELETE CASCADE,
  "Kind" text NOT NULL,
  CONSTRAINT "PK_NotificationPreference" PRIMARY KEY ("UserId","Kind"));

-- The accounts a page's publications have already told of their mention, so a republication tells only the newly mentioned.
-- Derived from the published revisions: rebuilt after a restore (and here, over the existing pages), so neither the upgrade nor a
-- restore tells anybody again.
CREATE TABLE "PageMentionNotice" (
  "PageId" uuid NOT NULL CONSTRAINT "FK_PageMentionNotice_Page" REFERENCES "Page"("Id") ON DELETE CASCADE,
  "UserId" uuid NOT NULL CONSTRAINT "FK_PageMentionNotice_User" REFERENCES "UserAccount"("Id") ON DELETE CASCADE,
  CONSTRAINT "PK_PageMentionNotice" PRIMARY KEY ("PageId","UserId"));

-- The accounts a rich document mentions: the internalUser of every contact snapshot (contactCard, contactInline), as the editor's
-- account mention writes it. Strict mode and silent errors as atlas_page_object_mentions; existing accounts only.
CREATE FUNCTION atlas_page_account_mentions(rich text) RETURNS SETOF uuid LANGUAGE sql STABLE AS $$
  SELECT DISTINCT u."Id"
  FROM jsonb_path_query(rich::jsonb->'document',
    'strict $.** ? (@.type == "contactCard" || @.type == "contactInline").attrs.data.contacts[*]', '{}'::jsonb, true) c
  JOIN "UserAccount" u ON jsonb_typeof(c->'internalUser'->'id')='string'
    AND c->'internalUser'->>'id' ~ '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$'
    AND u."Id"=lower(c->'internalUser'->>'id')::uuid
$$;

-- Every page's published mentions, trashed pages included (a page restored from the trash tells nobody again).
CREATE FUNCTION atlas_rebuild_page_mention_notices() RETURNS void LANGUAGE sql AS $$
  DELETE FROM "PageMentionNotice";
  INSERT INTO "PageMentionNotice"("PageId","UserId")
  SELECT p."Id",m FROM "Page" p JOIN "PageRevision" r ON r."Id"=p."PublishedRevisionId"
    CROSS JOIN LATERAL atlas_page_account_mentions(r."ContentJson") m;
$$;
SELECT atlas_rebuild_page_mention_notices();
