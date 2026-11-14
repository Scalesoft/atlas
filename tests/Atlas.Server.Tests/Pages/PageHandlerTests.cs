using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.KnowledgeMaintenance.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Pages;

public sealed class PageHandlerTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Get_serves_the_working_revision_to_writers_the_published_one_to_readers_and_hides_foreign_drafts()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Original");
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Working", Fixture.Rich("Working"), "working", 1), f.Writer, default);
        var mine = await f.As<PageDto>(f.Writer, new GetPage(page.Id));
        Assert.Equal("Working", mine.Title);
        Assert.Equal(2, mine.Version);
        Assert.Equal(1, mine.PublishedVersion);
        Assert.True(mine.IsPublished);
        Assert.True(mine.CanEdit);
        Assert.Equal(DateTimeKind.Utc, mine.ActiveFrom.Kind);
        Assert.Null(mine.ActiveTo);
        var seen = await f.As<PageDto>(f.Reader, new GetPage(page.Id));
        Assert.Equal("Original", seen.Title);
        Assert.Equal(1, seen.PublishedVersion);
        Assert.False(seen.CanEdit);
        var draft = await f.Create("Private");
        await Denied(404, "Stránka nebyla nalezena.", () => f.As<PageDto>(f.Reader, new GetPage(draft.Id)));
        await Denied(404, "Stránka nebyla nalezena.", () => f.As<PageDto>(f.OtherWriter, new GetPage(draft.Id)));
        Assert.Equal(draft.Id, (await f.As<PageDto>(f.Admin, new GetPage(draft.Id))).Id);
        await Denied(404, "Stránka nebyla nalezena.", () => f.As<PageDto>(f.Writer, new GetPage(Guid.NewGuid())));
        // CK_Page_Trash: a trashed row must carry all three trash columns.
        await f.Execute("UPDATE \"Page\" SET \"IsTrashed\"=true,\"TrashedAt\"=now(),\"TrashedById\"=\"OwnerId\",\"TrashBatchId\"=gen_random_uuid() WHERE \"Id\"=:id", draft.Id);
        await Denied(404, "Stránka nebyla nalezena.", () => f.As<PageDto>(f.Writer, new GetPage(draft.Id)));
    }

    [PostgreSqlFact]
    public async Task Create_starts_at_version_one_records_the_actor_and_validates_parent_and_document()
    {
        await using var f = await Fixture.CreateAsync();
        var parent = await f.As<PageDto>(f.Writer, new CreatePage(new(f.SpaceId, null, " Parent ", Fixture.Rich("Parent"), "parent")));
        Assert.Equal("Parent", parent.Title);
        Assert.Equal(1, parent.Version);
        Assert.False(parent.IsPublished);
        Assert.True(parent.CanEdit);
        Assert.Null(parent.PublishedVersion);
        var child = await f.As<PageDto>(f.Writer, new CreatePage(new(f.SpaceId, parent.Id, "Child", Fixture.Rich("Child"), "child")));
        Assert.Equal(parent.Id, child.ParentId);
        await Denied(400, "Rodičovská stránka není dostupná v tomto prostoru.",
            () => f.As<PageDto>(f.OtherWriter, new CreatePage(new(f.SpaceId, parent.Id, "Foreign child", Fixture.Rich("x"), "x"))));
        await Denied(400, "Obsah editoru není platný JSON dokument, nebo je zanořen hlouběji než 64 úrovní uzlů.",
            () => f.As<PageDto>(f.Writer, new CreatePage(new(f.SpaceId, null, "Broken", "{}", "broken"))));
        await Denied(400, "Pole Název stránky je povinné a smí mít nejvýše 200 znaků.",
            () => f.As<PageDto>(f.Writer, new CreatePage(new(f.SpaceId, null, "   ", Fixture.Rich("x"), "x"))));
        await Denied(403, "K úpravám obsahu potřebujete v prostoru oprávnění k zápisu.",
            () => f.As<PageDto>(f.Reader, new CreatePage(new(f.SpaceId, null, "Reader page", Fixture.Rich("x"), "x"))));
    }

    [PostgreSqlFact]
    public async Task Concurrent_creates_in_one_space_all_succeed_with_distinct_sibling_positions()
    {
        await using var f = await Fixture.CreateAsync();
        // The insert trigger takes the space FOR UPDATE to order the siblings. Creates that held it FOR SHARE first deadlocked (40P01).
        var created = await Task.WhenAll(Enumerable.Range(1, 6).Select(index => Task.Run(() => f.Create("Parallel " + index))));
        Assert.Equal(6, created.Select(page => page.Id).Distinct().Count());
        Assert.Equal(6, await f.Scalar("SELECT count(DISTINCT \"SortOrder\") FROM \"Page\" WHERE \"SpaceId\"=:id", f.SpaceId));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Save_appends_a_temporal_revision_enforces_CAS_and_stays_idempotent_for_identical_content()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("First");
        var saved = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Second", Fixture.Rich("Second"), "second", 1)));
        Assert.Equal(2, saved.Version);
        Assert.Equal("Second", saved.Title);
        Assert.Equal(Fixture.Rich("Second"), saved.ContentJson);
        var unchanged = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Second", Fixture.Rich("Second"), "second", 2)));
        Assert.Equal(2, unchanged.Version);
        await Denied(409, "Stránku mezitím změnil jiný uživatel. Vaše změny zůstávají v editoru; načtěte aktuální verzi před dalším uložením.",
            () => f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Stale", Fixture.Rich("Stale"), "stale", 1))));
        await Denied(404, "Stránka nebyla nalezena.",
            () => f.As<PageDto>(f.Reader, new SavePage(page.Id, new("Reader", Fixture.Rich("Reader"), "reader", 2))));
        await Denied(404, "Stránka nebyla nalezena.",
            () => f.As<PageDto>(f.OtherWriter, new SavePage(page.Id, new("Foreign", Fixture.Rich("Foreign"), "foreign", 2))));
        await Denied(404, "Stránka nebyla nalezena.",
            () => f.As<PageDto>(f.Writer, new SavePage(Guid.NewGuid(), new("Missing", Fixture.Rich("Missing"), "missing", 1))));
        using var read = f.Host.OpenSession();
        var revisions = (await read.QueryOver<PageRevision>().Where(x => x.PageId == page.Id).OrderBy(x => x.ActiveFrom).Asc.ListAsync()).ToArray();
        Assert.Equal(2, revisions.Length);
        Assert.Equal(revisions[0].ActiveTo, revisions[1].ActiveFrom);
        Assert.Null(revisions[1].ActiveTo);
        Assert.All(revisions, revision => Assert.Equal(DateTimeKind.Utc, revision.ActiveFrom.Kind));
        Assert.Equal(f.Writer.Id, revisions[1].AuthorId);
    }

    [PostgreSqlFact]
    public async Task Create_and_save_store_the_servers_Markdown_projection_and_never_the_clients()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.As<PageDto>(f.Writer, new CreatePage(new(f.SpaceId, null, "Projection", Fixture.Rich("Created"), "forged on create")));
        Assert.Equal("Created", page.Markdown);
        var saved = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Projection", Fixture.Rich("Saved"), "forged on save", 1)));
        Assert.Equal("Saved", saved.Markdown);
        // Only the client's Markdown differs (or is missing): the same content, so no new revision.
        Assert.Equal(2, (await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Projection", Fixture.Rich("Saved"), null, 2)))).Version);
        // The same document with Czech letters escaped (\u0159, as a serializer with the default encoder writes it) is no change either.
        var escaped = Fixture.Rich("Řeřicha").Replace("Ř", "\\u0158", StringComparison.Ordinal).Replace("ř", "\\u0159", StringComparison.Ordinal);
        var plain = escaped.Replace("\\u0158", "Ř", StringComparison.Ordinal).Replace("\\u0159", "ř", StringComparison.Ordinal);
        Assert.Equal(3, (await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Projection", plain, null, 2)))).Version);
        var again = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Projection", escaped, null, 3)));
        Assert.Equal((3, plain), (again.Version, again.ContentJson));
        // Review W3-4: a revision saved before the Markdown writer changed is the same document ("Zahodit změny" sends it back): no new
        // revision, and its stored Markdown is the current writer's.
        await f.Host.ExecuteAsync("""UPDATE "PageRevision" SET "Markdown"='Rerisha (old writer)' WHERE "PageId"=:id AND "ActiveTo" IS NULL""", page.Id);
        var reset = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Projection", plain, null, 3)));
        Assert.Equal((3, "Řeřicha"), (reset.Version, reset.Markdown));
        Assert.Equal(3, await f.Scalar("""SELECT count(*) FROM "PageRevision" WHERE "PageId"=:id""", page.Id));
    }

    [PostgreSqlFact]
    public async Task A_save_stores_the_document_without_default_attributes_and_a_revision_stored_with_them_is_the_same_document()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Defaults");
        // As the editor saves it: the paragraph's attribute at its default.
        var full = Fixture.Rich("Text").Replace("{\"type\":\"paragraph\",", "{\"type\":\"paragraph\",\"attrs\":{\"textAlign\":null},", StringComparison.Ordinal);
        var saved = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Defaults", full, null, 1)));
        Assert.Equal((2, Fixture.Rich("Text")), (saved.Version, saved.ContentJson));
        // A revision stored before 3.6 with every default: the editor's save of that document is no change.
        // (Base64: the JSON's colons would read as SQL parameters.)
        await f.Host.ExecuteAsync($"""UPDATE "PageRevision" SET "ContentJson"=convert_from(decode('{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(full))}','base64'),'UTF8') WHERE "PageId"=:id AND "ActiveTo" IS NULL""", page.Id);
        Assert.Equal(2, (await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Defaults", full, null, 2)))).Version);
    }

    [PostgreSqlFact]
    public async Task A_legacy_title_with_a_control_character_still_saves_unchanged_is_listed_and_a_new_one_is_refused()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Legacy");
        await f.Execute("UPDATE \"PageRevision\" SET \"Title\"='Legacy' || chr(9) || 'tab' WHERE \"PageId\"=:id", page.Id);
        var saved = await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Legacy\ttab", Fixture.Rich("Changed body"), null, 1)));
        Assert.Equal((2, "Legacy\ttab"), (saved.Version, saved.Title));
        await Denied(400, "Pole Název stránky smí mít jen jeden řádek bez řídicích znaků.",
            () => f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Other\ttab", Fixture.Rich("Changed body"), null, 2))));
        var listed = Assert.Single((await f.As<ContentValidationReport>(f.Admin, new GetContentValidation(f.SpaceId))).Items);
        Assert.Equal((page.Id, "title"), (listed.PageId, listed.Path));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Publish_is_idempotent_appends_publication_history_and_makes_the_revision_visible_to_readers()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Published");
        var published = await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new(1)));
        Assert.Equal(1, published.PublishedVersion);
        Assert.True(published.IsPublished);
        Assert.Equal("Published", (await f.As<PageDto>(f.Reader, new GetPage(page.Id))).Title);
        Assert.Equal(1, (await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new(1)))).PublishedVersion);
        await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Working", Fixture.Rich("Working"), "working", 1)));
        Assert.Equal("Published", (await f.As<PageDto>(f.Reader, new GetPage(page.Id))).Title);
        await Denied(409, "Stránku mezitím změnil jiný uživatel. Vaše změny zůstávají v editoru; načtěte aktuální verzi před dalším uložením.",
            () => f.As<PageDto>(f.Writer, new PublishPage(page.Id, new(1))));
        var republished = await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new(2)));
        Assert.Equal(2, republished.PublishedVersion);
        Assert.Equal("Working", (await f.As<PageDto>(f.Reader, new GetPage(page.Id))).Title);
        using var read = f.Host.OpenSession();
        var publications = (await read.QueryOver<PagePublication>().Where(x => x.PageId == page.Id).OrderBy(x => x.ActiveFrom).Asc.ListAsync()).ToArray();
        Assert.Equal(2, publications.Length);
        Assert.Equal(publications[0].ActiveTo, publications[1].ActiveFrom);
        Assert.Null(publications[1].ActiveTo);
        Assert.All(publications, publication => Assert.Equal(f.Writer.Id, publication.PublisherId));
        Assert.All(publications, publication => Assert.Equal(DateTimeKind.Utc, publication.ActiveFrom.Kind));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Publish_stores_the_trimmed_change_summary_on_the_new_publication_and_rejects_invalid_text()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Summarized");
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new(1, "  Doplněn postup  ")));
        // The same revision again is the idempotent early return: nothing is created and the stored summary is not overwritten.
        await f.As<PageDto>(f.Writer, new PublishPage(page.Id, new(1, "Jiný popis")));
        Assert.Equal("Doplněn postup", Assert.Single(await Summaries(f, page.Id)));

        var blank = await f.Create("Blank");
        await f.As<PageDto>(f.Writer, new PublishPage(blank.Id, new(1, "   ")));
        Assert.Null(Assert.Single(await Summaries(f, blank.Id)));

        var invalid = await f.Create("Invalid");
        const string message = "Popis změny smí mít jeden řádek a nejvýše 500 znaků.";
        await Denied(400, message, () => f.As<PageDto>(f.Writer, new PublishPage(invalid.Id, new(1, new string('a', 501)))));
        await Denied(400, message, () => f.As<PageDto>(f.Writer, new PublishPage(invalid.Id, new(1, "první\ndruhý"))));
        await Denied(400, message, () => f.As<PageDto>(f.Writer, new PublishPage(invalid.Id, new(1, "první\rdruhý"))));
        Assert.Empty(await Summaries(f, invalid.Id));
        Assert.False((await f.As<PageDto>(f.Writer, new GetPage(invalid.Id))).IsPublished);
        await f.As<PageDto>(f.Writer, new PublishPage(invalid.Id, new(1, new string('a', 500))));
        Assert.Equal(new string('a', 500), Assert.Single(await Summaries(f, invalid.Id)));
    }

    [PostgreSqlFact]
    public async Task Create_rejects_a_parent_from_another_space_and_save_rejects_a_trashed_page()
    {
        await using var f = await Fixture.CreateAsync();
        var foreign = await f.CreateSpace("Other", f.Writer);
        var foreignParent = await f.As<PageDto>(f.Writer, new CreatePage(new(foreign, null, "Elsewhere", Fixture.Rich("Elsewhere"), "elsewhere")));
        Assert.Equal(foreign, foreignParent.SpaceId);
        await Denied(400, "Rodičovská stránka není dostupná v tomto prostoru.",
            () => f.As<PageDto>(f.Writer, new CreatePage(new(f.SpaceId, foreignParent.Id, "Cross space", Fixture.Rich("x"), "x"))));
        var page = await f.Create("Trashable");
        // CK_Page_Trash: a trashed row must carry all three trash columns.
        await f.Execute("UPDATE \"Page\" SET \"IsTrashed\"=true,\"TrashedAt\"=now(),\"TrashedById\"=\"OwnerId\",\"TrashBatchId\"=gen_random_uuid() WHERE \"Id\"=:id", page.Id);
        await Denied(404, "Stránka nebyla nalezena.",
            () => f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Revived", Fixture.Rich("Revived"), "revived", 1))));
        await Denied(404, "Stránka nebyla nalezena.",
            () => f.As<PageDto>(f.Writer, new PublishPage(page.Id, new(1))));
    }

    [PostgreSqlFact]
    public async Task Publish_rechecks_the_locked_version_even_when_the_session_already_loaded_the_page()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Approved");
        using var scope = f.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var transaction = session.BeginTransaction();
        Assert.Equal(page.Version, (await session.GetAsync<Page>(page.Id)).Version);

        await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Unapproved", Fixture.Rich("Unapproved"), "unapproved", page.Version)));
        var handler = ActivatorUtilities.CreateInstance<PublishPageHandler>(scope.ServiceProvider);
        var error = await Assert.ThrowsAsync<ApiException>(() => handler.Handle(new PublishPage(page.Id, new(page.Version)), default));
        Assert.Equal(409, error.Status);
        await transaction.RollbackAsync();
        Assert.Empty(await Summaries(f, page.Id));
        Assert.False((await f.As<PageDto>(f.Writer, new GetPage(page.Id))).IsPublished);
    }

    [PostgreSqlFact]
    public async Task Save_rechecks_the_locked_version_even_when_the_session_already_loaded_the_page()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Loaded");
        using var scope = f.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var transaction = session.BeginTransaction();
        Assert.Equal(page.Version, (await session.GetAsync<Page>(page.Id)).Version);

        await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Theirs", Fixture.Rich("Theirs"), "theirs", page.Version)));
        var handler = ActivatorUtilities.CreateInstance<SavePageHandler>(scope.ServiceProvider);
        var error = await Assert.ThrowsAsync<ApiException>(() => handler.Handle(new SavePage(page.Id, new("Mine", Fixture.Rich("Mine"), "mine", page.Version)), default));
        Assert.Equal((409, "version_conflict"), (error.Status, error.Code));
    }

    private static async Task<string?[]> Summaries(Fixture f, Guid pageId)
    {
        using var read = f.Host.OpenSession();
        return (await read.QueryOver<PagePublication>().Where(x => x.PageId == pageId).OrderBy(x => x.ActiveFrom).Asc.ListAsync())
            .Select(x => x.ChangeSummary).ToArray();
    }

    [PostgreSqlFact]
    public async Task Save_without_a_generation_refuses_over_unsaved_live_edits_of_others_and_keeps_their_log()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Shared");
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        var opened = await f.Collaboration.OpenAsync(page.Id, f.OtherWriter);
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.OtherWriter.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 2 });

        // Changed and identical content alike: both paths would reset the generation and delete the log.
        foreach (var (title, text) in new[] { ("Api", "api"), ("Shared", "Shared") })
        {
            var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Writer, new SavePage(page.Id, new(title, Fixture.Rich(title), text, 1))));
            Assert.Equal(409, refused.Status);
            Assert.Equal("collaboration_unsaved", refused.Code);
            Assert.DoesNotContain(f.OtherWriter.DisplayName, refused.Message);
        }
        Assert.Equal(1, (await f.Pages.GetAsync(page.Id, f.Writer, default)).Version);
        Assert.Equal(2L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));
        Assert.False(await f.Collaboration.HasExternalChangeAsync(opened.DocumentId));

        // Once the live editors saved, nothing unsaved remains and the external save resets the generation as before.
        await f.Execute("UPDATE \"PageCollaboration\" SET \"LastSavedSequence\"=\"Sequence\" WHERE \"PageId\"=:id", page.Id);
        Assert.Equal(2, (await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Api", Fixture.Rich("Api"), "api", 1)))).Version);
        Assert.True(await f.Collaboration.HasExternalChangeAsync(opened.DocumentId));
    }

    [PostgreSqlFact]
    public async Task Save_without_a_generation_supersedes_only_the_actors_own_unsaved_live_edits()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.Create("Mine");
        var opened = await f.Collaboration.OpenAsync(page.Id, f.Writer);
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 1 });
        await f.Collaboration.AppendUpdateAsync(opened.DocumentId, Guid.NewGuid(), f.Writer.Id.ToString(), DateTimeOffset.UtcNow, new byte[] { 2 });

        Assert.Equal(2, (await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Api", Fixture.Rich("Api"), "api", 1)))).Version);
        Assert.Equal(0L, await f.Scalar("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\"=:id", page.Id));
        Assert.Equal(1L, await f.Scalar($"SELECT count(*) FROM \"PageCollaboration\" WHERE \"PageId\"=:id AND \"Generation\"<>'{opened.Generation}' AND \"Sequence\"=0", page.Id));
        Assert.True(await f.Collaboration.HasExternalChangeAsync(opened.DocumentId));
        // A fresh generation without any update is no obstacle either.
        await f.Collaboration.OpenAsync(page.Id, f.Writer);
        Assert.Equal(3, (await f.As<PageDto>(f.Writer, new SavePage(page.Id, new("Again", Fixture.Rich("Again"), "again", 2)))).Version);
    }

    private static async Task Denied(int status, string message, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(status, error.Status);
        Assert.Equal(message, error.Message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly AtlasTestHost m_host;

        private Fixture(AtlasTestHost host)
        {
            m_host = host;
            Pages = new TestPages(host);
            Collaboration = new TestCollaboration(host);
        }

        public AtlasTestHost Host => m_host;
        public TestPages Pages { get; }
        public TestCollaboration Collaboration { get; }
        public UserAccount Admin { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("admin"), DisplayName = "Admin", IsWikiAdmin = true };
        public UserAccount Writer { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("writer"), DisplayName = "Writer" };
        public UserAccount OtherWriter { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("other"), DisplayName = "Other Writer" };
        public UserAccount Reader { get; } = new() { Id = Guid.NewGuid(), Username = TestLogin.For("reader"), DisplayName = "Reader" };
        public Guid SpaceId { get; } = Guid.NewGuid();

        public static string Rich(string text) => "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(await AtlasTestHost.CreateAsync("atlas_pages_"));
            await f.m_host.SeedAsync(async session =>
            {
                foreach (var user in new[] { f.Admin, f.Writer, f.OtherWriter, f.Reader })
                {
                    await session.SaveAsync(user);
                }
                await session.SaveAsync(new Space { Id = f.SpaceId, Name = "Pages" });
                await session.FlushAsync();
                foreach (var user in new[] { f.Writer, f.OtherWriter, f.Reader })
                {
                    await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = user.Id, Role = user == f.Reader ? 1 : 2 });
                }
            });
            return f;
        }

        public Task<PageDto> Create(string title, Guid? parentId = null) => Pages.CreateAsync(new(SpaceId, parentId, title, Rich(title), title), Writer, default);

        public async Task<Guid> CreateSpace(string name, params UserAccount[] writers)
        {
            var spaceId = Guid.NewGuid();
            await m_host.SeedAsync(async session =>
            {
                await session.SaveAsync(new Space { Id = spaceId, Name = name });
                await session.FlushAsync();
                foreach (var user in writers)
                {
                    await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = spaceId, UserId = user.Id, Role = 2 });
                }
            });
            return spaceId;
        }

        public Task<T> As<T>(UserAccount actor, object message)
        {
            m_host.Actor.Current = actor;
            return m_host.InvokeAsync<T>(message);
        }

        public Task Execute(string sql, Guid id) => m_host.ExecuteAsync(sql, id);

        public async Task<long> Scalar(string sql, Guid id)
        {
            using var session = m_host.OpenSession();
            return Convert.ToInt64(await session.CreateSQLQuery(sql).SetParameter("id", id).UniqueResultAsync());
        }

        public ValueTask DisposeAsync() => m_host.DisposeAsync();
    }
}
