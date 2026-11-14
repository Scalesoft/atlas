-- The re-authentication window (owner decision 2026-09-28): for ten minutes after a verified re-authentication, security changes of
-- the same account, session and security stamp need no new proof. Such a change records reauth.window_used with the challenge that
-- opened the window, in the change's transaction, so the audit still names the fresh sign-in behind every security change and tells
-- a covered change from one that consumed its own proof (reauth.consumed). Value releases never use the window. A sign-out of the
-- session ends it on the server (WindowClosedAt), also for a copy of the cookie that outlives the sign-out.
ALTER TABLE "ReauthChallenge" ADD COLUMN "WindowClosedAt" timestamptz NULL;
ALTER TABLE "ReauthChallenge" ADD CONSTRAINT "CK_ReauthChallenge_WindowClosed" CHECK ("WindowClosedAt" IS NULL OR "VerifiedAt" IS NOT NULL);

ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_ActionTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_ActionTarget" CHECK (
    ("AccountRevisionId" IS NOT NULL AND "Action" IN ('account.created','account.updated','credential.reset_requested','credential.password_changed','credential.activated','credential.recovered')) OR
    ("GroupRevisionId" IS NOT NULL AND "Action" IN ('group.created','group.renamed')) OR
    ("GroupMemberId" IS NOT NULL AND "Action" IN ('membership.added','membership.removed')) OR
    ("SpaceGrantId" IS NOT NULL AND "Action" IN ('grant.added','grant.removed')) OR
    ("ApiPermissionId" IS NOT NULL AND "Action" IN ('api_permission.enabled','api_permission.disabled')) OR
    ("ResourceGrantId" IS NOT NULL AND "Action" IN ('resource_grant.added','resource_grant.removed')) OR
    ("ProtectionSettingId" IS NOT NULL AND "Action" IN ('protection.set','protection.cleared')) OR
    ("TrustedAddressRevisionId" IS NOT NULL AND "Action" IN ('trusted_network.created','trusted_network.updated','trusted_network.removed',
        'trusted_proxy.created','trusted_proxy.updated','trusted_proxy.removed')) OR
    ("SecurityRevisionId" IS NOT NULL AND "Action" = 'security.proxy_hops_changed') OR
    ("ReauthChallengeId" IS NOT NULL AND "Action" IN ('reauth.consumed','reauth.window_used')) OR
    ("SecretReleaseEventId" IS NOT NULL AND "Action" IN ('secret.released','secret.release_denied','secret.client_report')) OR
    ("AccessRequestId" IS NOT NULL AND "Action" IN ('access_request.created','access_request.approved','access_request.rejected','access_request.cancelled')));

CREATE FUNCTION atlas_record_reauth_window_audit(challenge_id uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM "ReauthChallenge" WHERE "Id"=challenge_id AND "VerifiedAt" IS NOT NULL AND "Operation" <> 'selftest') THEN
        RAISE EXCEPTION 'Only a verified re-authentication opens a window' USING ERRCODE='23514';
    END IF;
    INSERT INTO "AccessAuditEvent"("ActorId","Action","ReauthChallengeId")
    VALUES(NULLIF(current_setting('scaleatlas.actor_id',true),'')::uuid,'reauth.window_used',challenge_id);
END $$;
