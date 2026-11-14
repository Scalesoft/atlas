using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Search.Contracts;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>The default scope searches everything the actor may read: a freshly imported wiki has no published
/// revision yet, and a published-only default made every query in it answer "nothing found".</summary>
public sealed class UnifiedSearchScopeTests
{
    [PostgreSqlFact]
    public async Task Default_search_finds_a_never_published_page_by_a_word_in_its_title()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Create("Deployment in DEMO");

        var result = await f.Find(f.Writer, "demo");

        var hit = Assert.Single(result.Items);
        Assert.Equal(page.Id, hit.Id);
        Assert.True(hit.IsDraft);
    }

    [PostgreSqlFact]
    public async Task Default_search_returns_published_and_draft_pages_in_one_list()
    {
        await using var f = await SearchFixture.CreateAsync();
        var published = await f.Create("DEMO runbook");
        await f.Pages.PublishAsync(published.Id, new PublishPageRequest(published.Version), f.Writer, default);
        var draft = await f.Create("Deployment in DEMO");

        var result = await f.Find(f.Writer, "demo");

        Assert.Equal(new[] { draft.Id, published.Id }.OrderBy(id => id),
            result.Items.Select(item => item.Id).OrderBy(id => id));
    }

    [PostgreSqlFact]
    public async Task Default_search_returns_one_row_per_page_preferring_the_published_revision()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Create("Deployment in DEMO");
        var published = await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new SavePageRequest("Deployment in DEMO reworked",
            SearchFixture.Rich("Rollout notes"), "Rollout notes", published.Version), f.Writer, default);

        var result = await f.Find(f.Writer, "demo");

        var hit = Assert.Single(result.Items);
        Assert.Equal(page.Id, hit.Id);
        Assert.False(hit.IsDraft);
    }

    [PostgreSqlFact]
    public async Task Default_search_hides_a_never_published_page_from_a_reader_without_write_access()
    {
        await using var f = await SearchFixture.CreateAsync();
        await f.Create("Deployment in DEMO");

        var result = await f.Find(f.Reader, "demo");

        Assert.Empty(result.Items);
    }

    [PostgreSqlFact]
    public async Task Published_scope_still_excludes_a_never_published_page()
    {
        await using var f = await SearchFixture.CreateAsync();
        await f.Create("Deployment in DEMO");

        var result = await f.Find(f.Writer, "demo", new SearchOptions(Content: "published"));

        Assert.Empty(result.Items);
    }

    [PostgreSqlFact]
    public async Task Drafts_scope_still_excludes_a_page_whose_working_revision_is_the_published_one()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Create("Deployment in DEMO");
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);

        var result = await f.Find(f.Writer, "demo", new SearchOptions(Content: "drafts"));

        Assert.Empty(result.Items);
    }
}
