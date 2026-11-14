CREATE TABLE "ServerRecoveryToken" (
 "Id" uuid PRIMARY KEY,
 "UserId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
 "TokenHash" bytea NOT NULL UNIQUE CHECK(octet_length("TokenHash")=32),
 "IssuedAt" timestamptz NOT NULL, "ExpiresAt" timestamptz NOT NULL,
 "ConsumedAt" timestamptz NULL,
 CHECK("ExpiresAt">"IssuedAt"), CHECK("ConsumedAt" IS NULL OR "ConsumedAt">="IssuedAt")
);
CREATE UNIQUE INDEX "UX_ServerRecoveryToken_Current" ON "ServerRecoveryToken"((true)) WHERE "ConsumedAt" IS NULL;
