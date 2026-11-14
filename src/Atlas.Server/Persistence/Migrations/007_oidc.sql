-- Interactive client state is short-lived operational data, not account history.
CREATE TABLE "OidcStateToken" (
    "Id" varchar(64) PRIMARY KEY, "Version" integer NOT NULL,
    "ApplicationId" text, "AuthorizationId" text,
    "CreationDate" timestamptz, "ExpirationDate" timestamptz, "RedemptionDate" timestamptz,
    "Payload" text, "Properties" text, "ReferenceId" text,
    "Status" text, "Subject" text, "Type" text
);
CREATE UNIQUE INDEX "UX_OidcStateToken_ReferenceId" ON "OidcStateToken" ("ReferenceId") WHERE "ReferenceId" IS NOT NULL;
CREATE INDEX "IX_OidcStateToken_ExpirationDate" ON "OidcStateToken" ("ExpirationDate");
CREATE TABLE "ExternalIdentity" (
    "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL REFERENCES "UserAccount" ("Id"),
    "Issuer" text NOT NULL, "Subject" text NOT NULL,
    "ActiveFrom" timestamptz NOT NULL, "ActiveTo" timestamptz,
    CONSTRAINT "CK_ExternalIdentity_Validity" CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_ExternalIdentity_Current" ON "ExternalIdentity" ("Issuer", "Subject") WHERE "ActiveTo" IS NULL;
CREATE INDEX "IX_ExternalIdentity_User" ON "ExternalIdentity" ("UserId") WHERE "ActiveTo" IS NULL;
