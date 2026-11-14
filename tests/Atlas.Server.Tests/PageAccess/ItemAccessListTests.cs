using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Features.ContentExport.Queries;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Queries;
using Atlas.Server.Features.PersonalLibrary;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Spaces.Contracts;
using Atlas.Server.Features.Spaces.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.PageAccess.ItemAccessSeed;
using SearchQuery = Atlas.Server.Features.Search.Queries.Search;
using Atlas.Server.Features.PageTags.Queries;

namespace Atlas.Server.Tests.PageAccess;

/// <summary>S3: every list and search path filters by EffectivePageAccess. An own-access page leaks no title, snippet, tag or count
/// to a space Writer without a page grant, an account whose only access is a page grant sees exactly that page (and its space's
/// name, icon and colour, D8), and a page whose parent is hidden is listed at the root. S4: copy-on-create, cross-space reset.</summary>
public sealed class ItemAccessListTests
{
    private const string Secret = "Zirkonium";

    [PostgreSqlFact]
    public async Task Tree_shows_only_listed_pages_with_a_per_page_editor_flag_and_hidden_parents_at_the_root()
    {
        await using var f = await Fixture.CreateAsync();

        var writer = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        Assert.Equivalent(new[] { f.Shared, f.Orphan }, writer.Items.Select(x => x.Id));
        Assert.DoesNotContain(writer.Items, x => x.Id == f.Own || x.Title!.Contains(Secret));
        // D8: the child of the hidden page is at the root, without a placeholder.
        Assert.Null(writer.Items.Single(x => x.Id == f.Orphan).ParentId);

        var reader = Assert.Single((await f.As<PageTreeDto>(f.PageReader, new GetPageTree(f.SpaceId))).Items);
        Assert.Equal((f.Own, $"{Secret} published", false), (reader.Id, reader.Title, reader.CanEdit));
        var pageWriter = Assert.Single((await f.As<PageTreeDto>(f.PageWriter, new GetPageTree(f.SpaceId))).Items);
        Assert.Equal((f.Own, $"{Secret} working", true), (pageWriter.Id, pageWriter.Title, pageWriter.CanEdit));

        var admin = await f.As<PageTreeDto>(f.Admin, new GetPageTree(f.SpaceId));
        Assert.Equal(f.Own, admin.Items.Single(x => x.Id == f.Orphan).ParentId);
        Assert.Equal(404, await f.StatusAsync<PageTreeDto>(f.Outsider, new GetPageTree(f.SpaceId)));
    }

    [PostgreSqlFact]
    public async Task Space_pages_and_personal_api_pages_filter_before_the_window()
    {
        await using var f = await Fixture.CreateAsync();

        var writer = (await f.As<SpacePages>(f.Writer, new GetSpacePages(f.SpaceId))).Items;
        Assert.Equal(2, writer.Count);
        Assert.DoesNotContain(writer, x => x.Id == f.Own || x.Title!.Contains(Secret));
        Assert.Null(writer.Single(x => x.Id == f.Orphan).ParentId);

        var reader = Assert.Single((await f.As<SpacePages>(f.PageReader, new GetSpacePages(f.SpaceId))).Items);
        Assert.Equal($"{Secret} published", reader.Title);
        Assert.Equal($"{Secret} working", Assert.Single((await f.As<SpacePages>(f.PageWriter, new GetSpacePages(f.SpaceId))).Items).Title);
        Assert.Equal(404, await f.StatusAsync<SpacePages>(f.Outsider, new GetSpacePages(f.SpaceId)));

        // The window counts only listed pages: one page per request, and the hidden page never takes a slot.
        var first = await f.As<ApiList<ApiPageEntry>>(f.Writer, new ListApiSpacePages(f.SpaceId, 0, 1));
        var second = await f.As<ApiList<ApiPageEntry>>(f.Writer, new ListApiSpacePages(f.SpaceId, 1, 1));
        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equivalent(new[] { f.Shared, f.Orphan }, first.Items.Concat(second.Items).Select(x => x.Id));
        var api = await f.As<ApiList<ApiPageEntry>>(f.PageReader, new ListApiSpacePages(f.SpaceId, 0, 10));
        Assert.Equal(f.Own, Assert.Single(api.Items).Id);
        Assert.False(api.HasMore);
        Assert.Equal(404, await f.StatusAsync<ApiList<ApiPageEntry>>(f.Outsider, new ListApiSpacePages(f.SpaceId, 0, 10)));
    }

    [PostgreSqlFact]
    public async Task Item_only_accounts_see_the_space_name_icon_and_colour_only()
    {
        await using var f = await Fixture.CreateAsync();

        var full = Assert.Single((await f.As<AccessibleSpaces>(f.Writer, new GetAccessibleSpaces())).Items);
        Assert.Equal(("Writer", "Space description", false), (full.Role, full.Description, full.ItemAccessOnly));
        var limited = Assert.Single((await f.As<AccessibleSpaces>(f.PageReader, new GetAccessibleSpaces())).Items);
        Assert.Equal((f.SpaceId, "Reader", "", true), (limited.Id, limited.Role, limited.Description, limited.ItemAccessOnly));
        Assert.Empty((await f.As<AccessibleSpaces>(f.Outsider, new GetAccessibleSpaces())).Items);

        var api = Assert.Single((await f.As<ApiList<ApiSpace>>(f.PageWriter, new ListApiSpaces(null, null))).Items);
        Assert.Equal(("", true), (api.Description, api.ItemAccessOnly));
        Assert.Empty((await f.As<ApiList<ApiSpace>>(f.Outsider, new ListApiSpaces(null, null))).Items);

        // A grant on a trashed page alone does not reveal the space.
        await f.As<OrganizationChange>(f.Admin, new TrashPage(f.Own, new(true, await f.TreeVersionAsync(), await f.PageVersionAsync(f.Own))));
        Assert.Empty((await f.As<AccessibleSpaces>(f.PageReader, new GetAccessibleSpaces())).Items);
        Assert.Equal(404, await f.StatusAsync<PageTreeDto>(f.PageReader, new GetPageTree(f.SpaceId)));
    }

    [PostgreSqlFact]
    public async Task Every_search_scope_and_the_api_search_hide_pages_without_a_page_role()
    {
        await using var f = await Fixture.CreateAsync();
        var scopes = new[] { new SearchOptions(), new("published"), new("published", "week"), new("published", "any", "published"), new("drafts") };

        foreach (var scope in scopes)
        {
            var hits = await f.As<SearchResponse>(f.Writer, new SearchQuery(Secret, null, 0, 20, scope));
            Assert.DoesNotContain(hits.Items, x => x.Id == f.Own || x.Title!.Contains(Secret) || x.Snippet.Contains(Secret));
        }
        Assert.Equal(f.Own, Assert.Single((await f.As<SearchResponse>(f.PageReader, new SearchQuery(Secret, null, 0, 20, new("published")))).Items).Id);
        Assert.Equal(f.Own, Assert.Single((await f.As<SearchResponse>(f.PageReader, new SearchQuery(Secret, f.SpaceId, 0, 20, new()))).Items).Id);
        // Only a page writer searches the working revision.
        Assert.Empty((await f.As<SearchResponse>(f.PageReader, new SearchQuery(Secret, null, 0, 20, new("drafts")))).Items);
        var draft = Assert.Single((await f.As<SearchResponse>(f.PageWriter, new SearchQuery(Secret, null, 0, 20, new("drafts")))).Items);
        Assert.Equal($"{Secret} working", draft.Title);

        // Space suggestions: D8 shows the space to an item-only account, nothing to an outsider.
        Assert.Equal(f.SpaceId, Assert.Single((await f.As<SearchResponse>(f.PageReader, new SearchQuery("Listspace", null, 0, 20, new()))).Spaces).Id);
        Assert.Empty((await f.As<SearchResponse>(f.Outsider, new SearchQuery("Listspace", null, 0, 20, new()))).Spaces);
        Assert.Empty((await f.As<SearchResponse>(f.Outsider, new SearchQuery(Secret, null, 0, 20, new()))).Items);
    }

    [PostgreSqlFact]
    public async Task Tags_library_and_analytics_follow_the_page_role()
    {
        await using var f = await Fixture.CreateAsync();
        var tag = Assert.Single((await f.As<PageTagState>(f.PageWriter, new SetPageTags(f.Own, new([Secret], [])))).Tags);
        await f.As<PersonalPageState>(f.PageReader, new RecordPageVisit(f.Own));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Europe/Prague"));

        Assert.Empty((await f.As<TagSuggestions>(f.Writer, new SuggestTags(Secret.ToLowerInvariant()))).Items);
        Assert.Empty((await f.As<SearchResponse>(f.Writer, new ListTaggedPages(tag.Id, null, null, 0, 20, new()))).Items);
        Assert.Single((await f.As<TagSuggestions>(f.PageReader, new SuggestTags(Secret.ToLowerInvariant()))).Items);
        Assert.Equal($"{Secret} published", Assert.Single((await f.As<SearchResponse>(f.PageReader, new ListTaggedPages(tag.Id, null, null, 0, 20, new()))).Items).Title);

        var writerLibrary = (await f.As<PersonalLibraryList>(f.Writer, new ListPersonalLibrary("changes", 0, 20))).Items;
        Assert.DoesNotContain(writerLibrary, x => x.Id == f.Own || x.Title!.Contains(Secret));
        Assert.Null(writerLibrary.Single(x => x.Id == f.Orphan).ParentId);
        // The parent's title goes with the same visibility: hidden for the writer, the working one for the admin.
        Assert.Null(writerLibrary.Single(x => x.Id == f.Orphan).ParentTitle);
        var adminLibrary = (await f.As<PersonalLibraryList>(f.Admin, new ListPersonalLibrary("changes", 0, 20))).Items;
        Assert.Equal($"{Secret} working", adminLibrary.Single(x => x.Id == f.Orphan).ParentTitle);
        var readerLibrary = Assert.Single((await f.As<PersonalLibraryList>(f.PageReader, new ListPersonalLibrary("changes", 0, 20))).Items);
        Assert.Equal(($"{Secret} published", false), (readerLibrary.Title, readerLibrary.HasUnpublishedChanges));

        Assert.DoesNotContain((await f.As<PopularPages>(f.Writer, new GetPopularPages(today, today))).Items, x => x.PageId == f.Own);
        Assert.Equal(f.Own, Assert.Single((await f.As<PopularPages>(f.PageReader, new GetPopularPages(today, today))).Items).PageId);
        Assert.DoesNotContain((await f.As<ActivityChanges>(f.Writer, new GetActivityChanges(today, "wiki"))).Items, x => x.PageId == f.Own);
        Assert.Contains((await f.As<ActivityChanges>(f.PageWriter, new GetActivityChanges(today, "wiki"))).Items, x => x.PageId == f.Own);
    }

    [PostgreSqlFact]
    public async Task Whole_space_export_selects_only_readable_pages()
    {
        await using var f = await Fixture.CreateAsync();

        await using (var writer = await f.As<ContentExportArchive>(f.Writer, new BuildContentExport(f.SpaceId, true)))
        {
            Assert.Equivalent(new[] { f.Shared, f.Orphan }, writer.Selected.Select(x => x.PageId));
            await f.As(f.Writer, new RevalidateContentExport(f.SpaceId, writer.Selected, WholeSpace: true));
        }
        await using (var reader = await f.As<ContentExportArchive>(f.PageReader, new BuildContentExport(f.SpaceId, true)))
        {
            var selected = Assert.Single(reader.Selected);
            Assert.Equal((f.Own, await f.PublishedRevisionAsync(f.Own)), (selected.PageId, selected.RevisionId));
            await f.As(f.PageReader, new RevalidateContentExport(f.SpaceId, reader.Selected, WholeSpace: true));
            // The page grant ends before the archive leaves the server.
            await f.CloseGrantsAsync(f.Own, f.PageReader);
            Assert.Equal(404, await f.StatusAsync(f.PageReader, new RevalidateContentExport(f.SpaceId, reader.Selected, WholeSpace: true)));
        }
        await using (var pageWriter = await f.As<ContentExportArchive>(f.PageWriter, new BuildContentExport(f.SpaceId, true)))
            Assert.NotEqual(await f.PublishedRevisionAsync(f.Own), Assert.Single(pageWriter.Selected).RevisionId);
        Assert.Equal(404, await f.StatusAsync<ContentExportArchive>(f.Outsider, new BuildContentExport(f.SpaceId, true)));
        Assert.Equal(404, await f.StatusAsync(f.Outsider, new RevalidateContentExport(f.SpaceId, [], WholeSpace: true)));
    }

    [PostgreSqlFact]
    public async Task A_subpage_copies_its_parents_access_mode_and_grants()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Grant(f.Own, f.Temporary, 2, expiresAt: "now()+interval '3 days'");

        var child = await f.As<PageDto>(f.PageWriter, Create(f.SpaceId, f.Own, "Own child"));
        Assert.True(await f.OwnAccessAsync(child.Id));
        Assert.Equal(await f.GrantsAsync(f.Own), await f.GrantsAsync(child.Id));
        Assert.Equal(404, await f.StatusAsync<PageDto>(f.Writer, new GetPage(child.Id)));
        Assert.True((await f.As<PageDto>(f.PageWriter, new GetPage(child.Id))).CanEdit);
        await f.As<PageDto>(f.PageWriter, new PublishPage(child.Id, new(child.Version)));
        Assert.False((await f.As<PageDto>(f.PageReader, new GetPage(child.Id))).CanEdit);
        Assert.True((await f.As<PageDto>(f.Temporary, new GetPage(child.Id))).CanEdit);

        // A root page and a subpage of a page without grants start with the space defaults.
        var root = await f.As<PageDto>(f.Writer, Create(f.SpaceId, null, "Root"));
        var shared = await f.As<PageDto>(f.Writer, Create(f.SpaceId, f.Shared, "Shared child"));
        foreach (var page in new[] { root.Id, shared.Id })
        {
            Assert.False(await f.OwnAccessAsync(page));
            Assert.Empty(await f.GrantsAsync(page));
        }
    }

    [PostgreSqlFact]
    public async Task A_page_grant_only_editor_can_create_a_subpage_and_read_it()
    {
        await using var f = await Fixture.CreateAsync();
        // A default-access page shared with the page-only account: the space Writer and the page writer both edit it.
        await f.Grant(f.Shared, f.PageWriter, 2);

        var child = await f.As<PageDto>(f.PageWriter, Create(f.SpaceId, f.Shared, "Contractor page"));
        Assert.False(await f.OwnAccessAsync(child.Id));
        Assert.True((await f.As<PageDto>(f.PageWriter, new GetPage(child.Id))).CanEdit);
        var tree = await f.As<PageTreeDto>(f.PageWriter, new GetPageTree(f.SpaceId));
        Assert.Equal(f.Shared, tree.Items.Single(x => x.Id == child.Id).ParentId);
        Assert.Equal(404, await f.StatusAsync<PageDto>(f.PageWriter, Create(f.SpaceId, null, "Root")));
    }

    [PostgreSqlFact]
    public async Task Moves_keep_the_mode_within_a_space_and_reset_item_access_across_spaces()
    {
        await using var f = await Fixture.CreateAsync();
        var draft = await f.As<PageDto>(f.PageWriter, Create(f.SpaceId, f.Own, "Own child"));
        var child = await f.As<PageDto>(f.PageWriter, new PublishPage(draft.Id, new(draft.Version)));
        await f.As<OrganizationChange>(f.Admin, new MovePage(f.Shared, new(f.Own, null, await f.TreeVersionAsync())));
        Assert.False(await f.OwnAccessAsync(f.Shared));
        Assert.True(await f.OwnAccessAsync(child.Id));

        var target = await f.Host.SeedSpaceAsync("target", (f.Admin, SpaceRole.Admin));
        await f.As<OrganizationChange>(f.Admin, new MovePage(f.Shared, new(null, null, await f.TreeVersionAsync())));
        await f.As<OrganizationChange>(f.Admin, new MovePage(f.Orphan, new(null, null, await f.TreeVersionAsync())));
        var moved = await f.As<CrossSpaceChange>(f.Admin, await f.MoveToSpaceAsync(f.Own, target));
        Assert.Equivalent(new[] { f.Own, child.Id }, moved.AffectedIds);
        foreach (var page in moved.AffectedIds)
        {
            Assert.False(await f.OwnAccessAsync(page));
            Assert.Empty(await f.GrantsAsync(page));
        }
        Assert.Equal(404, await f.StatusAsync<PageDto>(f.PageReader, new GetPage(f.Own)));
        Assert.Empty((await f.As<AccessibleSpaces>(f.PageReader, new GetAccessibleSpaces())).Items);
        Assert.Equal(4L, await f.ScalarAsync($"""
            SELECT count(*) FROM "AccessAuditEvent" e JOIN "ResourceGrant" g ON g."Id"=e."ResourceGrantId"
            WHERE e."Action"='resource_grant.removed' AND g."PageId" IN ('{f.Own}','{child.Id}')
            """));
    }

    private static CreatePage Create(Guid spaceId, Guid? parentId, string title) =>
        new(new CreatePageRequest(spaceId, parentId, title, Fixture.Rich(title), title));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly NpgsqlConnection m_connection;

        private Fixture(AtlasTestHost host, NpgsqlConnection connection)
        {
            Host = host;
            m_connection = connection;
        }

        public AtlasTestHost Host { get; }
        public UserAccount Admin { get; private set; } = null!;
        public UserAccount Writer { get; private set; } = null!;
        public UserAccount Outsider { get; private set; } = null!;
        /// <summary>No space grant: a direct page grant on <see cref="Own"/> is the only access of these accounts.</summary>
        public UserAccount PageReader { get; private set; } = null!;
        public UserAccount PageWriter { get; private set; } = null!;
        public UserAccount Temporary { get; private set; } = null!;
        public Guid SpaceId { get; private set; }
        /// <summary>Published, space defaults.</summary>
        public Guid Shared { get; private set; }
        /// <summary>Own access, published as "Zirkonium published", working "Zirkonium working"; PageReader 1, PageWriter 2.</summary>
        public Guid Own { get; private set; }
        /// <summary>Published default-access child of <see cref="Own"/>: its parent is hidden from the space Writer.</summary>
        public Guid Orphan { get; private set; }

        public static string Rich(string text) =>
            "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

        public static async Task<Fixture> CreateAsync()
        {
            // The whole-space export writes under the host's content root (ContentExportFiles), shared by the tests of this host.
            var host = await AtlasTestHost.CreateAsync("atlas_item_lists_", services => services.AddSingleton<ContentExportFiles>(), sharedAs: "item_lists");
            var f = new Fixture(host, await OpenAsync(host))
            {
                Admin = await host.SeedUserAsync("admin"),
                Writer = await host.SeedUserAsync("writer"),
                Outsider = await host.SeedUserAsync("outsider"),
                PageReader = await host.SeedUserAsync("pagereader"),
                PageWriter = await host.SeedUserAsync("pagewriter"),
                Temporary = await host.SeedUserAsync("temporary"),
            };
            f.SpaceId = await host.SeedSpaceAsync("Listspace", (f.Admin, SpaceRole.Admin), (f.Writer, SpaceRole.Writer));
            await ExecuteAsync(f.m_connection, $"UPDATE \"Space\" SET \"Description\"='Space description' WHERE \"Id\"='{f.SpaceId}'");
            f.Shared = await f.PublishedAsync("Shared page", null);
            f.Own = await f.PublishedAsync($"{Secret} published", null);
            f.Orphan = await f.PublishedAsync("Orphan page", f.Own);
            await ExecuteAsync(f.m_connection, $"UPDATE \"Page\" SET \"OwnAccess\"=true WHERE \"Id\"='{f.Own}'");
            await f.Grant(f.Own, f.PageReader, 1);
            await f.Grant(f.Own, f.PageWriter, 2);
            var own = await f.As<PageDto>(f.Admin, new GetPage(f.Own));
            await f.As<PageDto>(f.Admin, new SavePage(f.Own, new SavePageRequest($"{Secret} working", Rich($"{Secret} working body"), $"{Secret} working body", own.Version)));
            return f;
        }

        private async Task<Guid> PublishedAsync(string title, Guid? parentId)
        {
            var page = await As<PageDto>(Admin, Create(SpaceId, parentId, title));
            return (await As<PageDto>(Admin, new PublishPage(page.Id, new(page.Version)))).Id;
        }

        public Task Grant(Guid pageId, UserAccount user, int role, string expiresAt = "NULL") =>
            GrantAsync(m_connection, pageId, user.Id, null, role, "NULL", expiresAt);

        public Task CloseGrantsAsync(Guid pageId, UserAccount user) =>
            ExecuteAsync(m_connection, $"UPDATE \"ResourceGrant\" SET \"ActiveTo\"=now() WHERE \"PageId\"='{pageId}' AND \"UserId\"='{user.Id}' AND \"ActiveTo\" IS NULL");

        public async Task<bool> OwnAccessAsync(Guid pageId) =>
            (bool)(await ExecuteAsync(m_connection, $"SELECT \"OwnAccess\" FROM \"Page\" WHERE \"Id\"='{pageId}'"))!;

        /// <summary>Current grants of a page as comparable "subject:role:validity" strings.</summary>
        public async Task<string[]> GrantsAsync(Guid pageId)
        {
            var rows = await ExecuteAsync(m_connection, $"""
                SELECT coalesce(string_agg(concat_ws(':',"UserId","GroupId","Role","StartAt","ExpiresAt"),'|' ORDER BY "UserId","GroupId"),'')
                FROM "ResourceGrant" WHERE "PageId"='{pageId}' AND "ActiveTo" IS NULL
                """);
            return ((string)rows!).Split('|', StringSplitOptions.RemoveEmptyEntries);
        }

        public Task<object?> ScalarAsync(string sql) => ExecuteAsync(m_connection, sql);

        public async Task<Guid> PublishedRevisionAsync(Guid pageId)
        {
            using var session = Host.OpenSession();
            return (await session.GetAsync<Page>(pageId))!.PublishedRevisionId!.Value;
        }

        public async Task<long> TreeVersionAsync()
        {
            using var session = Host.OpenSession();
            return await TreeVersion.ReadAsync(session, SpaceId, default);
        }

        public async Task<int> PageVersionAsync(Guid pageId)
        {
            using var session = Host.OpenSession();
            return (await session.GetAsync<Page>(pageId))!.Version;
        }

        public async Task<MovePageToSpace> MoveToSpaceAsync(Guid pageId, Guid target)
        {
            using var session = Host.OpenSession();
            var page = (await session.GetAsync<Page>(pageId))!;
            return new MovePageToSpace(pageId, new MovePageSpaceRequest(target, null, null, true,
                await TreeVersion.ReadAsync(session, page.SpaceId, default), await TreeVersion.ReadAsync(session, target, default),
                page.Version, true));
        }

        public Task<T> As<T>(UserAccount actor, object message)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync<T>(message);
        }

        public Task As(UserAccount actor, object message)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync(message);
        }

        public async Task<int> StatusAsync<T>(UserAccount actor, object message)
        {
            try
            {
                var result = await As<T>(actor, message);
                if (result is IAsyncDisposable disposable)
                    await disposable.DisposeAsync();
                return 200;
            }
            catch (ApiException error)
            {
                return error.Status;
            }
        }

        public async Task<int> StatusAsync(UserAccount actor, object message)
        {
            try
            {
                await As(actor, message);
                return 200;
            }
            catch (ApiException error)
            {
                return error.Status;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await m_connection.DisposeAsync();
            await Host.DisposeAsync();
        }
    }
}
