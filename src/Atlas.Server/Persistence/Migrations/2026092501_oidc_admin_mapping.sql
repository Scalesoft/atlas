-- Insert-only children of an exact settings revision, matching OidcProviderScope.
CREATE TABLE "OidcAdminMapping" (
    "SettingsId" uuid NOT NULL,
    "ProviderId" varchar(40) NOT NULL,
    "ClaimType" varchar(200) NOT NULL,
    "ClaimValue" varchar(500) NOT NULL,
    CONSTRAINT "PK_OidcAdminMapping" PRIMARY KEY ("SettingsId", "ProviderId", "ClaimType", "ClaimValue"),
    CONSTRAINT "FK_OidcAdminMapping_Provider" FOREIGN KEY ("SettingsId", "ProviderId")
        REFERENCES "OidcProviderSettings" ("SettingsId", "ProviderId") ON DELETE NO ACTION,
    CONSTRAINT "CK_OidcAdminMapping_Type" CHECK (length(btrim("ClaimType")) > 0 AND "ClaimType" = btrim("ClaimType")),
    CONSTRAINT "CK_OidcAdminMapping_Value" CHECK (length(btrim("ClaimValue")) > 0 AND "ClaimValue" = btrim("ClaimValue"))
);
-- NULL preserves all existing manual grants. Effective IsWikiAdmin stays the authorization
-- read model used by existing SQL; the granting issuer refreshes its provenance at login.
ALTER TABLE "UserAccount" ADD COLUMN "OidcAdminIssuer" text NULL;
ALTER TABLE "UserAccount" ADD CONSTRAINT "CK_UserAccount_OidcAdmin"
    CHECK ("OidcAdminIssuer" IS NULL OR ("IsWikiAdmin" AND length("OidcAdminIssuer") > 0));
-- Forward-only: dropping provenance would turn automatic grants into permanent manual grants.
