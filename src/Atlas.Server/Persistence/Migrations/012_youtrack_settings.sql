CREATE TABLE "YouTrackConfiguration" (
    "Id" uuid PRIMARY KEY,
    "Enabled" boolean NOT NULL,
    "BaseUrl" varchar(2048) NOT NULL,
    "ProtectedToken" text NOT NULL,
    "SpaceProjectsJson" text NOT NULL,
    "ChangedById" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "CK_YouTrackConfiguration_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_YouTrackConfiguration_Interval" EXCLUDE USING gist
        (tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_YouTrackConfiguration_Current" ON "YouTrackConfiguration" ((true)) WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_YouTrackConfiguration_History" ON "YouTrackConfiguration" ("ActiveFrom" DESC);
