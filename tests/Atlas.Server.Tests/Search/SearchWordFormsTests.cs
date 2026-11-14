using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Tests.AiIndex;
using Xunit;
// Alias, not a plain using: inside namespace ...Tests.Search the bare name Search binds to the namespace.
using SearchMessage = Atlas.Server.Features.Search.Queries.Search;

namespace Atlas.Server.Tests.Search;

/// <summary>Migration 2026102030: search matches Czech word forms (a light stem beside the exact words) and the parts of
/// code identifiers, published and working, while the exact form still ranks first. Fuzzy is off where recall is the point:
/// trigram similarity alone already lets "smlouva" reach "smlouvy" at the lowest tier.</summary>
public sealed class SearchWordFormsTests
{
    [PostgreSqlFact]
    public async Task Czech_word_forms_find_each_other_and_the_exact_form_ranks_first()
    {
        await using var f = await HybridSearchFixture.CreateAsync();
        var exact = await f.PublishAsync("Dokument A", "Podepsaná smlouva leží u správce.");
        var inflected = await f.PublishAsync("Dokument B", "Kopie smlouvy a práce se smlouvou.");
        var printer = await f.PublishAsync("Nástup", "Zaměstnanec dostane tiskárnu.");
        var unrelated = await f.PublishAsync("Kuchyň", "Kolo a kolotoč.");
        var draft = (await f.Index.Pages.CreateAsync(new CreatePageRequest(f.Index.SpaceId, null, "Koncept",
            IndexFixture.Rich("Tiskárny v kanceláři."), "Tiskárny v kanceláři."), f.Writer, default)).Id;

        Assert.Equal([exact, inflected], await Lexical(f, f.Reader, "smlouva"));
        Assert.Equal([inflected, exact], await Lexical(f, f.Reader, "smlouvy"));
        Assert.Equal([printer], await Lexical(f, f.Reader, "tiskárna"));
        Assert.Equal([printer], await Lexical(f, f.Reader, "tiskárnou"));
        Assert.Equal([draft], await Lexical(f, f.Writer, "tiskárna", "drafts"));
        Assert.Equal([printer, draft], await Lexical(f, f.Writer, "tiskárnu", "all"));
        // A quoted phrase stays exact; a word of four letters or fewer is not stemmed ("kola" does not match "kolo").
        Assert.Empty(await Lexical(f, f.Reader, "\"tiskárna\""));
        Assert.Empty(await Lexical(f, f.Reader, "kola"));
        Assert.Equal([unrelated], await Lexical(f, f.Reader, "kolo"));
        // With fuzzy matching on (the web's default) the inflected page still ranks with its word, above trigram-only hits.
        Assert.Equal([exact, inflected], (await f.InvokeAsync(f.Reader, "smlouva", null)).Items.Select(x => x.Id));
    }

    [PostgreSqlFact]
    public async Task Code_identifiers_are_found_by_their_parts_and_their_joined_form()
    {
        await using var f = await HybridSearchFixture.CreateAsync();
        var calendar = await f.PublishAsync("Kalendář", "Metoda getFreeBusy vrací obsazenost, HTTPServer ji volá.");
        var export = await f.PublishAsync("Export", "Volání export_page_pdf a příkaz render-cover-image vytvoří soubor.");

        foreach (var query in new[] { "freebusy", "free busy", "\"free busy\"", "getFreeBusy", "busy", "get free", "http server" })
        {
            Assert.Equal([calendar], await Lexical(f, f.Reader, query));
        }
        foreach (var query in new[] { "exportpagepdf", "pagepdf", "export_page_pdf", "page pdf", "rendercoverimage", "coverimage", "render-cover-image" })
        {
            Assert.Equal([export], await Lexical(f, f.Reader, query));
        }
    }

    [PostgreSqlFact]
    public async Task Headings_and_previous_titles_match_by_word_form()
    {
        await using var f = await HybridSearchFixture.CreateAsync();
        var page = await f.PublishAsync("Provoz", "## Tiskárny v kanceláři\nDvě barevné.", null, ("Tiskárny v kanceláři", "tiskarny"));
        var renamed = await f.PublishAsync("Smlouvy dodavatelů", "Seznam.");
        await f.Index.EditAndPublishAsync(renamed, "Dodavatelé", "Seznam.");
        await f.IndexAsync();

        foreach (var response in new[] { await f.InvokeAsync(f.Reader, "tiskárna", null), await f.FindAsync(f.Reader, "tiskárna") })
        {
            var hit = Assert.Single(response.Items, x => x.Id == page);
            Assert.Equal("Tiskárny v kanceláři", hit.HeadingTitle);
        }
        foreach (var response in new[] { await f.InvokeAsync(f.Reader, "smlouva", null), await f.FindAsync(f.Reader, "smlouva") })
        {
            Assert.Equal("Smlouvy dodavatelů", Assert.Single(response.Items, x => x.Id == renamed).MatchedAlias);
        }
    }

    /// <summary>K9 (review cycle 1): a whole question ("Na co čekáme v KBN?") has words no page has; when every word together finds
    /// nothing, search falls back to its significant words OR'ed. A query that finds something, on any page, stays as it is.</summary>
    [PostgreSqlFact]
    public async Task A_question_that_matches_nothing_falls_back_to_its_significant_words()
    {
        await using var f = await HybridSearchFixture.CreateAsync();
        var kbn = await f.PublishAsync("KBN — Production", "Stav nasazení: čeká na SMS bránu od IT KBN.");
        var gateway = await f.PublishAsync("SMS brána", "Obecný postup pro SMS bránu.");
        await f.PublishAsync("Kuchyň", "Kolo a kolotoč.");

        Assert.Equal([kbn], await Lexical(f, f.Reader, "Na co čekáme v KBN?"));
        Assert.Equivalent(new[] { kbn, gateway }, await Lexical(f, f.Reader, "Kdo má dodat SMS bránu pro KBN?"), strict: true);
        Assert.Equal([kbn], (await f.InvokeAsync(f.Reader, "Na co čekáme v KBN?", null)).Items.Select(x => x.Id));
        Assert.Equal([kbn], await Lexical(f, f.Reader, "SMS KBN"));
        // The next page of a query that found something stays empty instead of relaxing.
        Assert.Empty(await Lexical(f, f.Reader, "SMS KBN", offset: 1));
        Assert.Empty(await Lexical(f, f.Reader, "\"čekáme v KBN\""));
    }

    private static async Task<Guid[]> Lexical(HybridSearchFixture f, UserAccount actor, string query, string content = "published", int offset = 0)
        => (await f.Index.As<SearchResponse>(actor, new SearchMessage(query, null, offset, 20, new SearchOptions(Content: content)) { Fuzzy = false }))
            .Items.Select(x => x.Id).ToArray();
}
