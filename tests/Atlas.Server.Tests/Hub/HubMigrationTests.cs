using System.Net;
using System.Text.Json;
using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Queries;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Hub.Migration;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Hub;

public sealed class HubMigrationTests
{
    [PostgreSqlFact]
    public async Task Preview_is_read_only_and_apply_converts_exact_members_and_isolates_reviewed_pages_atomically()
    {
        await using var f = await Fixture.CreateAsync();
        f.Host.Actor.Current = f.Admin;
        await f.Host.ExecuteAsync("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=true WHERE \"Id\"=:id", f.Jiri.Id);
        var preview = await f.Service.PreviewAsync(f.Request with { RemoveWikiAdminUserIds = [f.Jiri.Id] }, CancellationToken.None);
        Assert.Contains(preview.BeforeRoutes, r => r.UserId == f.Jiri.Id && r.ResourceId == f.Page && r.Role == 2);
        Assert.DoesNotContain(preview.AfterRoutes, r => r.UserId == f.Jiri.Id && r.ResourceId == f.Page);
        Assert.Contains(preview.AfterRoutes, r => r.UserId == f.Daniela.Id && r.ResourceId == f.Page && r.Role == 1);
        Assert.Contains(preview.AfterRoutes, r => r.UserId == f.LocalOnly.Id && r.GrantId == f.Independent);
        Assert.DoesNotContain("test-token", JsonSerializer.Serialize(preview), StringComparison.Ordinal);
        using (var read = f.Host.OpenSession())
        {
            Assert.Empty(await read.QueryOver<HubGroupBinding>().ListAsync());
            Assert.Equal(3, await read.CreateSQLQuery("SELECT count(*) FROM \"GroupMember\" WHERE \"ActiveTo\" IS NULL").UniqueResultAsync<long>());
        }
        var result = await f.Host.InvokeAsync<HubMigrationResult>(new ApplyHubMigration(preview.Ticket));
        Assert.Equal(new HubMigrationResult(2, 1, 1, 1), result);
        using var session = f.Host.OpenSession();
        Assert.Equal(2, await session.QueryOver<HubGroupBinding>().RowCountAsync());
        Assert.Equal(0, await session.CreateSQLQuery("SELECT count(*) FROM \"GroupMember\" WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL").SetParameter("id", f.LocalOnly.Id).UniqueResultAsync<long>());
        Assert.Equal(1, await session.CreateSQLQuery("SELECT count(*) FROM \"SpaceGrant\" WHERE \"Id\"=:id AND \"ActiveTo\" IS NULL").SetParameter("id", f.Independent).UniqueResultAsync<long>());
        Assert.True((await session.GetAsync<Page>(f.Page)).OwnAccess);
        Assert.False((await session.GetAsync<UserAccount>(f.Jiri.Id)).IsWikiAdmin);
        Assert.NotEqual(f.Jiri.SecurityStamp, (await session.GetAsync<UserAccount>(f.Jiri.Id)).SecurityStamp);
        Assert.Equal(1, await session.CreateSQLQuery("SELECT \"Role\" FROM \"EffectivePageAccess\" WHERE \"PageId\"=:page AND \"UserId\"=:user").SetParameter("page", f.Page).SetParameter("user", f.Daniela.Id).UniqueResultAsync<int>());
        Assert.Equal(0, await session.CreateSQLQuery("SELECT count(*) FROM \"EffectivePageAccess\" WHERE \"PageId\"=:page AND \"UserId\"=:user").SetParameter("page", f.Page).SetParameter("user", f.Jiri.Id).UniqueResultAsync<long>());
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<HubMigrationResult>(new ApplyHubMigration(preview.Ticket)))).Status);

        const string emptyDocument = """{"format":"scalesoft-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var child = await f.Host.InvokeAsync<PageDto>(new CreatePage(new(preview.Inventory.Spaces.Single().Id, f.Page, "Nová OPS stránka", emptyDocument, "")));
        Assert.True((await session.GetAsync<Page>(child.Id)).OwnAccess); // The boundary exists before the first publication.
        await f.Host.InvokeAsync(new AuthorizeAttachmentUpload(child.Id));
        var attachmentId = Guid.NewGuid();
        using var content = new MemoryStream("OPS attachment"u8.ToArray());
        var stored = await f.Storage.Files.WriteAsync(attachmentId, content, false, CancellationToken.None);
        await f.Host.InvokeAsync<AttachmentDto>(new StoreAttachment(attachmentId, child.Id, "ops.txt", stored.Size, stored.Hash, stored.MimeType, false));
        var document = """{"format":"scalesoft-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"OPS attachment","marks":[{"type":"link","attrs":{"href":"/api/attachments/ATTACHMENT/download"}}]}]}]}}"""
            .Replace("ATTACHMENT", attachmentId.ToString(), StringComparison.Ordinal);
        child = await f.Host.InvokeAsync<PageDto>(new SavePage(child.Id, new(child.Title, document, "[OPS attachment](/api/attachments/" + attachmentId + "/download)", child.Version)));
        child = await f.Host.InvokeAsync<PageDto>(new PublishPage(child.Id, new(child.Version)));
        Assert.True(child.IsPublished);
        f.Host.Actor.Current = f.Daniela;
        Assert.False((await f.Host.InvokeAsync<PageDto>(new GetPage(child.Id))).CanEdit);
        Assert.Equal(attachmentId, (await f.Host.InvokeAsync<AttachmentContentDto>(new OpenAttachment(attachmentId, false))).Id);
        using (var binary = f.Storage.Files.Open(attachmentId))
        using (var reader = new StreamReader(binary)) Assert.Equal("OPS attachment", await reader.ReadToEndAsync());
        f.Host.Actor.Current = await session.GetAsync<UserAccount>(f.Jiri.Id);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<PageDto>(new GetPage(child.Id)))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<AttachmentContentDto>(new OpenAttachment(attachmentId, false)))).Status);
    }

    [PostgreSqlFact]
    public async Task Stale_acl_configuration_and_expired_observations_cannot_apply_any_change()
    {
        await using var f = await Fixture.CreateAsync();
        f.Host.Actor.Current = f.Admin;
        var preview = await f.Service.PreviewAsync(f.Request, CancellationToken.None);
        await f.Host.SeedGroupAsync("Concurrent local group");
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<HubMigrationResult>(new ApplyHubMigration(preview.Ticket)))).Status);
        preview = await f.Service.PreviewAsync(f.Request, CancellationToken.None);
        var protector = f.Host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("ScaleAtlas.Hub.Migration.v1");
        var ticket = JsonSerializer.Deserialize<HubMigrationTicket>(protector.Unprotect(preview.Ticket))!;
        var expired = protector.Protect(JsonSerializer.Serialize(ticket with { StartedAt = DateTime.UtcNow.AddMinutes(-6) }));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<HubMigrationResult>(new ApplyHubMigration(expired)))).Status);
        var scheduledGroup = await f.Host.SeedGroupAsync("Scheduled independent grant");
        await f.Host.SeedAsync(s => s.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), GroupId = scheduledGroup,
            SpaceId = preview.Inventory.Spaces.Single().Id, Role = 1, StartAt = DateTime.UtcNow.AddMinutes(-1) }));
        preview = await f.Service.PreviewAsync(f.Request, CancellationToken.None);
        ticket = JsonSerializer.Deserialize<HubMigrationTicket>(protector.Unprotect(preview.Ticket))!;
        // The same unchanged ACL at two instants: a scheduled grant starts after preview, before confirmation.
        var crossedDeadline = protector.Protect(JsonSerializer.Serialize(ticket with { StartedAt = DateTime.UtcNow.AddMinutes(-2) }));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<HubMigrationResult>(new ApplyHubMigration(crossedDeadline)))).Status);
        await f.Host.SeedAsync(s => s.CreateSQLQuery("""
            UPDATE "HubConnectionRevision" SET "ActiveTo"=statement_timestamp() WHERE "ActiveTo" IS NULL;
            INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
            SELECT gen_random_uuid(),"ConnectionId","Issuer","BaseUrl","ProtectedToken",1,"ChangedById",statement_timestamp()
            FROM "HubConnectionRevision" ORDER BY "ActiveFrom" DESC LIMIT 1
            """).ExecuteUpdateAsync());
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<HubMigrationResult>(new ApplyHubMigration(preview.Ticket)))).Status);
        using var read = f.Host.OpenSession();
        Assert.Empty(await read.QueryOver<HubGroupBinding>().ListAsync());
        Assert.False((await read.GetAsync<Page>(f.Page)).OwnAccess);
    }

    [PostgreSqlFact]
    public async Task Fresh_auth_and_surviving_recovery_account_are_required_before_mutation()
    {
        await using var f = await Fixture.CreateAsync();
        f.Host.Actor.Current = f.Admin;
        var preview = await f.Service.PreviewAsync(f.Request, CancellationToken.None);
        var lastSpaceAdmin = preview.Inventory.Grants.Single(x => x.SpaceId is not null && x.ActiveTo is null && x.Role == 3);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Service.PreviewAsync(
            f.Request with { RemoveSpaceGrantIds = [lastSpaceAdmin.Id] }, CancellationToken.None))).Status);
        f.Guard.Allow = false;
        Assert.Equal(428, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync<HubMigrationResult>(new ApplyHubMigration(preview.Ticket)))).Status);
        Assert.Equal("hub.migration", f.Guard.LastKind);
        f.Guard.Allow = true;
        await f.Host.ExecuteAsync("UPDATE \"BuiltinCredential\" SET \"PasswordHash\"=NULL WHERE \"UserId\"=:id", f.Admin.Id);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => f.Service.PreviewAsync(f.Request, CancellationToken.None))).Status);
        using var read = f.Host.OpenSession();
        Assert.Empty(await read.QueryOver<HubGroupBinding>().ListAsync());
    }

    [PostgreSqlFact]
    public async Task Preview_mode_apply_reconciles_existing_bindings_without_making_external_authority_effective()
    {
        await using var f = await Fixture.CreateAsync();
        f.Host.Actor.Current = f.Admin;
        var existing = await f.Host.SeedGroupAsync("Previously attached", f.Jiri);
        await f.Host.SeedAsync(s => s.CreateSQLQuery("""
            UPDATE "HubConnectionRevision" SET "ActiveTo"=statement_timestamp() WHERE "ActiveTo" IS NULL;
            INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
            SELECT gen_random_uuid(),"ConnectionId","Issuer","BaseUrl","ProtectedToken",1,"ChangedById",statement_timestamp()
            FROM "HubConnectionRevision" ORDER BY "ActiveFrom" DESC LIMIT 1;
            INSERT INTO "HubGroupBinding"("GroupId","ConnectionId","ExternalGroupId","GrantsWikiAdmin")
            SELECT :group,"ConnectionId",'opaque-existing',true FROM "HubConnectionRevision" WHERE "ActiveTo" IS NULL
            """).SetParameter("group", existing).ExecuteUpdateAsync());
        using var read = f.Host.OpenSession();
        var config = await read.QueryOver<HubConnectionRevision>().Where(x => x.ActiveTo == null).SingleOrDefaultAsync();
        var preview = await f.Service.PreviewAsync(f.Request with { ExpectedVersion = AdministrationVersion.Format(config.ActiveFrom) }, CancellationToken.None);
        var diff = Assert.Single(preview.GroupDiffs, x => x.LocalGroupId == existing);
        Assert.False(diff.ConvertsLocalGroup);
        Assert.Equal([f.Jiri.Id], diff.BeforeUserIds);
        Assert.Empty(diff.AfterUserIds);
        await f.Host.InvokeAsync<HubMigrationResult>(new ApplyHubMigration(preview.Ticket));
        using var after = f.Host.OpenSession();
        Assert.True((await after.GetAsync<HubGroupBinding>(existing)).GrantsWikiAdmin);
        Assert.Equal(0, await after.CreateSQLQuery("SELECT count(*) FROM \"GroupMember\" WHERE \"GroupId\"=:group AND \"ActiveTo\" IS NULL").SetParameter("group", existing).UniqueResultAsync<long>());
        Assert.Equal(0, await after.CreateSQLQuery("SELECT count(*) FROM \"EffectiveGroupMember\"").UniqueResultAsync<long>());
        Assert.Equal(0, await after.CreateSQLQuery("SELECT count(*) FROM \"EffectiveWikiAdmin\"").UniqueResultAsync<long>());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required AtlasTestHost Host { get; init; }
        public required HubMigrationService Service { get; init; }
        public required Guard Guard { get; init; }
        public required PreviewHubMigration Request { get; init; }
        public required UserAccount Admin { get; init; }
        public required UserAccount Daniela { get; init; }
        public required UserAccount Jiri { get; init; }
        public required UserAccount LocalOnly { get; init; }
        public required Guid Page { get; init; }
        public required Guid Independent { get; init; }
        public required AttachmentTests.Storage Storage { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var guard = new Guard();
            var storage = new AttachmentTests.Storage();
            var host = await AtlasTestHost.CreateAsync("atlas_hub_migration_", services =>
            {
                services.AddAdministration();
                services.AddDataProtection();
                services.AddSingleton(new AtlasOidcOptions());
                services.AddSingleton<ISecurityChangeGuard>(guard);
                services.AddSingleton(storage.Files);
            });
            var admin = await host.SeedUserAsync("recovery", true);
            var daniela = await host.SeedUserAsync("daniela");
            var jiri = await host.SeedUserAsync("jiri");
            var local = await host.SeedUserAsync("local-only");
            host.Actor.Current = admin;
            var px2 = await host.SeedGroupAsync("PX2 local", daniela, jiri, local);
            var ops = await host.SeedGroupAsync("OPS local");
            var space = await host.SeedSpaceAsync("Portal", (admin, SpaceRole.Admin));
            var page = Guid.NewGuid();
            var duplicate = Guid.NewGuid();
            var independent = Guid.NewGuid();
            var revision = Guid.NewGuid();
            var protection = host.Services.GetRequiredService<IDataProtectionProvider>();
            await host.SeedAsync(async s =>
            {
                await s.SaveAsync(new BuiltinCredential { UserId = admin.Id, PasswordHash = "test-password-hash" });
                await s.SaveAsync(new Page { Id = page, SpaceId = space, OwnerId = admin.Id });
                await s.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = space, GroupId = px2, Role = 2 });
                await s.SaveAsync(new SpaceGrant { Id = duplicate, SpaceId = space, UserId = daniela.Id, Role = 2 });
                await s.SaveAsync(new SpaceGrant { Id = independent, SpaceId = space, UserId = local.Id, Role = 1 });
                await s.FlushAsync();
                await s.CreateSQLQuery("""
                    INSERT INTO "HubConnection"("Id") VALUES (:connection);
                    INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
                    VALUES (:revision,:connection,'https://hub.example.test/hub','https://hub.example.test/hub',:token,2,:admin,statement_timestamp()-interval '1 hour');
                    INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom") VALUES
                    (gen_random_uuid(),:daniela,'https://hub.example.test/hub','opaque-daniela',statement_timestamp()-interval '1 hour'),
                    (gen_random_uuid(),:jiri,'https://hub.example.test/hub','opaque-jiri',statement_timestamp()-interval '1 hour')
                    """).SetParameter("connection", Guid.NewGuid()).SetParameter("revision", revision).SetParameter("admin", admin.Id)
                    .SetParameter("daniela", daniela.Id).SetParameter("jiri", jiri.Id)
                    .SetParameter("token", protection.CreateProtector("ScaleAtlas.Hub.Token.v1").Protect("test-token")).ExecuteUpdateAsync();
            });
            using var read = host.OpenSession();
            var config = await read.GetAsync<HubConnectionRevision>(revision);
            var request = new PreviewHubMigration(AdministrationVersion.Format(config.ActiveFrom), [new(px2, "opaque-px2"), new(ops, "opaque-ops")],
                [duplicate], [], [], [new(page, [new(ops, "Reader")])]);
            var service = new HubMigrationService(host.Services.GetRequiredService<IServiceScopeFactory>(), new ClientFactory(host), protection, TimeProvider.System);
            return new Fixture { Host = host, Service = service, Guard = guard, Request = request, Admin = admin,
                Daniela = daniela, Jiri = jiri, LocalOnly = local, Page = page, Independent = independent, Storage = storage };
        }

        public async ValueTask DisposeAsync()
        {
            try { await Host.DisposeAsync(); }
            finally { Storage.Dispose(); }
        }
    }

    private sealed class Guard : ISecurityChangeGuard
    {
        public bool Allow { get; set; } = true;
        public string? LastKind { get; private set; }
        public Task RequireAsync(UserAccount actor, SecurityChange change, CancellationToken ct)
        {
            LastKind = change.Kind;
            if (!Allow) throw new ApiException(428, "Fresh authentication required.");
            return Task.CompletedTask;
        }
    }

    private sealed class ClientFactory(AtlasTestHost host) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new DirectoryHandler(host));
    }

    private sealed class DirectoryHandler(AtlasTestHost host) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var s = host.OpenSession();
            using var tx = s.BeginTransaction();
            Assert.True(await s.CreateSQLQuery("SELECT pg_try_advisory_xact_lock_shared(731145012)").UniqueResultAsync<bool>(ct));
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/me", StringComparison.Ordinal)
                ? """{"id":"reader","banned":false,"guest":false,"transitiveProjectRoles":[{"project":{"id":"0","global":true},"role":{"permissions":[{"key":"jetbrains.jetpass.user-read-basic"},{"key":"jetbrains.jetpass.group-read"},{"key":"jetbrains.jetpass.role-read"}]}}]}"""
                : path.Contains("/usergroups/", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new { id = path.Split('/')[^1], name = "Verified group" })
                    : JsonSerializer.Serialize(new { id = path.Split('/')[^1], banned = false, guest = false, transitiveGroups =
                        path.EndsWith("opaque-daniela", StringComparison.Ordinal)
                            ? new[] { new { id = "opaque-px2", name = "PX2" }, new { id = "opaque-ops", name = "OPS" } }
                            : [new { id = "opaque-px2", name = "PX2" }] });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
