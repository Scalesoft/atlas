using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Answering;
using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// The answer sources (AnswerSourceQueries.Sources) through the page adapter (plan phase 3 §6, R3): six chunks, at most two of a page, pages ranked like the
/// hybrid search, the published rule in the same SQL (spec RAG §5), the lexical fallback and the scopes. The fake model gives
/// texts of one concept identical or graded near vectors (<see cref="Graded"/>).
/// </summary>
public sealed class AnswerSourcesTests
{
    private const string Concept = "backup restore database";

    /// <summary>The question and the sections by decreasing similarity: each further word lowers the cosine to the concept
    /// (1.0, 0.90, 0.82, 0.74, 0.70, 0.66 …), all above the 0.45 threshold. Any other text is its own trigram vector.</summary>
    private static readonly (string Marker, string Meaning)[] Graded =
    [
        ("obnovit data", Concept),
        ("Postup obnovy", Concept),
        ("Kontrola kopie", Concept + " alpha"),
        ("Noční kopie", Concept + " alpha bravo"),
        ("Retence kopií", Concept + " alpha bravo charlie"),
        ("Po havárii", Concept + " alpha bravo charlie delta"),
        ("Archivní pásky", Concept + " alpha bravo charlie delta echo"),
        ("Mazání pásek", Concept + " alpha bravo charlie delta echo foxtrot"),
    ];

    private static string GradedMeaning(string text)
        => Graded.FirstOrDefault(x => text.Contains(x.Marker, StringComparison.Ordinal)).Meaning ?? text;

    private const string Question = "Jak obnovit data?";

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Sources_are_six_chunks_at_most_two_per_page_from_published_readable_included_pages()
    {
        await using var f = await AnswerSourcesFixture.CreateAsync(GradedMeaning);
        var a = await f.PublishAsync("Návod pro správce",
            "## Postup obnovy\nSpusťte nástroj a vyberte bod v čase. TAJNE-MISTO\n\n## Kontrola kopie\nPorovnejte kontrolní součty.\n\n## Historie\nDokument vznikl v roce 2020.",
            null, ("Postup obnovy", "postup"), ("Kontrola kopie", "kontrola"), ("Historie", "historie"));
        var b = await f.PublishAsync("Provoz serverů",
            "## Noční kopie\nBěží ve dvě ráno.\n\n## Retence kopií\nUchováváme třicet dní.", null, ("Noční kopie", "nocni"), ("Retence kopií", "retence"));
        var c = await f.PublishAsync("Krizový plán",
            "## Po havárii\nSvolejte tým a informujte vedení.\n\n## Kontakty\nHelpdesk má linku 1234.", null, ("Po havárii", "havarie"), ("Kontakty", "kontakty"));
        var d = await f.PublishAsync("Sklad",
            "## Archivní pásky\nLeží v trezoru.\n\n## Mazání pásek\nJednou ročně.", null, ("Archivní pásky", "pasky"), ("Mazání pásek", "mazani"));
        var hidden = await f.PublishAsync("Cizí návod", "## Postup obnovy\nCIZÍ-TEXT jen pro jiný tým.", f.Index.OtherSpaceId, ("Postup obnovy", "postup"));
        // Password references stay in PageRevision; the worker strips them before chunking (V2).
        await f.Index.Host.ExecuteAsync("""
            UPDATE "PageRevision" SET "Markdown"=replace("Markdown",'TAJNE-MISTO','::secret{vault="v" secret="s"}') WHERE "Id"=:id
            """, await f.Index.PublishedRevisionAsync(a));
        await f.IndexAsync();
        // And should a chunk ever hold one, the source text drops it again.
        await f.ExecuteAsync("""
            UPDATE "KnowledgeChunk" SET "Text"="Text"||' :secret{vault="v" secret="t"}' WHERE "PageId"=@page AND "HeadingId"='nocni'
            """, ("page", b));

        var result = await f.RetrieveAsync(f.Reader, Question, perSource: 2);

        Assert.Equal(AnswerRetrieval.Hybrid, result.Retrieval);
        Assert.Equal([(a, "postup"), (a, "kontrola"), (b, "nocni"), (b, "retence"), (c, "havarie"), (d, "pasky")],
            result.Sources.Select(source => (source.Chunk!.PageId, source.HeadingId)));
        Assert.Equal([1.0, 0.5, 1.0 / 3, 0.25, 0.2, 1.0 / 6], result.Sources.Select(source => source.Rank));
        Assert.DoesNotContain(result.Sources, source => source.Chunk!.PageId == hidden || source.Text.Contains("CIZÍ", StringComparison.Ordinal));
        var first = result.Sources[0];
        Assert.Equal(("Návod pro správce", "Engineering", f.Index.SpaceId, "Postup obnovy"), (first.Title, first.SpaceName, first.SpaceId, first.HeadingTitle));
        Assert.StartsWith("Spusťte nástroj a vyberte bod v čase.", first.Text);
        Assert.Equal((f.Generation.Id, await f.Index.PublishedRevisionAsync(a)), (first.Chunk!.GenerationId, first.Chunk.RevisionId));
        Assert.Equal(new SourceReference("page", 1, a.ToString(), first.Chunk.RevisionId.ToString(), $"{f.Generation.Id:N}:{first.Chunk.Ordinal}"), first.Reference);
        Assert.All(result.Sources, source =>
        {
            Assert.DoesNotContain("secret{", source.Text);
            Assert.DoesNotContain(" › ", source.Text);
        });
        // The typed columns of the dependency (Q1) and back: the real page FK, the chunk and the section anchor.
        using var scope = f.Index.Host.Services.CreateScope();
        var registry = f.Registry(scope);
        var columns = registry.Columns(first);
        Assert.Equal(new SourceColumns("page", 1, a, first.Chunk.RevisionId, null, null, f.Generation.Id, first.Chunk.Ordinal, "postup"), columns);
        Assert.Equal(first.Reference, registry.Reference(columns));
        Assert.Null(registry.Reference(columns with { PageId = null, RevisionId = null }));
        // The limits are the caller's: one per page, three in all.
        Assert.Equal([a, b, c], (await f.RetrieveAsync(f.Reader, Question, take: 3, perSource: 1)).Sources.Select(source => source.Chunk!.PageId));
    }

    [PostgreSqlFact]
    public async Task A_deeper_effort_reads_more_sections_of_one_page()
    {
        await using var f = await AnswerSourcesFixture.CreateAsync(GradedMeaning);
        // The title shares no word with the question: no focused window, only the effort's limit per page applies.
        await f.PublishAsync("Provozní příručka",
            "## Postup obnovy\nSpusťte nástroj.\n\n## Kontrola kopie\nPorovnejte součty.\n\n## Noční kopie\nBěží ve dvě.\n\n## Retence kopií\nTřicet dní.",
            null, ("Postup obnovy", "postup"), ("Kontrola kopie", "kontrola"), ("Noční kopie", "nocni"), ("Retence kopií", "retence"));
        await f.IndexAsync();

        var quick = await f.RetrieveAsync(f.Reader, Question, take: AnswerEffort.Quick.Take, perSource: AnswerEffort.Quick.PerPage);
        var deep = await f.RetrieveAsync(f.Reader, Question, take: AnswerEffort.Max.Take, perSource: AnswerEffort.Max.PerPage);

        Assert.Equal(["postup", "kontrola"], quick.Sources.Select(source => source.HeadingId));
        Assert.Equal(["postup", "kontrola", "nocni", "retence"], deep.Sources.Select(source => source.HeadingId));
        Assert.Same(AnswerEffort.Quick, AnswerEffort.Parse(null));
        Assert.Equal(400, Assert.Throws<ApiException>(() => AnswerEffort.Parse("ultra")).Status);
    }

    [PostgreSqlFact]
    public async Task Short_deployment_match_brings_nearby_server_details_into_wiki_context()
    {
        const string question = "co nám běží v KBN ?";
        const string concept = "deployment environment";
        await using var f = await AnswerSourcesFixture.CreateAsync(text =>
            text.Contains("Prostředí", StringComparison.Ordinal) ? concept
            : text.Contains(question, StringComparison.Ordinal) ? concept
            : text);
        var sections = new (string Heading, string Body)[]
        {
            ("Přehled", "Úvod k instalaci."),
            ("Účel", "Provoz interního portálu."),
            ("Vlastník", "Správa aplikace."),
            ("Přístup", "Přístup přes firemní síť."),
            ("Vydání", "Verze aplikace."),
            ("Záloha", "Zálohování dat."),
            ("Monitoring", "Kontrola dostupnosti."),
            ("Kontakt", "Podpora provozu."),
            ("Prostředí", "Produkční prostředí ProductionKBN a testovací StagingKBN."),
            ("Nasazení", "Nasazení spravuje provozní tým."),
            ("Servery", "Aplikační servery NODE-PROD a NODE-TEST."),
            ("Platforma", "Oba servery používají Windows Server 2022."),
            ("Údržba", "Pravidelná údržba systému."),
        };
        var target = await f.PublishAsync("Popis nasazení v KBN", string.Join("\n\n", sections.Select(x => $"## {x.Heading}\n{x.Body}")));
        await f.PublishAsync("Co běží v laboratoři", "## Přehled\nLaboratorní služba běží samostatně.");
        await f.PublishAsync("Co běží v archivu", "## Přehled\nArchivní úlohy běží v noci.");
        await f.IndexAsync();

        var result = await f.RetrieveAsync(f.Reader, question);

        Assert.True(result.Sources.Count <= 6);
        Assert.Contains(result.Sources, source => source.Chunk!.PageId == target && source.Text.Contains("ProductionKBN", StringComparison.Ordinal));
        Assert.Contains(result.Sources, source => source.Chunk!.PageId == target && source.Text.Contains("NODE-PROD", StringComparison.Ordinal));
        Assert.Contains(result.Sources, source => source.Chunk!.PageId == target && source.Text.Contains("Windows Server 2022", StringComparison.Ordinal));
        Assert.Empty((await f.RetrieveAsync(f.Reader, "nezávislé účetní pravidlo", semantic: false)).Sources);
    }

    [PostgreSqlFact]
    public async Task Short_match_at_end_of_page_keeps_relevant_preceding_section()
    {
        const string question = "co běží v NORA?";
        const string concept = "deployment runtime";
        await using var f = await AnswerSourcesFixture.CreateAsync(text =>
            text.Contains(question, StringComparison.Ordinal) || text.Contains("Produkční prostředí", StringComparison.Ordinal) ? concept
            : text.Contains("Serverová topologie", StringComparison.Ordinal) ? concept + " alpha"
            : text);
        var target = await f.PublishAsync("Nasazení v NORA",
            "## Úvod\nPopis nasazení.\n\n## Konfigurace\nKonfigurační soubor.\n\n## Servery\nServerová topologie: NODE-A a NODE-B.\n\n## Prostředí\nProdukční prostředí používá oba uzly.");
        await f.IndexAsync();

        var sources = (await f.RetrieveAsync(f.Reader, question)).Sources;

        Assert.Contains(sources, source => source.Chunk!.PageId == target && source.Text.Contains("Produkční prostředí", StringComparison.Ordinal));
        Assert.Contains(sources, source => source.Chunk!.PageId == target && source.Text.Contains("Serverová topologie", StringComparison.Ordinal));
        Assert.True(sources.Count <= 4);
        Assert.Equal(3, (await f.RetrieveAsync(f.Reader, question, take: 1)).Sources[0].Chunk!.Ordinal);
    }

    /// <summary>The owner's case (5.2): „co nám běží v …ústavu?“ cited English naming pages first, because „nám“ was searched as the
    /// prefix nam:* (naming) and a title match ranks above meaning, while „ústavu“ never matched the institute's own page „… ústav“.</summary>
    [PostgreSqlFact]
    public async Task Entity_question_takes_the_entity_pages_and_no_page_matching_a_pronoun_prefix()
    {
        const string question = "co nám běží v testovacím ústavu?";
        const string concept = "institute deployment portal";
        await using var f = await AnswerSourcesFixture.CreateAsync(text =>
            text.Contains(question, StringComparison.Ordinal) || text.Contains("Testovací ústav", StringComparison.Ordinal) ? concept
            : text.Contains("Nemocnice Jinde", StringComparison.Ordinal) ? FakeAi.Near(concept)
            : text);
        var space = await f.PublishAsync("Testovací ústav",
            "## Popis prostředí\nPortál pro pacienty a MPI.\n\n## Požadavky\nSíťové prostupy a přístup k API.", null,
            ("Popis prostředí", "popis"), ("Požadavky", "pozadavky"));
        var order = await f.PublishAsync("Testovací ústav — zakázka 2026 (D99)",
            "## Stav\nHotovo 34 %, čeká se na termín.\n\n## Předmět dodávky\nOnline služby pro pacienty.", null,
            ("Stav", "stav"), ("Předmět dodávky", "predmet"));
        var naming = await f.PublishAsync("C# naming convention", "## Repository methods naming\nMethods start with a verb.", null,
            ("Repository methods naming", "repository"));
        var terms = await f.PublishAsync("Terms and naming standards", "## Glossary\nUse one term per concept.", null, ("Glossary", "glossary"));
        await f.PublishAsync("Nemocnice Jinde — zakázka 2026 (D98)", "## Stav\nHotovo 80 %.", null, ("Stav", "stav"));
        await f.IndexAsync();

        foreach (var semantic in new[] { true, false })
        {
            var pages = (await f.RetrieveAsync(f.Reader, question, semantic: semantic)).Sources.Select(source => source.Chunk!.PageId).ToArray();

            Assert.DoesNotContain(naming, pages);
            Assert.DoesNotContain(terms, pages);
            Assert.Contains(space, pages);
            Assert.Contains(order, pages);
            Assert.True(pages.Take(2).All(page => page == space || page == order), "The institute's own pages lead.");
        }
    }

    [PostgreSqlFact]
    public async Task Exact_title_match_precedes_focused_short_sections_of_other_pages()
    {
        const string question = "KBN servery";
        const string concept = "server deployment";
        await using var f = await AnswerSourcesFixture.CreateAsync(text =>
            text.Contains(question, StringComparison.Ordinal) || text.Contains("Distractor seed", StringComparison.Ordinal) ? concept : text);
        var exact = await f.PublishAsync(question, "## Přehled\n" + new string('X', 400) + " Přesná stránka popisuje servery.");
        foreach (var suffix in new[] { "A", "B" })
        {
            await f.PublishAsync("KBN provoz " + suffix,
                "## Úvod\nDistractor seed.\n\n## První\nKrátká poznámka.\n\n## Druhá\nJiná poznámka.\n\n## Třetí\nDalší poznámka.");
        }
        await f.IndexAsync();

        var sources = (await f.RetrieveAsync(f.Reader, question)).Sources;

        Assert.Equal(exact, sources[0].Chunk!.PageId);
        Assert.True(sources.Count <= 6);
    }

    [PostgreSqlFact]
    public async Task Hidden_pages_never_take_a_source_slot_even_when_nearest()
    {
        await using var f = await AnswerSourcesFixture.CreateAsync(text =>
            text.Contains("obnova dat", StringComparison.OrdinalIgnoreCase) ? Concept
            : text.Contains("návrat systému", StringComparison.Ordinal) ? FakeAi.Near(Concept)
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
            AiIndex.IndexFixture.Rich("Text G."), "Text G."), f.Writer, default)).Id;
        await f.IndexAsync();
        // Hidden after indexing, before the worker could clean up: the SQL must refuse them on its own.
        await f.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=@id""", ("id", ownAccess));
        await f.ExecuteAsync("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"=@admin,"TrashBatchId"=gen_random_uuid() WHERE "Id"=@id
            """, ("id", trashed), ("admin", f.Admin.Id));
        await f.ExecuteAsync("""UPDATE "Page" SET "IsTemplate"=true WHERE "Id"=@id""", ("id", template));
        await f.ExecuteAsync("""
            INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom") VALUES (gen_random_uuid(),@space,false,@admin,now())
            """, ("space", excludedSpace), ("admin", f.Admin.Id));
        await f.Index.EditAndPublishAsync(stale, "Skrytá stránka F", "Nový text F.");
        const string question = "obnova dat";
        var vector = f.Search.QueryVector(question);
        await f.Search.AddKnowledgePageAsync(unpublished, vector);
        // 300 hidden citable chunks identical to the question, the visible one only near it.
        var hidden = new[] { foreign, ownAccess, trashed, template, excluded, stale, unpublished };
        foreach (var page in hidden)
        {
            await f.AddChunksAsync(page, page == hidden[0] ? 48 : 42, vector);
        }

        foreach (var take in new[] { 6, 1 })
        {
            var result = await f.RetrieveAsync(f.Reader, question, take: take);
            Assert.Equal(AnswerRetrieval.Hybrid, result.Retrieval);
            var source = Assert.Single(result.Sources);
            Assert.Equal(visible, source.Chunk!.PageId);
            Assert.Equal("Kroky pro návrat systému ze zálohy.", source.Text);
        }
        // A WikiAdmin reads every space, but never the trash, a template, an excluded space, a stale or unpublished revision.
        var admin = await f.RetrieveAsync(f.Admin, question);
        Assert.Equal(new HashSet<Guid> { foreign, ownAccess, visible }, admin.Sources.Select(source => source.Chunk!.PageId).ToHashSet());

        // The widened index scan itself: forced onto HNSW, the iterative scan walks past the 300 hidden chunks to the visible one.
        var (pages, plan) = await f.AnswerSqlAsync(f.Reader, question, vector, settings: "SET LOCAL enable_seqscan=off; SET LOCAL enable_sort=off;");
        Assert.True(plan.Contains("IX_KnowledgeChunk_Embedding", StringComparison.Ordinal), plan);
        Assert.Equal([visible], pages);
        // HNSW is approximate: disabling iterative scans does not guarantee empty results. A graph can still visit
        // the visible node. Verify the production settings directly and keep ACL assertions for both scan modes.
        Assert.Equal(["relaxed_order", "100", "20000"], await f.Index.As<IReadOnlyList<string>>(f.Reader,
            new ReadAnswerRetrievalSettings(question, (await f.EmbedAsync(question))!)));
        var withoutWidening = await f.AnswerSqlAsync(f.Reader, question, vector,
            settings: "SET LOCAL enable_seqscan=off; SET LOCAL enable_sort=off; SET LOCAL hnsw.iterative_scan=off;");
        Assert.All(withoutWidening.Pages, page => Assert.Equal(visible, page));
        // A revoked stamp sees nothing at all, with or without a vector.
        Assert.Empty((await f.AnswerSqlAsync(f.Reader, question, vector, stamp: "revoked")).Pages);
        Assert.Empty((await f.AnswerSqlAsync(f.Reader, "Kroky po havárii", null, stamp: "revoked")).Pages);
    }

    [PostgreSqlFact]
    public async Task Without_a_vector_retrieval_is_lexical_over_the_active_generation()
    {
        await using var f = await AnswerSourcesFixture.CreateAsync();
        var vpn = await f.PublishAsync("VPN klient",
            "## Instalace\nStáhněte instalátor klienta.\n\n## Nastavení\nNastavit profil VPN a ověření zvládnete sami.\n\n## Licence\nPlatí rok.",
            null, ("Instalace", "instalace"), ("Nastavení", "nastaveni"), ("Licence", "licence"));
        var home = await f.PublishAsync("Práce z domova", "## Pravidla\nZ domova pracujte dva dny v týdnu.", null, ("Pravidla", "pravidla"));
        await f.PublishAsync("Kantýna", "## Menu\nPolévka a hlavní jídlo.", null, ("Menu", "menu"));
        await f.PublishAsync("VPN tajné", "## Nastavit\nNastavit VPN klienta z domova přes bránu B.", f.Index.OtherSpaceId, ("Nastavit", "nastavit"));
        await f.IndexAsync();
        const string question = "Jak nastavit VPN klienta z domova?";

        // The embeddings time out: the answer waits its budget of one second (fake clock), then retrieves lexically.
        f.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Timeout);
        var embedding = f.EmbedAsync(question);
        // The budget starts with the call: advance the clock once the call is there. Under load the way to it (configuration, access)
        // took longer than the loop below, whose advances then went by before the budget's timer existed.
        for (var watch = System.Diagnostics.Stopwatch.StartNew(); !embedding.IsCompleted && !f.Fake.Calls.Any(call => call.Task == AiTaskCatalog.SearchEmbedQuery);)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), "The question's embedding call never came.");
            await Task.Delay(10);
        }
        for (var i = 0; i < 40 && !embedding.IsCompleted; i++)
        {
            // Well below the fake's 30 s timeout: only the budget can end the wait.
            f.Index.Clock.Advance(SemanticQueryService.AnswerBudget / 4);
            await Task.Delay(25);
        }
        var vector = await embedding.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, f.Fake.Calls.Count(call => call.Task == AiTaskCatalog.SearchEmbedQuery));
        Assert.Null(vector);
        var calls = f.Fake.Calls.Count;

        var result = await f.RetrieveWithAsync(f.Reader, question, vector);

        Assert.Equal(AnswerRetrieval.Lexical, result.Retrieval);
        Assert.Equal([(vpn, "nastaveni"), (vpn, "instalace"), (home, "pravidla")], result.Sources.Select(source => (source.Chunk!.PageId, source.HeadingId)));
        Assert.Equal(calls, f.Fake.Calls.Count);
        // Without an active generation there is no chunk text to answer from.
        await f.ExecuteAsync("""UPDATE "IndexGeneration" SET "State"='Retired',"EndedAt"=now() WHERE "Id"=@id""", ("id", f.Generation.Id));
        Assert.Empty((await f.RetrieveWithAsync(f.Reader, question, null)).Sources);
    }

    [PostgreSqlFact]
    public async Task Page_scope_reads_only_that_page_and_space_scope_only_that_space()
    {
        await using var f = await AnswerSourcesFixture.CreateAsync(GradedMeaning);
        await f.Index.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant
        {
            Id = Guid.NewGuid(), SpaceId = f.Index.OtherSpaceId, UserId = f.Reader.Id, Role = (int)SpaceRole.Reader,
        }));
        var closed = await f.Index.Host.SeedSpaceAsync("Uzavřený", (f.Writer, SpaceRole.Writer));
        var here = await f.PublishAsync("Návod pro správce", "## Postup obnovy\nTady.", null, ("Postup obnovy", "postup"));
        var there = await f.PublishAsync("Archivní návod", "## Postup obnovy\nTam.", f.Index.OtherSpaceId, ("Postup obnovy", "postup"));
        var unrelated = await f.PublishAsync("Kantýna",
            "## Menu\nPolévka.\n\n## Otevírací doba\nOd jedenácti.\n\n## Placení\nKartou.", null,
            ("Menu", "menu"), ("Otevírací doba", "doba"), ("Placení", "placeni"));
        var unreadable = await f.PublishAsync("Uzavřený návod", "## Postup obnovy\nNikdo.", closed, ("Postup obnovy", "postup"));
        await f.IndexAsync();

        // Both sections are the same concept (identical vectors, no lexical match): an exact tie that the vector rank of
        // atlas_search_hybrid_candidates breaks by page id, a random Guid. The wiki scope reads both, in either order.
        Assert.Equal(new[] { here, there }.Order(), (await f.RetrieveAsync(f.Reader, Question)).Sources.Select(source => source.Chunk!.PageId).Order());
        Assert.Equal([here], (await f.RetrieveAsync(f.Reader, Question, AnswerScope.ForSpace(f.Index.SpaceId))).Sources.Select(source => source.Chunk!.PageId));
        Assert.Equal([there], (await f.RetrieveAsync(f.Reader, Question, AnswerScope.ForSpace(f.Index.OtherSpaceId))).Sources.Select(source => source.Chunk!.PageId));
        // A page scope reads that page's published revision with or without a match, beyond the per-page limit of the wiki scope.
        foreach (var semantic in new[] { true, false })
        {
            var page = await f.RetrieveAsync(f.Reader, Question, AnswerScope.ForPage(unrelated), semantic);
            Assert.Equal(["doba", "menu", "placeni"], page.Sources.Select(source => source.HeadingId).Order());
            Assert.All(page.Sources, source => Assert.Equal(unrelated, source.Chunk!.PageId));
        }
        // An unreadable page or space in the scope: nothing, and no error that would name it.
        Assert.Empty((await f.RetrieveAsync(f.Reader, Question, AnswerScope.ForPage(unreadable))).Sources);
        Assert.Empty((await f.RetrieveAsync(f.Reader, Question, AnswerScope.ForPage(Guid.NewGuid()))).Sources);
        // A space switched out of AI: its scope is empty and the wiki scope leaves it out.
        await f.ExecuteAsync("""
            INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom") VALUES (gen_random_uuid(),@space,false,@admin,now())
            """, ("space", f.Index.OtherSpaceId), ("admin", f.Admin.Id));
        Assert.Empty((await f.RetrieveAsync(f.Reader, Question, AnswerScope.ForSpace(f.Index.OtherSpaceId))).Sources);
        Assert.Empty((await f.RetrieveAsync(f.Reader, Question, AnswerScope.ForPage(there))).Sources);
        Assert.Equal([here], (await f.RetrieveAsync(f.Reader, Question)).Sources.Select(source => source.Chunk!.PageId));
    }

    [PostgreSqlFact]
    public async Task Drafts_are_never_sources_for_writers_or_wiki_admins()
    {
        await using var f = await AnswerSourcesFixture.CreateAsync(GradedMeaning);
        var published = await f.PublishAsync("Návod pro správce", "## Postup obnovy\nPublikovaný postup, jak obnovit data.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        // A never published page titled exactly like the question, and a newer draft of the published page.
        await f.Index.Pages.CreateAsync(new CreatePageRequest(f.Index.SpaceId, null, "Jak obnovit data",
            AiIndex.IndexFixture.Rich("KONCEPT: obnovit data takhle."), "KONCEPT: obnovit data takhle."), f.Writer, default);
        await f.Index.SaveDraftAsync(published, "Návod pro správce", "## Postup obnovy\nKONCEPT-TEXT obnovit data jinak.", ("Postup obnovy", "postup"));
        await f.IndexAsync();

        foreach (var actor in new[] { f.Writer, f.Admin })
        {
            foreach (var semantic in new[] { true, false })
            {
                var result = await f.RetrieveAsync(actor, Question, semantic: semantic);
                Assert.All(result.Sources, source =>
                {
                    Assert.Equal(published, source.Chunk!.PageId);
                    Assert.DoesNotContain("KONCEPT", source.Text);
                });
                Assert.Equal(semantic ? AnswerRetrieval.Hybrid : AnswerRetrieval.Lexical, result.Retrieval);
                Assert.Single(result.Sources);
            }
        }
    }
}
