using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>S1/S2 (plan §11, R4): tiers above vectors, meaning-only hits with their passage, the access rule of every vector
/// candidate, drafts and the generation snapshot. The fake model gives texts of one concept identical or near vectors.</summary>
public sealed class HybridSearchTests
{
    private const string Remote = "vzdálený přístup";

    /// <summary>The query and the "Připojení zvenku" section mean the same; every other text is its own trigram vector.</summary>
    private static string RemoteMeaning(string text)
        => text.Contains(Remote, StringComparison.OrdinalIgnoreCase) || text.Contains("Připojení zvenku", StringComparison.Ordinal)
            ? "remote access from home"
            : text;

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Exact_title_prefix_alias_and_heading_tiers_stay_above_vector_matches()
    {
        await using var f = await HybridSearchFixture.CreateAsync(text =>
            text.StartsWith("query: VPN", StringComparison.Ordinal) || text.Contains("Vzdálený přístup do sítě", StringComparison.Ordinal)
                ? "tunnel home connection"
                : text);
        var nearest = await f.PublishAsync("Vzdálený přístup do sítě", "Jak se připojit z domova k firemním systémům.");
        var exact = await f.PublishAsync("VPN", "Firemní síť pro zaměstnance.");
        var prefix = await f.PublishAsync("VPN klient", "Instalace klienta na notebook.");
        var heading = await f.PublishAsync("Síťová infrastruktura", "## VPN\nBrána a tunely.", null, ("VPN", "vpn"));
        var alias = await f.PublishAsync("VPN brána", "Zařízení na okraji sítě.");
        await f.Index.EditAndPublishAsync(alias, "Hraniční brána", "Zařízení na okraji sítě.");
        await f.IndexAsync();

        var response = await f.FindAsync(f.Reader, "VPN");

        Assert.True(response.Semantic);
        // An earlier title is derived from the title: it ranks above a heading (migration 2026102720).
        Assert.Equal([exact, prefix, alias, heading, nearest], response.Items.Select(x => x.Id));
        Assert.Equal("VPN brána", response.Items[2].MatchedAlias);
        Assert.Equal("vpn", response.Items[3].HeadingId);
        Assert.All(response.Items.Take(4), hit => Assert.False(hit.Semantic));
        Assert.True(response.Items[4].Semantic);
        // Hit@1 of an exact title never regresses: the same first hit without AI.
        var lexical = await f.InvokeAsync(f.Reader, "VPN", null);
        Assert.False(lexical.Semantic);
        Assert.Equal(exact, lexical.Items[0].Id);
        Assert.DoesNotContain(lexical.Items, hit => hit.Id == nearest);
    }

    [PostgreSqlFact]
    public async Task A_name_found_as_one_phrase_in_a_body_stays_above_pages_found_only_by_meaning()
    {
        // „luboš krbec“: the page of his contact card led the lexical preview, then pages near only in meaning (fused above a
        // body match) pushed it down, off the first page, and his contact with it.
        await using var f = await HybridSearchFixture.CreateAsync(text =>
            text.Contains("Vedení", StringComparison.Ordinal) || text.Contains("query: luboš krbec", StringComparison.Ordinal) ? "company leadership and management" : text);
        for (var i = 0; i < 3; i++)
        {
            await f.PublishAsync($"Vedení {i}", $"Vedení společnosti, oddělení {i}.");
        }
        var card = await f.PublishAsync("Zaměstnanci", "Kontakty na lidi.\n\nLuboš Krbec, jednatel, ORION s.r.o.");
        var scattered = await f.PublishAsync("Porada", "Krbec přišel pozdě, Luboš odešel dřív.");
        await f.IndexAsync();

        var response = await f.FindAsync(f.Reader, "luboš krbec", limit: 3);

        Assert.True(response.Semantic);
        Assert.Equal(card, response.Items[0].Id);
        Assert.False(response.Items[0].Semantic);
        // Only the phrase: the words apart still blend with meaning as before.
        Assert.DoesNotContain(response.Items, hit => hit.Id == scattered);
        var lexical = await f.InvokeAsync(f.Reader, "Lubos KRBEC", null);
        Assert.Equal([card, scattered], lexical.Items.Select(x => x.Id));
    }

    [PostgreSqlFact]
    public async Task A_paraphrase_found_only_by_meaning_is_semantic_and_carries_its_passage_and_anchor()
    {
        await using var f = await HybridSearchFixture.CreateAsync(RemoteMeaning);
        var page = await f.PublishAsync("Práce z domova",
            "## Vybavení\nNotebook a monitor dostanete od IT.\n\n## Připojení zvenku\nZ domova se do firemní sítě dostanete přes klienta a **ověření** telefonem.",
            null, ("Vybavení", "vybaveni"), ("Připojení zvenku", "zvenku"));
        await f.IndexAsync();

        var response = await f.FindAsync(f.Reader, Remote);

        Assert.True(response.Semantic);
        var hit = Assert.Single(response.Items);
        Assert.Equal((page, true, false), (hit.Id, hit.Semantic, hit.IsDraft));
        Assert.Equal(("Připojení zvenku", "zvenku"), (hit.HeadingTitle, hit.HeadingId));
        Assert.NotNull(hit.Passage);
        Assert.Equal(new SearchPassage("Z domova se do firemní sítě dostanete přes klienta a ověření telefonem.", "Připojení zvenku", "zvenku"), hit.Passage);
        Assert.Equal(hit.Passage.Text, hit.Snippet);
        Assert.Null(hit.MatchedAlias);
    }

    [PostgreSqlFact]
    public async Task Vector_candidates_never_leak_unreadable_pages_even_when_the_index_is_widened()
    {
        await using var f = await HybridSearchFixture.CreateAsync(text =>
            text.Contains("obnova dat", StringComparison.OrdinalIgnoreCase) ? "backup restore database"
            : text.Contains("návrat systému", StringComparison.Ordinal) ? FakeAi.Near("backup restore database")
            : text);
        var excludedSpace = await f.Index.Host.SeedSpaceAsync("Vyloučený", (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
        var visible = await f.PublishAsync("Kroky po havárii", "Kroky pro návrat systému ze zálohy.");
        var foreign = await f.PublishAsync("Skrytá stránka A", "Text A.", f.Index.OtherSpaceId);
        var ownAccess = await f.PublishAsync("Skrytá stránka B", "Text B.");
        var trashed = await f.PublishAsync("Skrytá stránka C", "Text C.");
        var template = await f.PublishAsync("Skrytá stránka D", "Text D.");
        var excluded = await f.PublishAsync("Skrytá stránka E", "Text E.", excludedSpace);
        var stale = await f.PublishAsync("Skrytá stránka F", "Text F.");
        var unpublished = (await f.Index.Pages.CreateAsync(new CreatePageRequest(f.Index.SpaceId, null, "Skrytá stránka G",
            Atlas.Server.Tests.AiIndex.IndexFixture.Rich("Text G."), "Text G."), f.Writer, default)).Id;
        await f.IndexAsync();
        // Hidden after indexing, before the worker could clean up: every read must refuse them on its own.
        await f.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=@id""", ("id", ownAccess));
        await f.ExecuteAsync("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"=@admin,"TrashBatchId"=gen_random_uuid() WHERE "Id"=@id
            """, ("id", trashed), ("admin", f.Admin.Id));
        await f.ExecuteAsync("""UPDATE "Page" SET "IsTemplate"=true WHERE "Id"=@id""", ("id", template));
        await f.ExecuteAsync("""
            INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom") VALUES (gen_random_uuid(),@space,false,@admin,now())
            """, ("space", excludedSpace), ("admin", f.Admin.Id));
        await f.Index.EditAndPublishAsync(stale, "Skrytá stránka F", "Nový text F.");
        var query = "obnova dat";
        var vector = f.QueryVector(query);
        await f.AddKnowledgePageAsync(unpublished, vector);
        // 300 hidden chunks identical to the query, the visible one only near it.
        var hidden = new[] { foreign, ownAccess, trashed, template, excluded, stale, unpublished };
        foreach (var page in hidden)
        {
            await f.AddChunksAsync(page, page == hidden[0] ? 48 : 42, vector);
        }

        foreach (var limit in new[] { 20, 1 })
        {
            var response = await f.FindAsync(f.Reader, query, limit: limit);
            Assert.True(response.Semantic);
            var hit = Assert.Single(response.Items);
            Assert.Equal((visible, true), (hit.Id, hit.Semantic));
            Assert.False(response.HasMore);
            Assert.Null(response.NextOffset);
        }
        var published = await f.FindAsync(f.Reader, query, new SearchOptions(Content: "published"));
        Assert.Equal([visible], published.Items.Select(x => x.Id));

        // The widened index scan itself: forced onto HNSW, the iterative scan walks past the 300 hidden chunks to the visible one.
        var (pages, plan) = await f.HybridSqlAsync(f.Reader, query, vector, settings: "SET LOCAL enable_seqscan=off; SET LOCAL enable_sort=off;");
        Assert.True(plan.Contains("IX_KnowledgeChunk_Embedding", StringComparison.Ordinal), plan);
        Assert.Equal([visible], pages);
        // Approximate HNSW may find the visible page without widening; every result must still be readable.
        var (nonIterativePages, _) = await f.HybridSqlAsync(f.Reader, query, vector,
            settings: "SET LOCAL enable_seqscan=off; SET LOCAL enable_sort=off; SET LOCAL hnsw.iterative_scan=off;");
        Assert.All(nonIterativePages, page => Assert.Equal(visible, page));
        // A revoked stamp sees nothing at all.
        Assert.Empty((await f.HybridSqlAsync(f.Reader, query, vector, stamp: "revoked")).Pages);
    }

    [PostgreSqlFact]
    public async Task Drafts_stay_lexical_and_never_get_the_semantic_label()
    {
        await using var f = await HybridSearchFixture.CreateAsync(text =>
            text.Contains("obnova dat", StringComparison.OrdinalIgnoreCase) || text.Contains("návrat systému", StringComparison.Ordinal)
                ? "backup restore database"
                : text);
        var meaning = await f.PublishAsync("Kroky po havárii", "Kroky pro návrat systému ze zálohy.");
        var draft = (await f.Index.Pages.CreateAsync(new CreatePageRequest(f.Index.SpaceId, null, "Obnova dat ze zálohy",
            Atlas.Server.Tests.AiIndex.IndexFixture.Rich("Koncept: obnova dat."), "Koncept: obnova dat."), f.Writer, default)).Id;
        await f.IndexAsync();

        var response = await f.FindAsync(f.Writer, "obnova dat");

        Assert.True(response.Semantic);
        Assert.Equal([draft, meaning], response.Items.Select(x => x.Id));
        var concept = response.Items[0];
        Assert.Equal((true, false), (concept.IsDraft, concept.Semantic));
        Assert.Null(concept.Passage);
        Assert.True(response.Items[1].Semantic);
        // The drafts scope never reaches the vector part.
        var drafts = await f.FindAsync(f.Writer, "obnova dat", new SearchOptions(Content: "drafts"));
        Assert.False(drafts.Semantic);
        Assert.Equal([draft], drafts.Items.Select(x => x.Id));
    }

    [PostgreSqlFact]
    public async Task A_retired_generation_or_a_republished_revision_falls_back_to_lexical()
    {
        await using var f = await HybridSearchFixture.CreateAsync(RemoteMeaning);
        var page = await f.PublishAsync("Práce z domova", "## Připojení zvenku\nPřes klienta a telefon.", null, ("Připojení zvenku", "zvenku"));
        await f.IndexAsync();
        Assert.Equal([page], (await f.FindAsync(f.Reader, Remote)).Items.Select(x => x.Id));

        // A new publication not indexed yet: the chunk of the old revision is never used.
        await f.Index.EditAndPublishAsync(page, "Práce z domova", "## Vybavení\nNotebook od IT.", ("Vybavení", "vybaveni"));
        var republished = await f.FindAsync(f.Reader, Remote);
        Assert.True(republished.Semantic);
        Assert.Empty(republished.Items);

        // The generation is retired between the embedding and the SQL: lexical, no error, no semantic label.
        var semantic = await f.Semantic.TryEmbedAsync(Remote, new SearchOptions(), true, 0, default);
        Assert.NotNull(semantic);
        await f.IndexAsync();
        await f.ExecuteAsync("""UPDATE "IndexGeneration" SET "State"='Retired',"EndedAt"=now() WHERE "Id"=@id""", ("id", f.Generation.Id));
        var lexical = await f.InvokeAsync(f.Reader, "domova", semantic);
        Assert.False(lexical.Semantic);
        Assert.Equal([page], lexical.Items.Select(x => x.Id));
        Assert.Null(lexical.Items[0].Passage);
        // Even called directly, the SQL ignores a generation that is no longer active.
        Assert.Empty((await f.HybridSqlAsync(f.Reader, Remote, semantic.Vector)).Pages);
    }
}
