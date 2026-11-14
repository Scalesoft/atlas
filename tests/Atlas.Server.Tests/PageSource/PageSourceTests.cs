using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.PageSource.Commands;
using Atlas.Server.Features.PageSource.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageSource;

public sealed class PageSourceTests
{
    [PostgreSqlFact]
    public async Task Space_admin_replaces_the_body_as_a_working_revision_with_an_audit_fact_and_a_new_generation()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.CreatePublished("Runbook");
        var opened = await f.Collaboration.OpenAsync(page.Id, f.SpaceAdmin);
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.SpaceAdmin.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });

        var replaced = await f.ReplaceAsync(page.Id, new(Fixture.Rich("Fixed"), "forged", 1), f.SpaceAdmin);

        Assert.Equal(2, replaced.Version);
        Assert.Equal("Runbook", replaced.Title);
        Assert.Equal(Fixture.Rich("Fixed"), replaced.ContentJson);
        Assert.Equal("Fixed", replaced.Markdown); // written from the JSON, not the client's "forged"
        Assert.Equal(1, replaced.PublishedVersion);
        Assert.Equal("Runbook", (await f.Pages.GetAsync(page.Id, f.Reader, default)).Title);
        var history = await f.History.ListAsync(page.Id, f.SpaceAdmin);
        Assert.Equal(new[] { 2, 1 }, history.Items.Select(x => x.Version));
        Assert.Equal(f.SpaceAdmin.Id, history.Items[0].AuthorId);
        Assert.Equal(history.Items[0].ActiveFrom, history.Items[1].ActiveTo);
        Assert.Equal(1L, await f.Scalar("""
            SELECT count(*) FROM "PageAccessEvent" e JOIN "PageRevision" r ON r."Id"=e."RevisionId"
            WHERE e."PageId"=:id AND e."Action"='page.source-edited' AND e."ShareLinkId" IS NULL AND r."Version"=2
            """, page.Id));
        Assert.Equal(1L, await f.Scalar($"SELECT count(*) FROM \"PageAccessEvent\" WHERE \"PageId\"=:id AND \"ActorId\"='{f.SpaceAdmin.Id}'", page.Id));
        // Collaboration reset like a restore: a new generation on the new revision, no log, nothing unsaved.
        Assert.Equal(0L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));
        Assert.Equal(1L, await f.Scalar($"""
            SELECT count(*) FROM "PageCollaboration" c JOIN "PageRevision" r ON r."Id"=c."BaseRevisionId"
            WHERE c."PageId"=:id AND c."Generation"<>'{opened.Generation}' AND c."Sequence"=0 AND c."LastSavedSequence"=0 AND r."Version"=2
            """, page.Id));
        Assert.True(await f.Collaboration.HasExternalChangeAsync(opened.DocumentId));
    }

    [PostgreSqlFact]
    public async Task Only_space_admins_and_wiki_admins_may_replace_the_source()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.CreatePublished("Guarded");
        var writer = await Assert.ThrowsAsync<ApiException>(() => f.ReplaceAsync(page.Id, new(Fixture.Rich("W"), "W", 1), f.Writer));
        Assert.Equal(403, writer.Status);
        // The refusal has a text of its own, not the one of a missing ManageAccess.
        Assert.Equal("Zdroj stránky může upravit pouze správce prostoru nebo správce stránky.", writer.Message);
        // A Reader can read the page, so the refusal is disclosed (403) like the Writer's; strangers learn nothing (404).
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.ReplaceAsync(page.Id, new(Fixture.Rich("R"), "R", 1), f.Reader))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.ReplaceAsync(page.Id, new(Fixture.Rich("S"), "S", 1), f.Stranger))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.ReplaceAsync(Guid.NewGuid(), new(Fixture.Rich("M"), "M", 1), f.SpaceAdmin))).Status);
        Assert.Equal(1, (await f.Pages.GetAsync(page.Id, f.Writer, default)).Version);
        Assert.Equal(0L, await f.Scalar("SELECT count(*) FROM \"PageAccessEvent\" WHERE \"PageId\"=:id AND \"Action\"='page.source-edited'", page.Id));

        Assert.Equal(2, (await f.ReplaceAsync(page.Id, new(Fixture.Rich("Wiki"), "Wiki", 1), f.WikiAdmin)).Version);
        Assert.Equal(3, (await f.ReplaceAsync(page.Id, new(Fixture.Rich("Space"), "Space", 2), f.SpaceAdmin)).Version);
    }

    [PostgreSqlFact]
    public async Task Stale_version_invalid_document_and_foreign_live_edits_are_refused_without_writing()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.CreatePublished("Busy");
        await f.Pages.SaveAsync(page.Id, new("Busy", Fixture.Rich("Second"), "Second", 1), f.Writer, default);

        var stale = await Assert.ThrowsAsync<ApiException>(() => f.ReplaceAsync(page.Id, new(Fixture.Rich("Old"), "Old", 1), f.SpaceAdmin));
        Assert.Equal(409, stale.Status);
        Assert.Equal("version_conflict", stale.Code);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.ReplaceAsync(page.Id, new("{\"format\":\"other\"}", "x", 2), f.SpaceAdmin))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.ReplaceAsync(page.Id, new("not json", "x", 2), f.SpaceAdmin))).Status);

        var opened = await f.Collaboration.OpenAsync(page.Id, f.Writer);
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.SpaceAdmin.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 2 });
        var live = await Assert.ThrowsAsync<ApiException>(() => f.ReplaceAsync(page.Id, new(Fixture.Rich("Mine"), "Mine", 2), f.SpaceAdmin));
        Assert.Equal(409, live.Status);
        Assert.Equal("collaboration_unsaved", live.Code);
        Assert.Equal(2L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));
        Assert.Equal(2, (await f.Pages.GetAsync(page.Id, f.Writer, default)).Version);
        Assert.Equal(0L, await f.Scalar("SELECT count(*) FROM \"PageAccessEvent\" WHERE \"PageId\"=:id AND \"Action\"='page.source-edited'", page.Id));
    }

    [PostgreSqlFact]
    public async Task Replace_rechecks_the_locked_version_even_when_the_session_already_loaded_the_page()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.CreatePublished("Loaded");
        using var scope = f.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var transaction = session.BeginTransaction();
        Assert.Equal(1, (await session.GetAsync<Page>(page.Id)).Version);

        await f.ReplaceAsync(page.Id, new(Fixture.Rich("Theirs"), "Theirs", 1), f.SpaceAdmin);
        var handler = ActivatorUtilities.CreateInstance<ReplacePageSourceHandler>(scope.ServiceProvider);
        var error = await Assert.ThrowsAsync<ApiException>(() => handler.Handle(new ReplacePageSource(page.Id, new(Fixture.Rich("Mine"), "Mine", 1)), default));
        Assert.Equal((409, "version_conflict"), (error.Status, error.Code));
    }

    [PostgreSqlFact]
    public async Task Own_unsaved_live_edits_are_superseded_and_an_unchanged_body_writes_no_revision()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.CreatePublished("Mine");
        var opened = await f.Collaboration.OpenAsync(page.Id, f.SpaceAdmin);
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.SpaceAdmin.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.SpaceAdmin.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 2 });

        var unchanged = await f.ReplaceAsync(page.Id, new(Fixture.Rich("Mine"), "Mine", 1), f.SpaceAdmin);
        Assert.Equal(1, unchanged.Version);
        Assert.Equal(0L, await f.Scalar("SELECT count(*) FROM \"PageAccessEvent\" WHERE \"PageId\"=:id", page.Id));
        Assert.Equal(0L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));
        Assert.True(await f.Collaboration.HasExternalChangeAsync(opened.DocumentId));

        var reopened = await f.Collaboration.OpenAsync(page.Id, f.SpaceAdmin);
        await f.Collaboration.AppendUpdateAsync(reopened.DocumentId, Guid.NewGuid(), f.SpaceAdmin.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 3 });
        Assert.Equal(2, (await f.ReplaceAsync(page.Id, new(Fixture.Rich("Replaced"), "Replaced", 1), f.SpaceAdmin)).Version);
    }

    [PostgreSqlFact]
    public async Task Source_replace_round_trips_over_http_with_role_checks_and_problem_codes()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_page_source_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var created = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Imported", contentJson = Fixture.Rich("Broken"), markdown = "Broken" }), HttpStatusCode.OK);
        var id = created["id"]!.GetValue<string>();

        var writerLogin = TestLogin.For("writer");
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/admin/users",
            new { username = writerLogin, displayName = "Writer", isWikiAdmin = false }), HttpStatusCode.Created);
        using var writer = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(writer, HttpMethod.Post, "/api/auth/login", new { username = writerLogin, password = site.Password }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId = invitation["user"]!["id"]!.GetValue<string>(), role = "Writer", expectedVersion = (string?)null })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{id}/publish", new { expectedVersion = 1 }), HttpStatusCode.OK);

        var body = new { contentJson = Fixture.Rich("Fixed"), markdown = "Fixed", expectedVersion = 1 };
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, $"/api/pages/{id}/source", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{id}/source",
            new { contentJson = "{}", markdown = "", expectedVersion = 1 })).StatusCode);
        var stale = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{id}/source", body with { expectedVersion = 7 }), HttpStatusCode.Conflict);
        Assert.Equal("version_conflict", (string?)stale["code"]);

        var replaced = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{id}/source", body), HttpStatusCode.OK);
        Assert.Equal(2, replaced["version"]!.GetValue<int>());
        Assert.Equal("Imported", replaced["title"]!.GetValue<string>());
        Assert.Equal("Fixed", replaced["markdown"]!.GetValue<string>());
        Assert.Equal(1, replaced["publishedVersion"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/pages/{id}/source", body with { expectedVersion = 2 })).StatusCode);
        await site.ExecuteAsync("""
            UPDATE "SpaceGrant" SET "ActiveTo"=now() WHERE "UserId"=@user AND "ActiveTo" IS NULL;
            INSERT INTO "SpaceGrant" ("Id","SpaceId","UserId","Role","ActiveFrom") VALUES (gen_random_uuid(),@space,@user,1,now());
            """, ("user", invitation["user"]!["id"]!.GetValue<Guid>()), ("space", Guid.Parse(spaceId)));
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(writer, HttpMethod.Put, $"/api/pages/{id}/source", body with { expectedVersion = 2 })).StatusCode);
        using var anonymous = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await AtlasHttpSite.SendAsync(anonymous, HttpMethod.Put, $"/api/pages/{id}/source", body)).StatusCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AtlasTestHost m_host;

        private Fixture(AtlasTestHost host)
        {
            m_host = host;
            Pages = new TestPages(host);
            History = new TestPageHistory(host);
            Collaboration = new TestCollaboration(host);
        }

        public AtlasTestHost Host => m_host;
        public TestPages Pages { get; }
        public TestPageHistory History { get; }
        public TestCollaboration Collaboration { get; }
        public UserAccount WikiAdmin { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("wiki"), DisplayName = "Wiki admin", IsWikiAdmin = true };
        public UserAccount SpaceAdmin { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("spaceadmin"), DisplayName = "Space admin" };
        public UserAccount Writer { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("writer"), DisplayName = "Writer" };
        public UserAccount Reader { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("reader"), DisplayName = "Reader" };
        public UserAccount Stranger { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("stranger"), DisplayName = "Stranger" };
        public Guid SpaceId { get; } = Guid.NewGuid();

        public static string Rich(string text) => "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

        /// <summary>Published, so the space administrator sees it: an unpublished concept is private to its owner and wiki admins.</summary>
        public async Task<PageDto> CreatePublished(string title)
        {
            var page = await Pages.CreateAsync(new(SpaceId, null, title, Rich(title), title), Writer, default);
            return await Pages.PublishAsync(page.Id, new(1), Writer, default);
        }

        public Task<PageDto> ReplaceAsync(Guid id, ReplacePageSourceRequest request, UserAccount actor)
        {
            m_host.Actor.Current = actor;
            return m_host.InvokeAsync<PageDto>(new ReplacePageSource(id, request));
        }

        public async Task<long> Scalar(string sql, Guid id)
        {
            using var session = m_host.OpenSession();
            return Convert.ToInt64(await session.CreateSQLQuery(sql).SetParameter("id", id).UniqueResultAsync());
        }

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(await AtlasTestHost.CreateAsync("atlas_source_"));
            await f.m_host.SeedAsync(async session =>
            {
                foreach (var user in new[] { f.WikiAdmin, f.SpaceAdmin, f.Writer, f.Reader, f.Stranger })
                {
                    await session.SaveAsync(user);
                }
                await session.SaveAsync(new Space { Id = f.SpaceId, Name = "Source" });
                await session.FlushAsync();
                await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = f.SpaceAdmin.Id, Role = 3 });
                await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = f.Writer.Id, Role = 2 });
                await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = f.Reader.Id, Role = 1 });
            });
            return f;
        }

        public ValueTask DisposeAsync() => m_host.DisposeAsync();
    }
}
