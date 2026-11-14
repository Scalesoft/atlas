using Atlas.Server.Features.Assistant.Agent;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>WP-14: the sources of an agent answer are only pages its tools returned; a sentence cites the ones it names.</summary>
public sealed class AgentAnswerTests
{
    [Fact]
    public void Sources_are_read_pages_and_named_search_hits_and_sentences_cite_the_titles_they_contain()
    {
        var vpn = new AssistantReturnedPage(Guid.NewGuid(), "Instalace VPN", false);
        var printers = new AssistantReturnedPage(Guid.NewGuid(), "Tiskárny", false);
        var unnamed = new AssistantReturnedPage(Guid.NewGuid(), "Wi-Fi pro hosty", false);
        var read = new AssistantReturnedPage(Guid.NewGuid(), "Pravidla IT", true);

        var result = AgentAnswer.Build("## Odpověď\nPostup je v **Instalace VPN** [1]. Viz také stránka Tajná.\n\nTiskárny nastavíte podle [Tiskárny](https://x).",
            [vpn, printers, unnamed, read, vpn with { Read = true }]);

        Assert.NotNull(result);
        // Order of first appearance; a page both found and read counts as read; an unnamed search hit is no source.
        Assert.Equal([vpn.PageId, printers.PageId, read.PageId], result.Sources.Select(page => page.PageId));
        Assert.Equal(["Odpověď", "Postup je v Instalace VPN.", "Viz také stránka Tajná.", "Tiskárny nastavíte podle Tiskárny."],
            result.Answer.Sentences.Select(sentence => sentence.Text));
        Assert.Equal([[], [1], [], [2]], result.Answer.Sentences.Select(sentence => sentence.Sources.ToArray()));
        Assert.Equal([1, 2], result.Cited);
        Assert.Null(AgentAnswer.Build(" \n** **\n", [vpn]));
    }

    /// <summary>Plan 3.5 I: an answer from projector_query or projector_waiting cites the zakázka pages their rows name; other projector
    /// results add no source.</summary>
    [Fact]
    public void Requirement_rows_add_their_pages_as_source_candidates()
    {
        var dubovice = Guid.NewGuid();
        var lhota = Guid.NewGuid();
        var results = new AssistantToolResults();
        results.Observe("projector_query", System.Text.Json.JsonDocument.Parse($$$"""
            {"rows":[{"pageId":"{{{dubovice}}}","pageTitle":"Dubovice — zakázka","requirement":{"blockId":"b1","title":"VPN"}}]}
            """).RootElement);
        results.Observe("projector_waiting", System.Text.Json.JsonDocument.Parse($$$"""
            {"parties":[{"party":"DODAVATEL","items":[{"pageId":"{{{lhota}}}","pageTitle":"Lhota — zakázka","blockId":"b2"}]}]}
            """).RootElement);
        results.Observe("projector_get_requirements", System.Text.Json.JsonDocument.Parse("{\"pageId\":\"" + Guid.NewGuid() + "\",\"pageTitle\":\"Jiná\"}").RootElement);

        Assert.Equal([(dubovice, "Dubovice — zakázka", false), (lhota, "Lhota — zakázka", false)],
            results.Pages.Select(page => (page.PageId, page.Title, page.Read)));
        var answer = AgentAnswer.Build("Na VPN se čeká u Dubovice — zakázka.", results.Pages);
        Assert.Equal([dubovice], answer!.Sources.Select(page => page.PageId));
    }
}
