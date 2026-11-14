using Atlas.Server.Features.Hub.Directory;

namespace Atlas.Server.Features.Hub.Migration;

public sealed record HubMigrationGroup(Guid LocalGroupId, string ExternalGroupId);
public sealed record HubMigrationPageGrant(Guid GroupId, string Role);
public sealed record HubMigrationPage(Guid PageId, HubMigrationPageGrant[] Grants);
public sealed record PreviewHubMigration(string ExpectedVersion, HubMigrationGroup[] Groups, Guid[] RemoveSpaceGrantIds,
    Guid[] RemovePageGrantIds, Guid[] RemoveWikiAdminUserIds, HubMigrationPage[] PageBoundaries);
public sealed record ApplyHubMigration(string Ticket)
{
    public override string ToString() => "ApplyHubMigration { Ticket = [REDACTED] }";
}
public sealed record HubMigrationResult(int ConvertedGroups, int RemovedGrants, int RemovedWikiAdmins, int IsolatedPages);
public sealed record HubMigrationMemberDiff(Guid LocalGroupId, string ExternalGroupId, bool ConvertsLocalGroup, Guid[] BeforeUserIds, Guid[] AfterUserIds);
public sealed record HubMigrationRoute(Guid UserId, string ResourceKind, Guid? ResourceId, int Role, string Source, Guid? GrantId, Guid? GroupId);
public sealed record HubMigrationCopiedGrant(Guid PageId, Guid SourceSpaceGrantId, Guid? UserId, Guid? GroupId, int Role);
public sealed record HubMigrationObservedIdentity(Guid IdentityId, Guid UserId, string State);
public sealed record HubMigrationPreview(string Ticket, DateTime ExpiresAt, string ConfigurationVersion, HubMigrationInventory Inventory,
    HubMigrationMemberDiff[] GroupDiffs, HubMigrationGrant[] RemovedGrants, HubMigrationPage[] PageBoundaries,
    HubMigrationCopiedGrant[] CopiedGrantsRemoved, Guid[] RemovedWikiAdminUserIds, HubMigrationObservedIdentity[] ObservedIdentities,
    HubMigrationRoute[] BeforeRoutes, HubMigrationRoute[] AfterRoutes, string[] Warnings)
{
    public override string ToString() => "HubMigrationPreview { Ticket = [REDACTED] }";
}
public sealed record HubMigrationUser(Guid Id, string DisplayName, bool IsActive, bool IsWikiAdmin, string? OidcAdminIssuer, bool HasPassword);
public sealed record HubMigrationLocalGroup(Guid Id, string Name);
public sealed record HubMigrationMember(Guid Id, Guid GroupId, Guid UserId, DateTime ActiveFrom, DateTime? ActiveTo);
public sealed record HubMigrationGrant(Guid Id, Guid? SpaceId, Guid? PageId, Guid? UserId, Guid? GroupId, int Role,
    DateTime? StartAt, DateTime? ExpiresAt, DateTime ActiveFrom, DateTime? ActiveTo);
public sealed record HubMigrationPageState(Guid Id, Guid SpaceId, string Title, bool OwnAccess, bool IsTrashed, bool IsTemplate);
public sealed record HubMigrationSpace(Guid Id, string Name);
public sealed record HubMigrationIdentity(Guid Id, Guid UserId, string Issuer, string Subject, DateTime? ActiveTo);
public sealed record HubMigrationBinding(Guid GroupId, string ExternalGroupId, bool Enabled, bool GrantsWikiAdmin);
public sealed record HubMigrationInventory(HubMigrationUser[] Users, HubMigrationLocalGroup[] Groups, HubMigrationMember[] Members,
    HubMigrationMember[] EffectiveMembers, HubMigrationGrant[] Grants, HubMigrationPageState[] Pages,
    HubMigrationIdentity[] Identities, HubMigrationBinding[] Bindings, Guid[] EffectiveWikiAdmins, HubMigrationSpace[] Spaces);
public sealed record HubMigrationObservation(Guid IdentityId, Guid UserId, HubUserObservation User);
public sealed record HubMigrationTicket(Guid ActorId, string ActorStamp, Guid ConfigurationId, string Digest, DateTime StartedAt,
    PreviewHubMigration Request, HubMigrationObservation[] Observations)
{
    public override string ToString() => "HubMigrationTicket { ActorStamp = [REDACTED], Observations = [REDACTED] }";
}
public sealed record PrepareHubMigration(PreviewHubMigration Request);
public sealed record ReadHubMigrationInventory;
public sealed record PreparedHubMigration(HubMigrationTicket Ticket, HubMigrationInventory Inventory, string BaseUrl, string ProtectedToken)
{
    public override string ToString() => "PreparedHubMigration { ProtectedToken = [REDACTED], Ticket = [REDACTED] }";
}
