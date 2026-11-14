using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Tests.PersonalLibrary;
using Xunit;
using Atlas.Server.Features.PageTags.Queries;

namespace Atlas.Server.Tests.PageTags;

public sealed class PageTagHandlerTests
{
    [PostgreSqlFact]
    public async Task Tags_normalize_deduplicate_and_keep_temporal_assignments_without_document_edits()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Tagged");
        // Tags are one catalog of the wiki: names of the test's own, or another test's spelling of them would come back.
        var own = Token();
        var saved = await Save(f, page.Id, [$"  Team   Notes {own} ", $"team notes {own}", $"Ｔｅａｍ Notes {own}", $"Backend {own}"], []);
        Assert.Equal([$"Backend {own}", $"Team Notes {own}"], saved.Tags.Select(t => t.Name));
        Assert.Equal(saved.Tags, (await f.As<PageTagState>(f.Writer, new GetPageTags(page.Id))).Tags);
        var backend = saved.Tags[0];
        var kept = await Save(f, page.Id, [$"backend {own}"], saved.Tags.Select(t => t.Id).ToArray());
        Assert.Equal(backend, Assert.Single(kept.Tags));
        var readded = await Save(f, page.Id, [$"BACKEND {own}", $"team notes {own}"], [backend.Id]);
        Assert.Equal(saved.Tags, readded.Tags);
        using var session = f.Host.OpenSession();
        var history = await session.CreateSQLQuery("SELECT \"ActiveFrom\",\"ActiveTo\" FROM \"PageTag\" WHERE \"PageId\"=:id AND \"TagId\"=:tag ORDER BY \"ActiveFrom\"")
            .SetParameter("id", page.Id).SetParameter("tag", saved.Tags[1].Id).ListAsync<object[]>();
        Assert.Equal(2, history.Count);
        Assert.NotNull(history[0][1]);
        Assert.Null(history[1][1]);
        Assert.True((DateTime)history[0][1] < (DateTime)history[1][0]);
        Assert.Equal(1, (await session.GetAsync<Page>(page.Id)).Version);
        Assert.Equal(1L, await session.CreateSQLQuery("SELECT count(*) FROM \"PageRevision\" WHERE \"PageId\"=:id").SetParameter("id", page.Id).UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task Invalid_names_limits_and_stale_updates_leave_existing_tags_intact()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Validation");
        var state = await Save(f, page.Id, [new string('x', 48)], []);
        var ids = state.Tags.Select(t => t.Id).ToArray();
        foreach (var names in new[] { new[] { "" }, new[] { "bad\nname" }, new[] { "bad\u202ename" }, new[] { new string('x', 49) }, Enumerable.Range(0, 21).Select(i => "Tag " + i).ToArray(), new string[] { null! } })
            await Denied(400, () => Save(f, page.Id, names, ids));
        await Denied(400, () => Save(f, page.Id, null!, ids));
        await Denied(400, () => Save(f, page.Id, [], null!));
        await Denied(409, () => Save(f, page.Id, ["Replacement"], []));
        Assert.Equal(state.Tags, (await f.As<PageTagState>(f.Writer, new GetPageTags(page.Id))).Tags);
        var twenty = await Save(f, page.Id, Enumerable.Range(0, 20).Select(i => "Tag " + i).ToArray(), ids);
        Assert.Equal(20, twenty.Tags.Count);
    }

    [PostgreSqlFact]
    public async Task A_change_keeps_current_tags_by_id_even_with_a_legacy_name_today_refused()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Legacy");
        // The test renames the tag in the wiki's one catalog: names of its own.
        var own = Token();
        var legacy = Assert.Single((await Save(f, page.Id, [$"Legacy {own}"], [])).Tags);
        // A name stored before today's rules: re-normalizing it refused every add and remove (D-L1).
        await f.Execute("UPDATE \"Tag\" SET \"Name\"='Stará'||chr(10)||'značka' WHERE \"Id\"=:id", legacy.Id);

        var added = await f.As<PageTagState>(f.Writer, new ChangePageTags(page.Id, new([$"Nový {own}"], null, null)));
        Assert.Equal([legacy.Id, added.Tags.Single(x => x.Name == $"Nový {own}").Id], added.Tags.OrderBy(x => x.Name == $"Nový {own}").Select(x => x.Id));
        var removed = await f.As<PageTagState>(f.Writer, new ChangePageTags(page.Id, new(null, [$"nový {own}"], null)));
        Assert.Equal(legacy.Id, Assert.Single(removed.Tags).Id);
    }

    [PostgreSqlFact]
    public async Task Concurrent_edits_conflict_and_catalog_creation_is_shared_across_spaces()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Concurrency");
        f.Host.Actor.Current = f.Writer;
        var results = await Task.WhenAll(new[] { "One", "Two" }.Select(async name =>
        {
            try { await f.Host.InvokeAsync<PageTagState>(new SetPageTags(page.Id, new([name], []))); return 200; }
            catch (ApiException e) { return e.Status; }
        }));
        Assert.Equal([200, 409], results.Order());
        var otherSpace = await f.Host.SeedSpaceAsync("Other", (f.Writer, SpaceRole.Writer));
        var other = await f.Pages.CreateAsync(new(otherSpace, null, "Other", PersonalLibraryFixture.Rich("Other"), "Other"), f.Writer, default);
        var next = await f.Create("Next");
        var shared = await Task.WhenAll(Save(f, other.Id, ["Common"], []), Save(f, next.Id, ["COMMON"], []));
        Assert.Equal(shared[0].Tags[0].Id, shared[1].Tags[0].Id);
    }

    [PostgreSqlFact]
    public async Task Hidden_drafts_trash_and_revoked_access_never_disclose_tags()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Private");
        // The wiki administrator is suggested every test's tags: a name of the test's own, which the administrator asks for.
        var own = Token();
        var state = await Save(f, page.Id, [$"Secret {own}"], []);
        var tag = state.Tags[0];
        foreach (var actor in new[] { f.Reader, f.OtherWriter })
        {
            await Denied(404, () => f.As<PageTagState>(actor, new GetPageTags(page.Id)));
            Assert.Empty((await f.As<TagSuggestions>(actor, new SuggestTags(""))).Items);
            Assert.Empty((await f.As<SearchResponse>(actor, Browse(tag.Id))).Items);
        }
        await Denied(404, () => f.As<PageTagState>(f.OtherWriter, new SetPageTags(page.Id, new(["Leak"], [tag.Id]))));
        Assert.Single((await f.As<TagSuggestions>(f.Admin, new SuggestTags($"secret {own}"))).Items);
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        await Denied(403, () => f.As<PageTagState>(f.Reader, new SetPageTags(page.Id, new(["Reader edit"], [tag.Id]))));
        Assert.Single((await f.As<PageTagState>(f.Reader, new GetPageTags(page.Id))).Tags);
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=:id AND \"ActiveTo\" IS NULL", f.Reader.Id);
        Assert.Empty((await f.As<TagSuggestions>(f.Reader, new SuggestTags(""))).Items);
        Assert.Empty((await f.As<SearchResponse>(f.Reader, Browse(tag.Id))).Items);
        await Denied(404, () => f.As<PageTagState>(f.Reader, new GetPageTags(page.Id)));
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        await f.As<OrganizationChange>(f.Writer, new TrashPage(page.Id, new(false, tree.Version, 1)));
        Assert.Empty((await f.As<TagSuggestions>(f.Writer, new SuggestTags(""))).Items);
        Assert.Empty((await f.As<SearchResponse>(f.Writer, Browse(tag.Id))).Items);
        await Denied(404, () => f.As<PageTagState>(f.Writer, new GetPageTags(page.Id)));
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=:id", f.Admin.Id);
        await Denied(401, () => f.As<TagSuggestions>(f.Admin, new SuggestTags("")));
        await Denied(401, () => f.As<SearchResponse>(f.Admin, Browse(tag.Id)));
    }

    [PostgreSqlFact]
    public async Task Tagged_browse_respects_published_metadata_draft_filters_title_query_and_paging()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        var page = await f.Create("Public title");
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        var tag = (await Save(f, page.Id, ["Browse"], [])).Tags[0];
        await f.Pages.SaveAsync(page.Id, new("Confidential title", PersonalLibraryFixture.Rich("secret body"), "secret body", 1), f.Writer, default);
        var draft = await f.Create("Another draft");
        await Save(f, draft.Id, ["browse"], []);
        var reader = Assert.Single((await f.As<SearchResponse>(f.Reader, Browse(tag.Id))).Items);
        Assert.Equal("Public title", reader.Title);
        Assert.False(reader.IsDraft);
        Assert.Empty(reader.Snippet);
        Assert.Empty((await f.As<SearchResponse>(f.Reader, Browse(tag.Id, "Confidential"))).Items);
        Assert.Empty((await f.As<SearchResponse>(f.Reader, Browse(tag.Id, content: "drafts"))).Items);
        Assert.Equal("Public title", Assert.Single((await f.As<SearchResponse>(f.Writer, Browse(tag.Id, content: "published"))).Items).Title);
        Assert.Equal(2, (await f.As<SearchResponse>(f.Writer, Browse(tag.Id, content: "drafts"))).Items.Count);
        var hit = Assert.Single((await f.As<SearchResponse>(f.Writer, Browse(tag.Id, "Confidential"))).Items);
        Assert.Equal(page.Id, hit.Id);
        Assert.True(hit.IsDraft);
        Assert.Empty((await f.As<SearchResponse>(f.Writer, Browse(tag.Id, "secret body"))).Items);
        var first = await f.As<SearchResponse>(f.Writer, Browse(tag.Id) with { Limit = 1 });
        Assert.Equal(draft.Id, Assert.Single(first.Items).Id);
        Assert.True(first.HasMore);
        Assert.Equal(1, first.NextOffset);
        var second = await f.As<SearchResponse>(f.Writer, Browse(tag.Id) with { Offset = 1, Limit = 1 });
        Assert.Equal(page.Id, Assert.Single(second.Items).Id);
        Assert.False(second.HasMore);
        Assert.Empty((await f.As<SearchResponse>(f.Writer, Browse(Guid.NewGuid()))).Items);
        Assert.Empty((await f.As<SearchResponse>(f.Writer, Browse(tag.Id) with { SpaceId = Guid.NewGuid() })).Items);
        // Infinite results: past the old 1000 is an empty page, not an error.
        Assert.Empty((await f.As<SearchResponse>(f.Writer, Browse(tag.Id) with { Offset = 1001 })).Items);
        foreach (var bad in new[] { Browse(tag.Id) with { Offset = -1 }, Browse(tag.Id) with { Offset = int.MaxValue }, Browse(tag.Id) with { Limit = 51 }, Browse(tag.Id) with { Options = new("bad") } })
            await Denied(400, () => f.As<SearchResponse>(f.Writer, bad));
    }

    [PostgreSqlFact]
    public async Task Suggestions_apply_visibility_before_the_twenty_item_limit_and_escape_literal_queries()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        // The wiki administrator is suggested every test's tags: 23 names of the test's own, which the administrator asks for.
        var own = Token();
        var hidden = await f.Create("Hidden");
        await Save(f, hidden.Id, Enumerable.Range(0, 20).Select(i => $"A hidden {i} {own}").ToArray(), []);
        var visible = await f.Create("Visible", owner: f.OtherWriter);
        await f.Pages.PublishAsync(visible.Id, new(1), f.OtherWriter, default);
        var tags = await Save(f, visible.Id, [$"Z Visible {own}", $"100% Ready {own}", $"Under_score {own}"], []);
        Assert.Equal(3, (await f.As<TagSuggestions>(f.Reader, new SuggestTags(""))).Items.Count);
        Assert.Equal($"Z Visible {own}", Assert.Single((await f.As<TagSuggestions>(f.Reader, new SuggestTags(" ＶＩＳＩＢＬＥ "))).Items).Name);
        Assert.Equal($"100% Ready {own}", Assert.Single((await f.As<TagSuggestions>(f.Reader, new SuggestTags("%"))).Items).Name);
        Assert.Equal($"Under_score {own}", Assert.Single((await f.As<TagSuggestions>(f.Reader, new SuggestTags("_"))).Items).Name);
        Assert.Equal(20, (await f.As<TagSuggestions>(f.Admin, new SuggestTags(own))).Items.Count);
    }

    [PostgreSqlFact]
    public async Task Tagged_page_date_windows_and_date_order_use_visible_publication_metadata()
    {
        await using var f = await PersonalLibraryFixture.CreateAsync();
        Guid tagId = default;
        foreach (var (title, days) in new[] { ("Zulu", 0), ("Alpha", 5), ("Middle", 20) })
        {
            var page = await f.Create(title);
            await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
            tagId = (await Save(f, page.Id, ["Dated"], [])).Tags[0].Id;
            // Each fixture has one revision/publication, so moving its start preserves temporal integrity.
            await f.Execute($"UPDATE \"PageRevision\" SET \"ActiveFrom\"=clock_timestamp()-interval '{days} days' WHERE \"PageId\"=:id", page.Id);
            await f.Execute($"UPDATE \"PagePublication\" SET \"ActiveFrom\"=clock_timestamp()-interval '{days} days' WHERE \"PageId\"=:id", page.Id);
        }
        foreach (var (window, expected) in new[]
                 {
                     ("day", new[] { "Zulu" }),
                     ("week", new[] { "Zulu", "Alpha" }),
                     ("month", new[] { "Zulu", "Alpha", "Middle" })
                 })
        {
            var result = await f.As<SearchResponse>(f.Reader, Browse(tagId) with { Options = new("all", window, "published") });
            Assert.Equal(expected, result.Items.Select(item => item.Title));
            Assert.All(result.Items, item => Assert.Equal(item.PublishedAt, item.ChangedAt));
        }
        await Denied(400, () => f.As<SearchResponse>(f.Writer, Browse(tagId) with { Options = new("all", "year") }));
        await Denied(400, () => f.As<SearchResponse>(f.Writer, Browse(tagId) with { Options = new("all", "any", "unknown") }));
    }

    private static string Token() => Guid.NewGuid().ToString("N")[..8];

    private static Task<PageTagState> Save(PersonalLibraryFixture f, Guid page, string[] names, Guid[] expected) =>
        f.As<PageTagState>(f.Writer, new SetPageTags(page, new(names, expected)));

    private static ListTaggedPages Browse(Guid tag, string? query = null, string content = "all") =>
        new(tag, query, null, 0, 20, new(content));

    private static async Task Denied(int status, Func<Task> action) =>
        Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
}
