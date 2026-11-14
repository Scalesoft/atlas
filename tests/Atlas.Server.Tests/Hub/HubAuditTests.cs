using Atlas.Server.Features.Hub.Commands;
using Atlas.Server.Features.Hub.Directory;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Features.AccessAudit.Domain;
using NHibernate.Exceptions;
using Npgsql;
using System.Text.Json;
using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Queries;
using Xunit;

namespace Atlas.Server.Tests.Hub;

public sealed class HubAuditTests
{
    [PostgreSqlFact]
    public async Task Connection_health_distinguishes_error_expiry_and_recovery_without_audit_noise()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_health_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (_, _, identity) = await HubEffectiveAuthorityTests.SeedAsync(host, false);
        using var read = host.OpenSession();
        var count = await read.CreateSQLQuery("SELECT count(*) FROM \"AccessAuditEvent\"").UniqueResultAsync<long>();
        Assert.Equal("Healthy", (await host.InvokeAsync<HubHealthView?>(new GetHubHealth()))!.State);
        await host.ExecuteAsync("UPDATE \"HubIdentityState\" SET \"LastErrorCode\"='timeout' WHERE \"ExternalIdentityId\"=:id", identity);
        var failed = (await host.InvokeAsync<HubHealthView?>(new GetHubHealth()))!;
        Assert.Equal("Error", failed.State);
        Assert.Equal(1, failed.Errors);
        await HubEffectiveAuthorityTests.ExpireAsync(host, identity);
        var stale = (await host.InvokeAsync<HubHealthView?>(new GetHubHealth()))!;
        Assert.Equal("Stale", stale.State);
        Assert.Equal(1, stale.Expired);
        await host.ExecuteAsync("""
            UPDATE "HubIdentityState" SET "SnapshotStartedAt"=statement_timestamp(),"VerifiedUntil"=statement_timestamp()+interval '5 minutes',
                "LastSuccessAt"=clock_timestamp(),"LastErrorCode"=NULL WHERE "ExternalIdentityId"=:id
            """, identity);
        Assert.Equal("Healthy", (await host.InvokeAsync<HubHealthView?>(new GetHubHealth()))!.State);
        await host.ExecuteAsync("""
            UPDATE "HubIdentityState" SET "SnapshotStartedAt"=statement_timestamp()+interval '1 minute',
                "VerifiedUntil"=statement_timestamp()+interval '6 minutes' WHERE "ExternalIdentityId"=:id
            """, identity);
        var future = (await host.InvokeAsync<HubHealthView?>(new GetHubHealth()))!;
        Assert.Equal("Unverified", future.State);
        Assert.Equal(1, future.Pending);
        Assert.Equal(count, await read.CreateSQLQuery("SELECT count(*) FROM \"AccessAuditEvent\"").UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task Observed_state_and_membership_have_hub_provenance_without_heartbeat_audit()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_audit_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (member, group, identity) = await HubEffectiveAuthorityTests.SeedAsync(host, false);
        using var initial = host.OpenSession();
        var initialCount = await initial.CreateSQLQuery("SELECT count(*) FROM \"AccessAuditEvent\"").UniqueResultAsync<long>();
        var attempt = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        var observed = new HubUserObservation(attempt.ExternalUserId, HubDirectoryAccountState.Active, [new("opaque-group", "Hub group")]);
        await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(attempt, observed));
        var unchanged = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(unchanged, observed));
        using var beforeBan = host.OpenSession();
        var beforeCount = await beforeBan.CreateSQLQuery("SELECT count(*) FROM \"AccessAuditEvent\"").UniqueResultAsync<long>();
        Assert.Equal(initialCount, beforeCount); // The seed already has the same name, membership and active state.
        var ban = (await host.InvokeAsync<HubSyncAttempt?>(new ReserveHubSync(identity, true)))!;
        await host.InvokeAsync<HubSyncResult>(new ApplyHubSync(ban, observed with { State = HubDirectoryAccountState.Disabled, Groups = [] }));
        using var after = host.OpenSession();
        var facts = await after.CreateSQLQuery("SELECT \"PreviousState\",\"CurrentState\" FROM \"HubChange\" WHERE \"UserId\"=:user AND \"PreviousState\" IS NOT NULL")
            .SetParameter("user", member.Id).ListAsync<object[]>();
        var change = Assert.Single(facts);
        Assert.Equal(1, Convert.ToInt32(change[0]));
        Assert.Equal(2, Convert.ToInt32(change[1]));
        Assert.Equal(beforeCount + 2, await after.CreateSQLQuery("SELECT count(*) FROM \"AccessAuditEvent\"").UniqueResultAsync<long>());
        var provenance = await after.CreateSQLQuery("SELECT \"ActorName\",\"HubConnectionId\",\"ExternalGroupId\",\"ExternalUserId\" FROM \"AccessAuditView\" WHERE \"GroupId\"=:group AND \"Action\"='membership.removed'")
            .SetParameter("group", group).UniqueResultAsync<object[]>();
        Assert.Equal("Synchronizace Hubu", provenance[0]);
        Assert.Equal(attempt.ConnectionId, provenance[1]);
        Assert.Equal("opaque-group", provenance[2]);
        Assert.Equal(attempt.ExternalUserId, provenance[3]);
        await host.SeedAsync(s => s.CreateSQLQuery("""
            UPDATE "ExternalIdentity" SET "ActiveTo"=clock_timestamp() WHERE "Id"=:identity;
            INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
            VALUES(gen_random_uuid(),:user,'https://hub.example.test/hub','replacement-user',clock_timestamp())
            """).SetParameter("identity", identity).SetParameter("user", member.Id).ExecuteUpdateAsync());
        using var historical = host.OpenSession();
        Assert.Equal(attempt.ExternalUserId, await historical.CreateSQLQuery("""
            SELECT "ExternalUserId" FROM "AccessAuditView" WHERE "GroupId"=:group AND "Action"='membership.removed'
            """).SetParameter("group", group).UniqueResultAsync<string>());
    }

    [PostgreSqlFact]
    public async Task Configuration_and_binding_pin_immutable_revisions_without_exposing_credentials()
    {
        await using var host = await AtlasTestHost.CreateIsolatedAsync("atlas_hub_audit_config_") /* own database: the Hub connection is one active row per installation (HubConnectionRevision, UX_HubConnectionRevision_Singleton) and the table is immutable */;
        var (member, group, _) = await HubEffectiveAuthorityTests.SeedAsync(host, false);
        await host.SeedAsync(async s =>
        {
            var binding = (await s.GetAsync<Atlas.Server.Features.Hub.Domain.HubGroupBinding>(group))!;
            binding.GrantsWikiAdmin = true;
            await Atlas.Server.Features.Hub.Domain.HubHistory.BindingAsync(s, binding, member.Id, CancellationToken.None);
        });
        using var read = host.OpenSession();
        Assert.Equal(1, await read.CreateSQLQuery("""
            SELECT count(*) FROM "HubChange" h JOIN "HubConnectionRevision" c ON c."Id"=h."ConnectionRevisionId"
            JOIN "AccessAuditEvent" e ON e."HubChangeId"=h."Id"
            WHERE e."ActorId"=c."ChangedById" AND h."Reason"='connection.created'
            """).UniqueResultAsync<long>());
        Assert.Equal(2, await read.CreateSQLQuery("""
            SELECT count(*) FROM "HubChange" h JOIN "HubGroupBindingRevision" b ON b."Id"=h."BindingRevisionId"
            WHERE h."GroupId"=:group AND h."GroupId"=b."GroupId"
            """).SetParameter("group", group).UniqueResultAsync<long>());
        var row = await read.QueryOver<AccessAuditView>().Where(x => x.GroupId == group && x.Action == "hub.binding.changed")
            .OrderBy(x => x.Id).Desc.Take(1).SingleOrDefaultAsync();
        Assert.Equal(member.Id, row.ActorId);
        var payload = JsonSerializer.Serialize(row.ToDto());
        Assert.Contains("opaque-group", payload);
        Assert.DoesNotContain("test-protected", payload);
        // HubChange is append-only: no server code updates or deletes it (IntegrityRulesConventionTests, C# since 3.6).
    }
}
