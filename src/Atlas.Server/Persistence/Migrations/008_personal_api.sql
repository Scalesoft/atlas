CREATE TABLE "UserApiPermission" (
    "Id" uuid PRIMARY KEY,
    "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "CanIssueTokens" boolean NOT NULL,
    "ChangedById" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    CONSTRAINT "CK_UserApiPermission_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "EX_UserApiPermission_Interval" EXCLUDE USING gist
        ("UserId" WITH =, tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&)
);
CREATE UNIQUE INDEX "UX_UserApiPermission_Current" ON "UserApiPermission"("UserId") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_UserApiPermission_History" ON "UserApiPermission"("UserId", "ActiveFrom" DESC);

-- Immutable credential metadata. Revocation closes its validity; secrets are never versioned.
CREATE TABLE "PersonalApiToken" (
    "Id" uuid PRIMARY KEY,
    "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "Name" varchar(100) NOT NULL,
    "TokenHash" varchar(64) NOT NULL UNIQUE,
    "Prefix" varchar(12) NOT NULL,
    "SecurityStamp" varchar(64) NOT NULL,
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz,
    "ExpiresAt" timestamptz NOT NULL,
    CONSTRAINT "CK_PersonalApiToken_Interval" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom"),
    CONSTRAINT "CK_PersonalApiToken_Expiry" CHECK ("ExpiresAt" > "ActiveFrom"),
    CONSTRAINT "CK_PersonalApiToken_Hash" CHECK ("TokenHash" ~ '^[0-9A-F]{64}$')
);
CREATE INDEX "IX_PersonalApiToken_CurrentUser" ON "PersonalApiToken"("UserId", "ExpiresAt") WHERE "ActiveTo" IS NULL;
CREATE TABLE "PersonalApiTokenScope" (
    "TokenId" uuid NOT NULL REFERENCES "PersonalApiToken"("Id"),
    "Scope" varchar(10) NOT NULL CHECK ("Scope" IN ('read', 'write')),
    PRIMARY KEY ("TokenId", "Scope")
);
