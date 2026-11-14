using Atlas.Server.Features.Hub.Commands;
using Atlas.Server.Features.Hub.Directory;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Hub;

public sealed class HubSyncTests
{
    [PostgreSqlFact]
    public async Task Upstream_rename_preserves_local_name_limits_and_separate_group_identity()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_rename_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (identity, _, group) = await SeedAsync(host);
        var local = await host.SeedGroupAsync(new string('x', 100));
        var attempt = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(attempt,
            new HubUserObservation("opaque-user", HubDirectoryAccountState.Active, [new("opaque-group", new string('x', 180))])));
        using var read = host.OpenSession();
        var name = (await read.GetAsync<Atlas.Server.Features.Administration.Domain.UserGroup>(group)).Name;
        Assert.EndsWith(" · Hub " + group.ToString("N"), name, StringComparison.Ordinal);
        Assert.True(name.Length <= 100);
        Assert.Equal(new string('x', 100), (await read.GetAsync<Atlas.Server.Features.Administration.Domain.UserGroup>(local)).Name);
    }

    [PostgreSqlFact]
    public async Task Complete_observations_version_membership_and_older_attempts_cannot_restore_it()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_sync_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (identity, member, group) = await SeedAsync(host);
        var first = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        var observation = new HubUserObservation("opaque-user", HubDirectoryAccountState.Active, [new("opaque-group", "Hub writers")]);
        Assert.Equal(new HubSyncResult(true, "verified", 1, 0), await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(first, observation)));
        var unchanged = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        Assert.Equal(new HubSyncResult(true, "verified", 0, 0), await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(unchanged, observation)));
        var replacement = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        Assert.Equal(new HubSyncResult(true, "verified", 0, 1), await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(replacement,
            observation with { Groups = [new("replacement-opaque-id", "Hub writers")] })));
        // Recreating a group with the same display name cannot inherit the old external ID's grants.
        using (var check = host.OpenSession())
            Assert.Empty(await check.QueryOver<GroupMembership>().Where(x => x.UserId == member.Id && x.GroupId == group).ListAsync());
        var restored = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(restored, observation));
        var older = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        var newer = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        Assert.True(newer.Generation > older.Generation);
        Assert.Equal(new HubSyncResult(true, "verified", 0, 1), await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(newer, observation with { Groups = [] })));
        Assert.False((await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(older, observation))).Applied);
        using var session = host.OpenSession();
        Assert.Empty(await session.QueryOver<GroupMembership>().Where(x => x.UserId == member.Id && x.GroupId == group).ListAsync());
        Assert.Equal(2, await session.CreateSQLQuery("SELECT CAST(count(*) AS int) FROM \"GroupMember\" WHERE \"GroupId\"=:id AND \"ActiveTo\" IS NOT NULL")
            .SetParameter("id", group).UniqueResultAsync<int>());
    }

    [PostgreSqlFact]
    public async Task Failed_reads_do_not_renew_a_lease_and_preview_does_not_write_membership()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_failure_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (identity, member, group) = await SeedAsync(host);
        var observation = new HubUserObservation("opaque-user", HubDirectoryAccountState.Active, [new("opaque-group", "Hub writers")]);
        var first = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(first, observation));
        var failed = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(failed, null, "invalid-response"));
        using (var check = host.OpenSession())
        {
            var until = await check.CreateSQLQuery("SELECT \"VerifiedUntil\" FROM \"HubIdentityState\" WHERE \"ExternalIdentityId\"=:id")
                .SetParameter("id", identity).UniqueResultAsync<DateTime>();
            Assert.Equal(first.StartedAt.AddMinutes(5), until);
        }
        await host.SeedAsync(async session =>
        {
            await session.CreateSQLQuery("""
                WITH previous AS (UPDATE "HubConnectionRevision" SET "ActiveTo"=statement_timestamp() WHERE "ActiveTo" IS NULL RETURNING *)
                INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
                SELECT gen_random_uuid(),"ConnectionId","Issuer","BaseUrl","ProtectedToken",1,"ChangedById","ActiveTo" FROM previous
                """).ExecuteUpdateAsync();
        });
        var preview = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        Assert.False((await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(first, observation))).Applied);
        Assert.Equal(new HubSyncResult(true, "preview", 0, 1), await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(preview, observation with { Groups = [] })));
        using var session = host.OpenSession();
        Assert.Equal(1, await session.CreateSQLQuery("SELECT CAST(count(*) AS int) FROM \"GroupMember\" WHERE \"GroupId\"=:id AND \"ActiveTo\" IS NULL")
            .SetParameter("id", group).UniqueResultAsync<int>());
        Assert.Empty(await session.QueryOver<GroupMembership>().Where(x => x.UserId == member.Id).ListAsync());
    }

    [PostgreSqlFact]
    public async Task A_result_cannot_reactivate_a_disabled_local_account_or_a_closed_identity()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_detached_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (identity, member, _) = await SeedAsync(host);
        var attempt = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        var observation = new HubUserObservation("opaque-user", HubDirectoryAccountState.Active, [new("opaque-group", "Hub writers")]);
        await host.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsActive\"=false WHERE \"Id\"=:id", member.Id);
        Assert.False((await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(attempt, observation))).Applied);
        using (var check = host.OpenSession()) Assert.False((await check.GetAsync<UserAccount>(member.Id)).IsActive);
        // Explicit local reactivation allows testing the independent link guard on the same untouched observation.
        await host.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsActive\"=true WHERE \"Id\"=:id", member.Id);
        await host.ExecuteAsync("UPDATE \"ExternalIdentity\" SET \"ActiveTo\"=statement_timestamp() WHERE \"Id\"=:id", identity);
        Assert.False((await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(attempt, observation))).Applied);
        using var session = host.OpenSession();
        Assert.Equal(0, await session.CreateSQLQuery("SELECT CAST(count(*) AS int) FROM \"GroupMember\" WHERE \"UserId\"=:id")
            .SetParameter("id", member.Id).UniqueResultAsync<int>());
    }

    [PostgreSqlFact]
    public async Task An_unchanged_verification_does_not_wait_for_content_writes_and_a_membership_change_does()
    {
        // The exclusive content lock stops every keystroke in the wiki: a verification takes it only when access changes.
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_lock_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (identity, _, _) = await SeedAsync(host);
        var observation = new HubUserObservation("opaque-user", HubDirectoryAccountState.Active, [new("opaque-group", "Hub writers")]);
        var first = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(first, observation));
        using var writer = host.OpenSession();
        using var write = writer.BeginTransaction();
        // A content write in progress holds the shared content lock (AtlasLocks).
        await writer.CreateSQLQuery("SELECT pg_advisory_xact_lock_shared(731145012)").ExecuteUpdateAsync();

        var unchanged = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        Assert.Equal(new HubSyncResult(true, "verified", 0, 0),
            await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(unchanged, observation)).WaitAsync(TimeSpan.FromSeconds(10)));
        var removal = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        var removing = host.InvokeAsync<HubSyncResult>(new ApplyHubSync(removal, observation with { Groups = [] }));
        await Task.Delay(500);
        Assert.False(removing.IsCompleted);
        await write.CommitAsync();
        Assert.Equal(new HubSyncResult(true, "verified", 0, 1), await removing.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static async Task<(Guid Identity, UserAccount Member, Guid Group)> SeedAsync(AtlasTestHost host)
    {
        var member = await host.SeedUserAsync("member");
        var group = await host.SeedGroupAsync("Hub writers");
        var identity = Guid.NewGuid();
        await host.SeedAsync(async session =>
        {
            await session.CreateSQLQuery("""
                INSERT INTO "HubConnection"("Id") VALUES (:connection);
                INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
                VALUES (gen_random_uuid(),:connection,'https://hub.example.test/hub','https://hub.example.test/hub','protected-test-value',2,:user,statement_timestamp()-interval '1 hour');
                INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
                VALUES (:identity,:user,'https://hub.example.test/hub','opaque-user',statement_timestamp()-interval '1 hour');
                INSERT INTO "HubGroupBinding"("GroupId","ConnectionId","ExternalGroupId") VALUES (:group,:connection,'opaque-group');
                """).SetParameter("connection", Guid.NewGuid()).SetParameter("user", member.Id)
                .SetParameter("identity", identity).SetParameter("group", group).ExecuteUpdateAsync();
        });
        return (identity, member, group);
    }
}
