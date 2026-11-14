CREATE TABLE "OidcSettingsRevision" (
    "Id" uuid PRIMARY KEY,
    "PublicOrigin" text NOT NULL,
    "AuthorId" uuid NOT NULL REFERENCES "UserAccount"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    CHECK ("ActiveTo" IS NULL OR "ActiveTo" > "ActiveFrom")
);
CREATE UNIQUE INDEX "UX_OidcSettingsRevision_Current" ON "OidcSettingsRevision" ((true)) WHERE "ActiveTo" IS NULL;
ALTER TABLE "OidcSettingsRevision" ADD CONSTRAINT "EX_OidcSettingsRevision_Validity" EXCLUDE USING gist (tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&);
CREATE TABLE "OidcProviderSettings" (
    "SettingsId" uuid NOT NULL REFERENCES "OidcSettingsRevision"("Id"),
    "ProviderId" varchar(40) NOT NULL,
    "DisplayName" varchar(200) NOT NULL,
    "Issuer" text NOT NULL,
    "ClientId" text NOT NULL,
    "ProtectedSecret" text NULL,
    "AllowSignup" boolean NOT NULL,
    PRIMARY KEY ("SettingsId", "ProviderId"),
    UNIQUE ("SettingsId", "Issuer")
);
CREATE TABLE "OidcProviderScope" (
    "SettingsId" uuid NOT NULL,
    "ProviderId" varchar(40) NOT NULL,
    "Scope" varchar(200) NOT NULL,
    PRIMARY KEY ("SettingsId", "ProviderId", "Scope"),
    FOREIGN KEY ("SettingsId", "ProviderId") REFERENCES "OidcProviderSettings"("SettingsId", "ProviderId")
);
