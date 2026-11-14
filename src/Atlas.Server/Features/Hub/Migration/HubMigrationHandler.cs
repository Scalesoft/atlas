using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Hub.Directory;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Hub.Persistence;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Commands;
using Atlas.Server.Features.PageOrganization.Domain;
using Microsoft.AspNetCore.DataProtection;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Hub.Migration;

[Wolverine.Attributes.WolverineHandler]
public sealed class HubMigrationHandler(ISession session, AdministrationAccess access, IAtlasLocks locks, ISecurityChangeGuard guard,
    AtlasOidcOptions oidc, AccessChanges changes, IDataProtectionProvider protection, OrganizationLocks organizationLocks)
{
    public const string TicketPurpose = "ScaleAtlas.Hub.Migration.v1";

    public async Task<HubMigrationInventory> Handle(ReadHubMigrationInventory query, CancellationToken ct)
    {
        await access.LockAdministrationAsync(ct);
        var config = await RequireConfigurationAsync(ct);
        return (await HubMigrationSnapshot.ReadAsync(session, config, ct)).Inventory;
    }

    public async Task<PreparedHubMigration> Handle(PrepareHubMigration command, CancellationToken ct)
    {
        var actor = await access.LockAdministrationAsync(ct);
        var config = await RequireConfigurationAsync(ct);
        AdministrationVersion.Expect(config.ActiveFrom, command.Request.ExpectedVersion);
        var now = await HubReads.NowAsync(session, ct);
        var (inventory, digest) = await HubMigrationSnapshot.ReadAsync(session, config, ct);
        HubMigrationSnapshot.Validate(command.Request, inventory, now);
        await HubSettingsReader.RequireRecoveryAsync(session, oidc, ct);
        return new(new(actor.Id, actor.SecurityStamp, config.Id, digest, now, command.Request, []), inventory, config.BaseUrl, config.ProtectedToken);
    }

    public async Task<HubMigrationResult> Handle(ApplyHubMigration command, CancellationToken ct)
    {
        var actor = await access.LockAdministrationAsync(ct);
        var ticket = ReadTicket(command.Ticket);
        var config = await RequireConfigurationAsync(ct);
        var now = await HubReads.NowAsync(session, ct);
        RequireTicketContext(ticket, actor, config, now);
        AdministrationVersion.Expect(config.ActiveFrom, ticket.Request.ExpectedVersion);
        var (inventory, digest) = await HubMigrationSnapshot.ReadAsync(session, config, ct);
        if (!string.Equals(digest, ticket.Digest, StringComparison.Ordinal) || HubMigrationSnapshot.GrantDeadlineCrossed(inventory, ticket.StartedAt, now)) throw Conflict();
        HubMigrationSnapshot.Validate(ticket.Request, inventory, now);
        RequireCompleteObservations(ticket, inventory);
        await guard.RequireAsync(actor, new SecurityChange("hub.migration", config.ConnectionId), ct);
        await HubSettingsReader.RequireRecoveryAsync(session, oidc, ct);
        await locks.SetActorAsync(actor.Id, ct);
        foreach (var group in ticket.Request.Groups)
        {
            var binding = new HubGroupBinding { GroupId = group.LocalGroupId, ConnectionId = config.ConnectionId, ExternalGroupId = group.ExternalGroupId };
            await session.SaveAsync(binding, ct);
            await HubHistory.BindingAsync(session, binding, actor.Id, ct);
        }
        await ReplaceMembershipAsync(inventory, ticket, actor.Id, ct);
        foreach (var observation in ticket.Observations) await SaveObservationAsync(config, ticket.StartedAt, now, observation, actor.Id, ct);
        foreach (var id in ticket.Request.RemoveSpaceGrantIds) await CloseGrantAsync(AccessAuditLog.SpaceGrant, id, actor.Id, ct);
        foreach (var id in ticket.Request.RemovePageGrantIds) await CloseGrantAsync(AccessAuditLog.ResourceGrant, id, actor.Id, ct);
        foreach (var boundary in ticket.Request.PageBoundaries) await ApplyPageBoundaryAsync(boundary, actor.Id, ct);
        foreach (var id in ticket.Request.RemoveWikiAdminUserIds)
        {
            var user = await session.GetAsync<UserAccount>(id, ct);
            user.IsWikiAdmin = false;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            await AccountHistory.RecordAsync(session, user, actor.Id, ct);
        }
        await session.FlushAsync(ct);
        await HubSettingsReader.RequireRecoveryAsync(session, oidc, ct);
        var completedAt = await HubReads.NowAsync(session, ct);
        if (ticket.StartedAt.AddMinutes(5) <= completedAt || HubMigrationSnapshot.GrantDeadlineCrossed(inventory, ticket.StartedAt, completedAt)) throw Conflict();
        changes.Changed();
        return new(ticket.Request.Groups.Length, ticket.Request.RemoveSpaceGrantIds.Length + ticket.Request.RemovePageGrantIds.Length,
            ticket.Request.RemoveWikiAdminUserIds.Length, ticket.Request.PageBoundaries.Length);
    }

    private async Task ReplaceMembershipAsync(HubMigrationInventory inventory, HubMigrationTicket ticket, Guid actorId, CancellationToken ct)
    {
        foreach (var diff in HubMigrationSnapshot.MembershipDiffs(inventory, ticket))
        {
            // A conversion closes EVERY old interval, including local accounts with no Hub identity.
            var closed = await session.CreateSQLQuery("""
                UPDATE "GroupMember" SET "ActiveTo"=greatest(clock_timestamp(),"ActiveFrom"+interval '1 microsecond')
                WHERE "GroupId"=:group AND "ActiveTo" IS NULL
                RETURNING "Id"
                """).SetParameter("group", diff.LocalGroupId).ListAsync<Guid>(ct);
            await AccessAuditLog.ClosedAsync(session, AccessAuditLog.GroupMember, [.. closed], actorId, ct);
            foreach (var userId in diff.AfterUserIds)
            {
                var opened = await session.CreateSQLQuery("""
                    INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom")
                    SELECT gen_random_uuid(),:group,:user,greatest(clock_timestamp(),max("ActiveTo")+interval '1 microsecond')
                    FROM "GroupMember" WHERE "GroupId"=:group AND "UserId"=:user
                    RETURNING "Id"
                    """).SetParameter("group", diff.LocalGroupId).SetParameter("user", userId).ListAsync<Guid>(ct);
                await AccessAuditLog.OpenedAsync(session, AccessAuditLog.GroupMember, [.. opened], actorId, ct);
            }
        }
    }

    private async Task ApplyPageBoundaryAsync(HubMigrationPage boundary, Guid actorId, CancellationToken ct)
    {
        var version = await SetPageAccessModeHandler.CurrentVersionAsync(session, boundary.PageId, ct);
        await new SetPageAccessModeHandler(session, organizationLocks, changes).Handle(
            new SetPageAccessMode(boundary.PageId, new UpdatePageAccessMode(true, version is null ? null : AdministrationVersion.Format(version.Value))), ct);
        // Exact reviewed replacement also closes Reader/Writer defaults copied by the existing own-access transition.
        var closed = await session.CreateSQLQuery("""
            UPDATE "ResourceGrant" SET "ActiveTo"=greatest(clock_timestamp(),"ActiveFrom"+interval '1 microsecond')
            WHERE "PageId"=:page AND "ActiveTo" IS NULL
            RETURNING "Id"
            """).SetParameter("page", boundary.PageId).ListAsync<Guid>(ct);
        await AccessAuditLog.ClosedAsync(session, AccessAuditLog.ResourceGrant, [.. closed], actorId, ct);
        foreach (var grant in boundary.Grants)
        {
            var opened = await session.CreateSQLQuery("""
                INSERT INTO "ResourceGrant"("Id","PageId","GroupId","Role","ActiveFrom")
                SELECT gen_random_uuid(),:page,:group,:role,greatest(clock_timestamp(),max("ActiveTo")+interval '1 microsecond')
                FROM "ResourceGrant" WHERE "PageId"=:page AND "GroupId"=:group
                RETURNING "Id"
                """).SetParameter("page", boundary.PageId).SetParameter("group", grant.GroupId)
                .SetParameter("role", HubMigrationSnapshot.Role(grant.Role)).ListAsync<Guid>(ct);
            await AccessAuditLog.OpenedAsync(session, AccessAuditLog.ResourceGrant, [.. opened], actorId, ct);
        }
    }

    private async Task SaveObservationAsync(HubConnectionRevision config, DateTime startedAt, DateTime now, HubMigrationObservation observation,
        Guid actorId, CancellationToken ct)
    {
        var state = await session.GetAsync<HubIdentityState>(observation.IdentityId, ct);
        var before = state?.UpstreamAccountState;
        if (state is null)
        {
            // Closed identity links can leave the constrained one-per-user projection; never merge active links.
            var previous = await session.QueryOver<HubIdentityState>().Where(x => x.ConnectionId == config.ConnectionId &&
                (x.UserId == observation.UserId || x.ExternalUserId == observation.User.ExternalUserId)).ListAsync(ct);
            foreach (var old in previous)
            {
                var active = await session.CreateSQLQuery("SELECT count(*) FROM \"ExternalIdentity\" WHERE \"Id\"=:id AND \"ActiveTo\" IS NULL")
                    .SetParameter("id", old.ExternalIdentityId).UniqueResultAsync<long>(ct);
                if (active != 0) throw Conflict();
                await session.DeleteAsync(old, ct);
            }
            await session.FlushAsync(ct);
            state = new HubIdentityState { ExternalIdentityId = observation.IdentityId, UserId = observation.UserId,
                ConnectionId = config.ConnectionId, ExternalUserId = observation.User.ExternalUserId };
            await session.SaveAsync(state, ct);
        }
        if (state.ConnectionId != config.ConnectionId || state.UserId != observation.UserId || state.ExternalUserId != observation.User.ExternalUserId) throw Conflict();
        state.Generation = checked(state.Generation + 1);
        state.LastAttemptAt = startedAt;
        state.SnapshotStartedAt = startedAt;
        state.VerifiedUntil = startedAt.AddMinutes(5);
        state.LastSuccessAt = now;
        state.NextAttemptAt = startedAt.AddSeconds(60);
        state.FailureCount = 0;
        state.LastErrorCode = null;
        state.UpstreamAccountState = observation.User.State switch
        {
            HubDirectoryAccountState.Active => HubUpstreamAccountState.Active,
            HubDirectoryAccountState.Disabled => HubUpstreamAccountState.Disabled,
            _ => HubUpstreamAccountState.Missing
        };
        await HubHistory.IdentityAsync(session, state, before, actorId, ct);
    }

    private async Task CloseGrantAsync(string target, Guid id, Guid actorId, CancellationToken ct)
    {
        var closed = await session.CreateSQLQuery(target == AccessAuditLog.SpaceGrant
                ? """UPDATE "SpaceGrant" SET "ActiveTo"=greatest(clock_timestamp(),"ActiveFrom"+interval '1 microsecond') WHERE "Id"=:id AND "ActiveTo" IS NULL RETURNING "Id" """
                : """UPDATE "ResourceGrant" SET "ActiveTo"=greatest(clock_timestamp(),"ActiveFrom"+interval '1 microsecond') WHERE "Id"=:id AND "ActiveTo" IS NULL RETURNING "Id" """)
            .SetParameter("id", id).ListAsync<Guid>(ct);
        await AccessAuditLog.ClosedAsync(session, target, [.. closed], actorId, ct);
    }

    private static void RequireTicketContext(HubMigrationTicket ticket, UserAccount actor, HubConnectionRevision config, DateTime now)
    {
        if (ticket.ActorId != actor.Id || ticket.ActorStamp != actor.SecurityStamp || ticket.ConfigurationId != config.Id ||
            ticket.StartedAt < config.ActiveFrom || ticket.StartedAt > now || ticket.StartedAt.AddMinutes(5) <= now) throw Conflict();
    }

    private static void RequireCompleteObservations(HubMigrationTicket ticket, HubMigrationInventory inventory)
    {
        var identities = HubMigrationSnapshot.ActiveIdentities(inventory);
        if (ticket.Observations.Length != identities.Length || ticket.Observations.Select(x => x.IdentityId).Distinct().Count() != identities.Length ||
            ticket.Observations.Any(o => !identities.Any(i => i.Id == o.IdentityId && i.UserId == o.UserId && i.Subject == o.User.ExternalUserId))) throw Conflict();
    }

    private async Task<HubConnectionRevision> RequireConfigurationAsync(CancellationToken ct)
    {
        var config = await HubReads.CurrentAsync(session, ct);
        return config is null || config.Mode == HubConnectionMode.Disabled
            ? throw new ApiException(409, "Migrace vyžaduje připojení v Preview nebo Enforced.", "hub_migration_disabled") : config;
    }

    private HubMigrationTicket ReadTicket(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8 * 1024 * 1024) throw Conflict();
        try { return JsonSerializer.Deserialize<HubMigrationTicket>(protection.CreateProtector(TicketPurpose).Unprotect(value)) ?? throw Conflict(); }
        catch (CryptographicException) { throw Conflict(); }
        catch (JsonException) { throw Conflict(); }
    }

    private static ApiException Conflict() => new(409, "Náhled migrace již neplatí. Vytvořte nový náhled a zkontrolujte změny.", "hub_migration_stale");
}
