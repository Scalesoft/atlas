-- SEC-A-1, SEC-T-3: who can sign in, and who can open a backup, are security changes. Saving the OIDC providers (issuers,
-- registrations, sign-up, the wiki administrator rules) needs a fresh sign-in and leaves the access audit event
-- oidc.configuration_changed of the saved settings revision; issuing a backup key, which opens every vault password in a backup
-- (docs/git-backup.md), is the backup change backup.key. Forward only: no actor is invented for the settings saved before.
ALTER TABLE "BackupDestinationChange" DROP CONSTRAINT "CK_BackupDestinationChange_Kind";
ALTER TABLE "BackupDestinationChange" ADD CONSTRAINT "CK_BackupDestinationChange_Kind"
    CHECK ("Kind" IN ('git.remote','git.key','archive.path','archive.complete','archive.download','backup.key'));

ALTER TABLE "AccessAuditEvent" ADD COLUMN "OidcSettingsRevisionId" uuid NULL
    CONSTRAINT "FK_AccessAuditEvent_OidcSettingsRevision" REFERENCES "OidcSettingsRevision"("Id") ON DELETE RESTRICT;
CREATE UNIQUE INDEX "UX_AccessAuditEvent_OidcSettingsRevision" ON "AccessAuditEvent"("OidcSettingsRevisionId")
    WHERE "OidcSettingsRevisionId" IS NOT NULL;

-- Full definitions: 2026102996 and 2026103010 plus the OIDC settings.
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_OneTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_OneTarget" CHECK
    (num_nonnulls("AccountRevisionId","GroupRevisionId","GroupMemberId","SpaceGrantId","ApiPermissionId","ResourceGrantId",
        "ProtectionSettingId","TrustedAddressRevisionId","SecurityRevisionId","ReauthChallengeId",
        "SecretReleaseEventId","AccessRequestId","HubChangeId","ApiActivityId","McpSettingId","GroupAiPolicyId","UserAiPolicyId",
        "ObjectPurgeEventId","BackupDestinationChangeId","OidcSettingsRevisionId")=1);
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_ActionTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_ActionTarget" CHECK (
    ("AccountRevisionId" IS NOT NULL AND "Action" IN ('account.created','account.updated','credential.reset_requested','credential.password_changed','credential.activated','credential.recovered')) OR
    ("GroupRevisionId" IS NOT NULL AND "Action" IN ('group.created','group.renamed')) OR
    ("GroupMemberId" IS NOT NULL AND "Action" IN ('membership.added','membership.removed')) OR
    ("SpaceGrantId" IS NOT NULL AND "Action" IN ('grant.added','grant.removed')) OR
    ("ApiPermissionId" IS NOT NULL AND "Action" IN ('api_permission.enabled','api_permission.disabled','api_permission.changed')) OR
    ("ResourceGrantId" IS NOT NULL AND "Action" IN ('resource_grant.added','resource_grant.removed')) OR
    ("ProtectionSettingId" IS NOT NULL AND "Action" IN ('protection.set','protection.cleared')) OR
    ("TrustedAddressRevisionId" IS NOT NULL AND "Action" IN ('trusted_network.created','trusted_network.updated','trusted_network.removed',
        'trusted_proxy.created','trusted_proxy.updated','trusted_proxy.removed')) OR
    ("SecurityRevisionId" IS NOT NULL AND "Action" = 'security.proxy_hops_changed') OR
    ("ReauthChallengeId" IS NOT NULL AND "Action" IN ('reauth.consumed','reauth.window_used')) OR
    ("SecretReleaseEventId" IS NOT NULL AND "Action" IN ('secret.released','secret.release_denied','secret.client_report')) OR
    ("AccessRequestId" IS NOT NULL AND "Action" IN ('access_request.created','access_request.approved','access_request.rejected','access_request.cancelled')) OR
    ("HubChangeId" IS NOT NULL AND "Action" IN ('hub.connection.changed','hub.binding.changed','hub.identity.changed')) OR
    ("ApiActivityId" IS NOT NULL AND "Action" IN ('api.write','mcp.write','mcp.publish','assistant.write','assistant.publish')) OR
    ("McpSettingId" IS NOT NULL AND "Action" IN ('mcp.enabled','mcp.disabled')) OR
    ("GroupAiPolicyId" IS NOT NULL AND "Action" = 'ai.group_policy.changed') OR
    ("UserAiPolicyId" IS NOT NULL AND "Action" = 'ai.user_policy.changed') OR
    ("ObjectPurgeEventId" IS NOT NULL AND "Action" IN ('object.purged','page.purged','space.purged')) OR
    ("BackupDestinationChangeId" IS NOT NULL AND "Action" IN ('backup.destination_changed','backup.downloaded')) OR
    ("OidcSettingsRevisionId" IS NOT NULL AND "Action" = 'oidc.configuration_changed'));

CREATE FUNCTION atlas_audit_oidc_settings() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO "AccessAuditEvent"("ActorId","Action","OidcSettingsRevisionId") VALUES(NEW."AuthorId",'oidc.configuration_changed',NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_OidcSettingsRevision_Audit" AFTER INSERT ON "OidcSettingsRevision"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_oidc_settings();
