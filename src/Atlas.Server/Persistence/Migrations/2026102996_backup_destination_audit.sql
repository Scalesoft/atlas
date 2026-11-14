-- SEC1-2: where a backup goes is a security change. A backup holds every private note and personal board in plain form by design
-- (a keyless restore, owner rule), so whoever points it elsewhere reads them: the change needs a fresh sign-in (ISecurityChangeGuard,
-- kind backup.destination) and leaves an access audit event with the new destination. Forward only: no actor is invented for the
-- destinations configured before.
CREATE TABLE "BackupDestinationChange" (
    "Id" bigint GENERATED ALWAYS AS IDENTITY CONSTRAINT "PK_BackupDestinationChange" PRIMARY KEY,
    "ActorId" uuid NOT NULL CONSTRAINT "FK_BackupDestinationChange_Actor" REFERENCES "UserAccount"("Id") ON DELETE RESTRICT,
    -- git.remote: the repository URL; git.key: a new deploy key (its public half); archive.path: the ZIP backups' folder.
    "Kind" varchar(16) NOT NULL CONSTRAINT "CK_BackupDestinationChange_Kind" CHECK ("Kind" IN ('git.remote','git.key','archive.path')),
    "Destination" varchar(2000) NOT NULL,
    "OccurredAt" timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE TRIGGER "TR_BackupDestinationChange_Immutable" BEFORE UPDATE OR DELETE ON "BackupDestinationChange"
    FOR EACH ROW EXECUTE FUNCTION atlas_access_audit_immutable();

ALTER TABLE "AccessAuditEvent" ADD COLUMN "BackupDestinationChangeId" bigint NULL
    CONSTRAINT "FK_AccessAuditEvent_BackupDestinationChange" REFERENCES "BackupDestinationChange"("Id") ON DELETE RESTRICT;
CREATE UNIQUE INDEX "UX_AccessAuditEvent_BackupDestinationChange" ON "AccessAuditEvent"("BackupDestinationChangeId")
    WHERE "BackupDestinationChangeId" IS NOT NULL;

-- Full definitions: 2026102975 plus the backup destination.
ALTER TABLE "AccessAuditEvent" DROP CONSTRAINT "CK_AccessAuditEvent_OneTarget";
ALTER TABLE "AccessAuditEvent" ADD CONSTRAINT "CK_AccessAuditEvent_OneTarget" CHECK
    (num_nonnulls("AccountRevisionId","GroupRevisionId","GroupMemberId","SpaceGrantId","ApiPermissionId","ResourceGrantId",
        "ProtectionSettingId","TrustedAddressRevisionId","SecurityRevisionId","ReauthChallengeId",
        "SecretReleaseEventId","AccessRequestId","HubChangeId","ApiActivityId","McpSettingId","GroupAiPolicyId","UserAiPolicyId",
        "ObjectPurgeEventId","BackupDestinationChangeId")=1);
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
    ("BackupDestinationChangeId" IS NOT NULL AND "Action" = 'backup.destination_changed'));

CREATE FUNCTION atlas_audit_backup_destination() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO "AccessAuditEvent"("ActorId","Action","BackupDestinationChangeId") VALUES(NEW."ActorId",'backup.destination_changed',NEW."Id");
    RETURN NEW;
END $$;
CREATE TRIGGER "TR_BackupDestinationChange_Audit" AFTER INSERT ON "BackupDestinationChange"
    FOR EACH ROW EXECUTE FUNCTION atlas_audit_backup_destination();
