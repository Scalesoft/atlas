-- Atlas 2.0 G2 (D9): normalized omnisearch queries that found nothing, for the space admin's "Nenalezené dotazy".
-- No user identity: "UserKey" is the first 16 bytes of HMAC-SHA256(secret, user id || space id || normalized query), so
-- distinct people of one query can be counted while one person's queries cannot be linked to each other or to an account.
-- The secret is 32 random bytes kept only data-protected ("ProtectedKey", ASP.NET Core data protection keyring outside the
-- database), so a database reader alone cannot recompute keys for known accounts. Time is kept to the day; one person counts
-- once per query and day. Neither table is part of wiki backups; rows older than 90 days are purged by SearchMissFlusher.
-- "SpaceId" NULL: a query without a space filter or context (owner decision O3, shown to WikiAdmins only from phase 4).

CREATE TABLE "SearchQueryLog" (
  "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_SearchQueryLog" PRIMARY KEY,
  "SpaceId" uuid NULL CONSTRAINT "FK_SearchQueryLog_Space" REFERENCES "Space"("Id") ON DELETE CASCADE,
  "NormalizedQuery" varchar(200) NOT NULL,
  "DisplayQuery" varchar(200) NOT NULL,
  "UserKey" bytea NOT NULL CONSTRAINT "CK_SearchQueryLog_UserKey" CHECK (octet_length("UserKey")=16),
  "OccurredOn" date NOT NULL,
  CONSTRAINT "UX_SearchQueryLog_UserDay" UNIQUE ("UserKey","OccurredOn"));
CREATE INDEX "IX_SearchQueryLog_SpaceQuery" ON "SearchQueryLog"("SpaceId","NormalizedQuery","OccurredOn");
CREATE INDEX "IX_SearchQueryLog_OccurredOn" ON "SearchQueryLog"("OccurredOn");

CREATE TABLE "SearchQueryLogKey" (
  "Id" smallint CONSTRAINT "PK_SearchQueryLogKey" PRIMARY KEY CONSTRAINT "CK_SearchQueryLogKey_Single" CHECK ("Id"=1),
  "ProtectedKey" bytea NOT NULL);

CREATE TABLE "SearchGapResolution" (
  "SpaceId" uuid NOT NULL CONSTRAINT "FK_SearchGapResolution_Space" REFERENCES "Space"("Id") ON DELETE CASCADE,
  "NormalizedQuery" varchar(200) NOT NULL,
  "ResolvedAt" timestamptz NOT NULL,
  "ResolvedById" uuid NOT NULL CONSTRAINT "FK_SearchGapResolution_ResolvedBy" REFERENCES "UserAccount"("Id"),
  CONSTRAINT "PK_SearchGapResolution" PRIMARY KEY ("SpaceId","NormalizedQuery"));
CREATE INDEX "IX_SearchGapResolution_ResolvedAt" ON "SearchGapResolution"("ResolvedAt");
