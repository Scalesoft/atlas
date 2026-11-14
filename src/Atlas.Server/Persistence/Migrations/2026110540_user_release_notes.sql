-- „Co je nového“ (Atlas 3.5 package K): the release whose notes an account has seen. The shell shows the notice once when a newer
-- release runs; an account without a row is new and takes the running release without the notice. Every account that exists now
-- has seen 3.4.3, the last release before the notes, so each of them gets the notice of the next release once.
CREATE TABLE "UserReleaseNotes" (
  "UserId" uuid PRIMARY KEY REFERENCES "UserAccount"("Id") ON DELETE CASCADE,
  "SeenVersion" text NOT NULL CONSTRAINT "CK_UserReleaseNotes_SeenVersion" CHECK ("SeenVersion" ~ '^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$')
);
INSERT INTO "UserReleaseNotes"("UserId","SeenVersion") SELECT "Id", '3.4.3' FROM "UserAccount";
