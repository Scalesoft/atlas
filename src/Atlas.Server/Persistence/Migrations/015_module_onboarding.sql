CREATE TABLE "ModuleOnboarding" (
    "Id" uuid PRIMARY KEY,
    "WorkspaceId" integer NOT NULL REFERENCES "Workspace"("Id"),
    "ActiveFrom" timestamptz NOT NULL,
    "ActiveTo" timestamptz NULL,
    "CompletedById" uuid NULL REFERENCES "UserAccount"("Id"),
    CHECK ("ActiveTo" IS NULL OR "ActiveTo">"ActiveFrom"),
    CHECK (("ActiveTo" IS NULL) = ("CompletedById" IS NULL))
);
CREATE UNIQUE INDEX "UX_ModuleOnboarding_Current" ON "ModuleOnboarding" ("WorkspaceId") WHERE "ActiveTo" IS NULL;
ALTER TABLE "ModuleOnboarding" ADD CONSTRAINT "EX_ModuleOnboarding_Validity" EXCLUDE USING gist ("WorkspaceId" WITH =, tstzrange("ActiveFrom", "ActiveTo", '[)') WITH &&);
-- Existing installations are already onboarded; only new setup transactions create a pending row.
