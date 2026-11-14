ALTER TABLE "HubIdentityState" ADD COLUMN "NextAttemptAt" timestamptz NULL,
    ADD COLUMN "FailureCount" integer NOT NULL DEFAULT 0,
    ADD CONSTRAINT "CK_HubIdentityState_NextAttempt" CHECK ("NextAttemptAt" IS NULL OR isfinite("NextAttemptAt")),
    ADD CONSTRAINT "CK_HubIdentityState_FailureCount" CHECK ("FailureCount" BETWEEN 0 AND 16);
CREATE INDEX "IX_HubIdentityState_NextAttempt" ON "HubIdentityState"("NextAttemptAt");
