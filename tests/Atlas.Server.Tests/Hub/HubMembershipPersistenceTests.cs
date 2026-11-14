using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Tests.Platform;
using NHibernate.Exceptions;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Hub;

public sealed class HubMembershipPersistenceTests
{
    [PostgreSqlFact]
    public async Task Expiry_without_a_worker_removes_external_rights_but_keeps_the_local_path()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_expiry_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var fixture = await SeedAsync(host);
        await AssertAccessAsync(host, fixture, 2);
        // Move the successful observation into the past; no worker, access-change commit, or clock delay is needed.
        await host.ExecuteAsync("""
            UPDATE "HubIdentityState" SET "SnapshotStartedAt"=statement_timestamp()-interval '6 minutes',
                "VerifiedUntil"=statement_timestamp()-interval '1 minute' WHERE "ExternalIdentityId"=:id
            """, fixture.Identity);
        await AssertAccessAsync(host, fixture, 1);

        using var check = host.OpenSession();
        Assert.Equal(2, await check.CreateSQLQuery("SELECT CAST(count(*) AS int) FROM \"GroupMember\" WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL")
            .SetParameter("id", fixture.Member.Id).UniqueResultAsync<int>());
    }

    [PostgreSqlFact]
    public async Task Configuration_changes_require_a_new_observation_and_disabled_bindings_cannot_become_local()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_modes_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var fixture = await SeedAsync(host);
        await ChangeModeAsync(host, fixture, 1);
        await AssertAccessAsync(host, fixture, 1);
        await ChangeModeAsync(host, fixture, 0);
        await AssertAccessAsync(host, fixture, 1);
        await ChangeModeAsync(host, fixture, 2);
        // Returning to enforced cannot resurrect the observation made under the old configuration.
        await AssertAccessAsync(host, fixture, 1);
        await host.ExecuteAsync("""
            UPDATE "HubIdentityState" SET "SnapshotStartedAt"=statement_timestamp(),
                "VerifiedUntil"=statement_timestamp()+interval '5 minutes',"Generation"="Generation"+1 WHERE "ExternalIdentityId"=:id
            """, fixture.Identity);
        await AssertAccessAsync(host, fixture, 2);
        await BindingAsync(host, fixture.ExternalGroup, binding => binding.Enabled = false);
        await AssertAccessAsync(host, fixture, 1);
        using var session = host.OpenSession();
        Assert.Equal(2, await session.CreateSQLQuery("SELECT CAST(count(*) AS int) FROM \"HubGroupBindingRevision\" WHERE \"GroupId\"=:id")
            .SetParameter("id", fixture.ExternalGroup).UniqueResultAsync<int>());
        await RefusedAsync(host, "Disable a Hub binding; deleting it would turn external membership into local membership",
            async s => await s.DeleteAsync(await s.GetAsync<HubGroupBinding>(fixture.ExternalGroup)));
        await RefusedAsync(host, "A Hub binding cannot change identity",
            async s => (await s.GetAsync<HubGroupBinding>(fixture.ExternalGroup))!.ExternalGroupId = "replacement");
        await AssertAccessAsync(host, fixture, 1);
    }

    [PostgreSqlFact]
    public async Task Disabled_upstream_and_closed_identity_remove_external_rights_and_invalid_leases_are_rejected()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_identity_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var fixture = await SeedAsync(host);
        await host.ExecuteAsync("UPDATE \"HubIdentityState\" SET \"UpstreamAccountState\"=2 WHERE \"ExternalIdentityId\"=:id", fixture.Identity);
        await AssertAccessAsync(host, fixture, 1);
        await host.ExecuteAsync("UPDATE \"HubIdentityState\" SET \"UpstreamAccountState\"=1 WHERE \"ExternalIdentityId\"=:id", fixture.Identity);
        await AssertAccessAsync(host, fixture, 2);
        await ConstraintAsync(() => host.ExecuteAsync("UPDATE \"HubIdentityState\" SET \"VerifiedUntil\"=\"SnapshotStartedAt\"+interval '6 minutes' WHERE \"ExternalIdentityId\"=:id", fixture.Identity));
        await RefusedAsync(host, "Hub identity is stable and generation cannot decrease",
            async s => (await s.GetAsync<HubIdentityState>(fixture.Identity))!.Generation = 0);
        await host.ExecuteAsync("UPDATE \"ExternalIdentity\" SET \"ActiveTo\"=statement_timestamp() WHERE \"Id\"=:id", fixture.Identity);
        await AssertAccessAsync(host, fixture, 1);
    }

    private static Task ChangeModeAsync(AtlasTestHost host, Fixture fixture, int mode) => host.SeedAsync(async session =>
    {
        await session.CreateSQLQuery("""
            WITH closed AS (
                UPDATE "HubConnectionRevision" SET "ActiveTo"=statement_timestamp() WHERE "ConnectionId"=:connection AND "ActiveTo" IS NULL
                RETURNING *
            )
            INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
            SELECT gen_random_uuid(),"ConnectionId","Issuer","BaseUrl","ProtectedToken",:mode,"ChangedById","ActiveTo" FROM closed
            """).SetParameter("connection", fixture.Connection).SetParameter("mode", mode).ExecuteUpdateAsync();
    });

    private static async Task<Fixture> SeedAsync(AtlasTestHost host)
    {
        var member = await host.SeedUserAsync("member");
        var externalGroup = await host.SeedGroupAsync("Hub writers", member);
        var localGroup = await host.SeedGroupAsync("Local readers", member);
        var space = await host.SeedSpaceAsync("Docs");
        var page = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var identity = Guid.NewGuid();
        await host.SeedAsync(async session =>
        {
            await session.SaveAsync(new Page { Id = page, SpaceId = space, OwnerId = member.Id });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, GroupId = externalGroup, Role = 2 });
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, GroupId = localGroup, Role = 1 });
            await session.FlushAsync();
            await session.CreateSQLQuery("""
                INSERT INTO "HubConnection"("Id") VALUES (:connection);
                INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
                VALUES (gen_random_uuid(),:connection,'https://hub.example.test/hub','https://hub.example.test/hub','protected-test-value',2,:user,statement_timestamp()-interval '1 hour');
                INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
                VALUES (:identity,:user,'https://hub.example.test/hub','opaque-user',statement_timestamp()-interval '1 hour');
                INSERT INTO "HubIdentityState"("ExternalIdentityId","UserId","ConnectionId","ExternalUserId","SnapshotStartedAt","VerifiedUntil","LastSuccessAt","LastAttemptAt","UpstreamAccountState","Generation")
                VALUES (:identity,:user,:connection,'opaque-user',statement_timestamp(),statement_timestamp()+interval '5 minutes',statement_timestamp(),statement_timestamp(),1,1);
                """)
                .SetParameter("connection", connection).SetParameter("user", member.Id)
                .SetParameter("identity", identity).ExecuteUpdateAsync();
        });
        // The binding as the application writes it: with its history (HubHistory).
        await BindingAsync(host, externalGroup, binding =>
        {
            binding.GroupId = externalGroup;
            binding.ConnectionId = connection;
            binding.ExternalGroupId = "opaque-group";
            binding.Enabled = true;
        });

        return new Fixture(member, page, space, externalGroup, localGroup, connection, identity);
    }

    private static async Task AssertAccessAsync(AtlasTestHost host, Fixture fixture, int expectedRole)
    {
        var (member, page, space, externalGroup, localGroup, _, _) = fixture;
        using var session = host.OpenSession();
        var memberships = await session.QueryOver<GroupMembership>().Where(x => x.UserId == member.Id && x.ActiveTo == null).ListAsync();
        Assert.Contains(memberships, x => x.GroupId == localGroup);
        Assert.Null(memberships.Single(x => x.GroupId == localGroup).VerifiedUntil);
        Assert.Equal(expectedRole == 2, memberships.Any(x => x.GroupId == externalGroup));
        if (expectedRole == 2) Assert.NotNull(memberships.Single(x => x.GroupId == externalGroup).VerifiedUntil);
        Assert.Equal((SpaceRole)expectedRole, await new SpaceRoles(session).EffectiveAsync(member, space, CancellationToken.None));
        Assert.Equal(expectedRole, await session.CreateSQLQuery("SELECT \"Role\" FROM \"EffectivePageAccess\" WHERE \"UserId\"=:user AND \"PageId\"=:page")
            .SetParameter("user", member.Id).SetParameter("page", page).UniqueResultAsync<int>());
    }

    private sealed record Fixture(UserAccount Member, Guid Page, Guid Space, Guid ExternalGroup, Guid LocalGroup, Guid Connection, Guid Identity);

    /// <summary>Creates or changes the group's binding through the session, with its history (HubHistory, null actor).</summary>
    private static Task BindingAsync(AtlasTestHost host, Guid group, Action<HubGroupBinding> change) => host.SeedAsync(async session =>
    {
        var binding = await session.GetAsync<HubGroupBinding>(group);
        if (binding is null)
        {
            binding = new HubGroupBinding();
            change(binding);
            await session.SaveAsync(binding);
        }
        else
        {
            change(binding);
        }
        await HubHistory.BindingAsync(session, binding, null, CancellationToken.None);
    });

    /// <summary>A write through the session that IntegrityRules refuses (C# since 3.6, the identity triggers before).</summary>
    private static async Task RefusedAsync(AtlasTestHost host, string message, Func<NHibernate.ISession, Task> change)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.SeedAsync(async session =>
        {
            await change(session);
            await session.FlushAsync();
        }));
        Assert.Equal(message, error.Message);
    }

    private static async Task ConstraintAsync(Func<Task> change)
    {
        var error = await Assert.ThrowsAsync<GenericADOException>(change);
        Assert.Equal("23514", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
}
