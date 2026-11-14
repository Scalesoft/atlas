using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Settings;
using Xunit;

namespace Atlas.Server.Tests.AiSuggestions;

/// <summary>E4 (design §7.5 B): the <c>[[</c> candidates ordered by similarity to the section around the cursor. No rule-based
/// part, so without the capability the endpoint is unavailable and the client keeps its lexical order.</summary>
public sealed class PageOrderTests
{
    private const string Crypto = "kryptografie certifikat podpis klic verejny soukromy retezec autorita revokace";
    private const string Weather = "pocasi bourka vitr dest mraky slunce teplota vlhkost tlak predpoved";

    [PostgreSqlFact]
    public async Task Context_similarity_orders_only_readable_candidates_and_answers_503_without_the_capability()
    {
        await using var f = await SuggestionFixture.CreateAsync();
        var alfa = await f.PublishAsync("Alfa", Crypto);
        var beta = await f.PublishAsync("Beta", Weather);
        var gama = await f.PublishAsync("Gama", Crypto, f.RestrictedSpaceId, author: f.Admin);
        var delta = await f.DraftAsync("Delta", Crypto);
        var editing = await f.DraftAsync("Rozepsaná stránka");
        await f.ActivateAsync();
        var context = "Postup: kryptografie certifikat podpis :secret{secretId=\"6a1f\" field=\"password\"} klic verejny soukromy";
        Guid[] candidates = [beta.Id, gama.Id, delta.Id, alfa.Id];

        var order = await f.AsAsync(f.Writer, flow => flow.PageOrderAsync(new(editing.Id, context, candidates), default));

        // Readable publications only, most similar first: the hidden page and the draft are absent (they keep their lexical place).
        Assert.Equal([alfa.Id, beta.Id], order.Items.Select(x => x.PageId));
        Assert.True(order.Items[0].Similarity > order.Items[1].Similarity);
        // The section left Atlas once, without the password reference the client might have missed.
        var sent = Assert.Single(f.Sent(AiTaskCatalog.EditorLink));
        Assert.DoesNotContain("secret", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("6a1f", sent, StringComparison.Ordinal);
        Assert.StartsWith("query: Postup: kryptografie certifikat podpis", sent, StringComparison.Ordinal);
        // A WikiAdmin reads the restricted page.
        var admin = await f.AsAsync(f.Admin, flow => flow.PageOrderAsync(new(editing.Id, context, candidates), default));
        Assert.Equal(new[] { alfa.Id, gama.Id }.Order(), admin.Items.Take(2).Select(x => x.PageId).Order());

        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.AsAsync(f.Reader, flow =>
            flow.PageOrderAsync(new(editing.Id, context, candidates), default)))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.AsAsync(f.Writer, flow =>
            flow.PageOrderAsync(new(editing.Id, new string('x', 2001), candidates), default)))).Status);

        // Without the capability: 503 ai_unavailable before anything is read or sent.
        await f.SetTaskAsync(AiTaskCatalog.EditorLink, false);
        var unavailable = await Assert.ThrowsAsync<ApiException>(() => f.AsAsync(f.Writer, flow =>
            flow.PageOrderAsync(new(editing.Id, context + " znovu", candidates), default)));
        Assert.Equal((503, AiGate.UnavailableCode), (unavailable.Status, unavailable.Code));
        Assert.Single(f.Sent(AiTaskCatalog.EditorLink));
    }
}
