using System.Diagnostics;
using System.Net;
using Atlas.Server.Features.Hub;
using Atlas.Server.Features.Hub.Commands;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Atlas.Server.Tests.Events.OutboxTests;

namespace Atlas.Server.Tests.Hub;

public sealed class HubSynchronizationBoundaryTests
{
    [PostgreSqlFact]
    public async Task Upstream_read_holds_no_content_lock_and_only_commits_the_complete_response()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_hub_boundary_", services => services.AddDataProtection());
        var member = await host.SeedUserAsync("member");
        var group = await host.SeedGroupAsync("Hub writers");
        var identity = Guid.NewGuid();
        var protection = host.Services.GetRequiredService<IDataProtectionProvider>();
        await host.SeedAsync(async session =>
        {
            await session.CreateSQLQuery("""
                INSERT INTO "HubConnection"("Id") VALUES (:connection);
                INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
                VALUES (gen_random_uuid(),:connection,'https://hub.example.test/hub','https://hub.example.test/hub',:token,2,:user,statement_timestamp()-interval '1 hour');
                INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
                VALUES (:identity,:user,'https://hub.example.test/hub','opaque-user',statement_timestamp()-interval '1 hour');
                INSERT INTO "HubGroupBinding"("GroupId","ConnectionId","ExternalGroupId") VALUES (:group,:connection,'opaque-group');
                """).SetParameter("connection", Guid.NewGuid()).SetParameter("user", member.Id).SetParameter("group", group)
                .SetParameter("identity", identity).SetParameter("token", protection.CreateProtector("ScaleAtlas.Hub.Token.v1").Protect("test-token")).ExecuteUpdateAsync();
        });
        using var handler = new CheckingHandler(async () =>
        {
            using var check = host.OpenSession();
            using var transaction = check.BeginTransaction();
            Assert.True(await check.CreateSQLQuery("SELECT pg_try_advisory_xact_lock(731145012)").UniqueResultAsync<bool>());
            Assert.Empty(await check.QueryOver<GroupMembership>().Where(x => x.UserId == member.Id).ListAsync());
        });
        var synchronizer = new HubSynchronizer(host.Services.GetRequiredService<IServiceScopeFactory>(), new ClientFactory(handler), protection, TimeProvider.System);
        // The job's data schedule (HubIdentityJob): an identity never verified is due now, a verified one at its NextAttemptAt.
        var unverified = await synchronizer.NextDueAsync(CancellationToken.None);
        Assert.Equal(new HubSyncResult(true, "verified", 1, 0), await synchronizer.VerifyAsync(identity, true, CancellationToken.None));
        using var session = host.OpenSession();
        Assert.Single(await session.QueryOver<GroupMembership>().Where(x => x.UserId == member.Id && x.GroupId == group).ListAsync());
        Assert.Equal(2, handler.Requests);
        var state = await session.GetAsync<HubIdentityState>(identity);
        Assert.Equal(state.NextAttemptAt, await synchronizer.NextDueAsync(CancellationToken.None));
        Assert.True(unverified < state.NextAttemptAt);
    }

    [PostgreSqlFact]
    public async Task The_first_identity_plans_the_job_its_empty_start_left_without_a_plan()
    {
        using var handler = new CheckingHandler(() => Task.CompletedTask);
        await using var host = await AtlasTestHost.CreateAsync("atlas_hub_plan_", services =>
        {
            services.AddDataProtection();
            services.AddSingleton<IHttpClientFactory>(new ClientFactory(handler));
            services.AddSingleton<HubSynchronizer>();
            services.AddJob<HubIdentityJob>();
        }, durable: true);
        var member = await host.SeedUserAsync("member");
        var protection = host.Services.GetRequiredService<IDataProtectionProvider>();
        await host.SeedAsync(async session => await session.CreateSQLQuery("""
            INSERT INTO "HubConnection"("Id") VALUES (:connection);
            INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
            VALUES (gen_random_uuid(),:connection,'https://hub.example.test/hub','https://hub.example.test/hub',:token,2,:user,statement_timestamp()-interval '1 hour');
            """).SetParameter("connection", Guid.NewGuid()).SetParameter("user", member.Id)
            .SetParameter("token", protection.CreateProtector("ScaleAtlas.Hub.Token.v1").Protect("test-token")).ExecuteUpdateAsync());
        // The start found no identity: no plan, as after a pass over none.
        await WaitAsync(host, """SELECT count(*) = 1 FROM "Job" WHERE "Id" = 'hub.identities'""");
        Assert.IsType<DBNull>(await ScalarAsync(host.ConnectionString, """SELECT "NextDueAt" FROM "Job" WHERE "Id" = 'hub.identities'"""));

        // The first login through Hub creates the first state; the job plans its re-verification from it.
        var identity = Guid.NewGuid();
        await host.SeedAsync(async session => await session.CreateSQLQuery("""
            INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
            VALUES (:identity,:user,'https://hub.example.test/hub','opaque-user',statement_timestamp());
            """).SetParameter("identity", identity).SetParameter("user", member.Id).ExecuteUpdateAsync());
        Assert.NotNull(await host.Services.GetRequiredService<HubSynchronizer>().VerifyAsync(identity, true, CancellationToken.None));
        await WaitAsync(host, """SELECT "NextDueAt" IS NOT NULL FROM "Job" WHERE "Id" = 'hub.identities'""");
    }

    private static async Task WaitAsync(AtlasTestHost host, string sql)
    {
        var clock = Stopwatch.StartNew();
        while (await ScalarAsync(host.ConnectionString, sql) is not true)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), sql);
            await Task.Delay(50);
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CheckingHandler(Func<Task> check) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("hub.example.test", request.RequestUri!.Host);
            await check();
            var body = request.RequestUri.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
                ? """{"id":"reader","banned":false,"guest":false,"transitiveProjectRoles":[{"project":{"id":"0","global":true},"role":{"permissions":[{"key":"jetbrains.jetpass.user-read-basic"},{"key":"jetbrains.jetpass.group-read"},{"key":"jetbrains.jetpass.role-read"}]}}]}"""
                : """{"id":"opaque-user","banned":false,"guest":false,"transitiveGroups":[{"id":"opaque-group","name":"Hub writers"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
