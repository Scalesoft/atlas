using Atlas.Server.Features.Search.Contracts;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>S3 "Měli jste na mysli" (O5): trigram suggestions of readable published titles and aliases, without any model.</summary>
public sealed class DidYouMeanTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Suggests_only_titles_and_aliases_the_reader_may_read()
    {
        await using var f = await SearchFixture.CreateAsync();
        var hidden = await f.Host.SeedSpaceAsync("Tajné", (f.Writer, SpaceRole.Writer));
        await PublishAsync(f, "Připojení k VPN");
        // Closer to the typo than the readable title, and exactly the query: neither suggested nor a "strong result".
        await PublishAsync(f, "Připojení k VNP", hidden);
        var renamed = await PublishAsync(f, "Tiskárny ve firmě");
        var current = await f.Pages.GetAsync(renamed, f.Writer, default);
        var saved = await f.Pages.SaveAsync(renamed, new SavePageRequest("Návod k tisku", SearchFixture.Rich("Návod k tisku"), "Návod k tisku", current.Version), f.Writer, default);
        await f.Pages.PublishAsync(renamed, new PublishPageRequest(saved.Version), f.Writer, default);

        var typo = await f.Find(f.Reader, "pripojeni k vnp");
        Assert.Equal("Připojení k VPN", typo.DidYouMean);
        Assert.False(typo.Semantic);
        // An alias (a previous published title) is a suggestion too.
        Assert.Equal("Tiskárny ve firmě", (await f.Find(f.Reader, "tiskarny ve frme")).DidYouMean);
        // A strong result needs no suggestion; neither does a later page, the prefix pass or the drafts scope.
        Assert.Null((await f.Find(f.Reader, "Připojení k VPN")).DidYouMean);
        Assert.Null((await f.Find(f.Reader, "pripojeni k vnp", offset: 1)).DidYouMean);
        Assert.Null((await f.Find(f.Reader, "pripojeni k vnp", new SearchOptions(Content: "drafts"))).DidYouMean);
        f.Host.Actor.Current = f.Reader;
        var prefix = await f.Host.InvokeAsync<SearchResponse>(new Atlas.Server.Features.Search.Queries.Search("pripojeni k vnp", null, 0, 20, new SearchOptions()) { Fuzzy = false });
        Assert.Null(prefix.DidYouMean);
        // The writer of the hidden space has an exact title: no suggestion for them.
        Assert.Null((await f.Find(f.Writer, "pripojeni k vnp")).DidYouMean);
        // A correctly spelled part of a title is no misspelling: the search already finds the page.
        Assert.Null((await f.Find(f.Reader, "pripojeni")).DidYouMean);
        // Nothing readable is close: nothing suggested.
        Assert.Null((await f.Find(f.Reader, "kubernetes")).DidYouMean);
    }

    [PostgreSqlFact]
    public async Task Three_results_need_no_suggestion()
    {
        await using var f = await SearchFixture.CreateAsync();
        await PublishAsync(f, "Připojení k VPN");
        foreach (var title in new[] { "Připojení k VPN doma", "Připojení k VPN v kanceláři", "Připojení k VPN z mobilu" })
        {
            await PublishAsync(f, title);
        }
        var response = await f.Find(f.Reader, "pripojeni k vnp");
        Assert.True(response.Items.Count >= 3);
        Assert.Null(response.DidYouMean);
    }

    private static async Task<Guid> PublishAsync(SearchFixture f, string title, Guid? spaceId = null)
    {
        var page = await f.Pages.CreateAsync(new CreatePageRequest(spaceId ?? f.SpaceId, null, title, SearchFixture.Rich(title), title), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        return page.Id;
    }
}
