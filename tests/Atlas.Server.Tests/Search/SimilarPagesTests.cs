using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.AiSuggestions;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Queries;
using Atlas.Server.Tests.AiSuggestions;
using Atlas.Server.Tests.Platform;
using Wolverine;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>P1: while a page is being created, existing pages with a similar title, previous title, import path or
/// section are suggested. Word-AND search misses a longer new title, so similarity has its own function; access is
/// the search ACL, and a lookup that runs too long answers nothing instead of an error.
/// The lookup's own 1 s statement timeout (PlannerSettings.TypingTimeout) is a wall-clock bound, so the class runs alone after the
/// parallel tests, as the timed tests do: under the parallel suite the lookup ran past it and answered nothing.</summary>
[Collection(SimilarPagesCollection.Name)]
public sealed class SimilarPagesTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Similar_titles_aliases_and_headings_are_suggested_with_the_matching_section()
    {
        await using var f = await SearchFixture.CreateAsync();
        var vpn = await Published(f, "Připojení k VPN", ("z-mobilu", "Z mobilu"));
        var handbook = await Published(f, "Síťová příručka", ("pripojeni-na-vpn", "Připojení na VPN"));
        var imported = await Published(f, "VPN");
        var source = $"similar-pages-{Guid.NewGuid():N}";
        await f.Execute($"""
            INSERT INTO "WikiImportSource" ("Id","SourceKey") VALUES(gen_random_uuid(),'{source}');
            INSERT INTO "WikiImportPage" ("Id","SourceId","SourcePath","PageId")
              SELECT gen_random_uuid(),"Id",'navody/vzdaleny-pristup',@id FROM "WikiImportSource" WHERE "SourceKey"='{source}';
            INSERT INTO "WikiImportCheckpoint" ("Id","ImportPageId","RevisionId","SourceSha256","ActiveFrom")
              SELECT gen_random_uuid(),m."Id",p."PublishedRevisionId",repeat('a',64),clock_timestamp()
              FROM "WikiImportPage" m JOIN "Page" p ON p."Id"=m."PageId" WHERE m."PageId"=@id;
            UPDATE "SearchDocument" SET "Aliases"="Aliases" WHERE "PageId"=@id;
            """, imported.Id);

        var result = await Similar(f, f.Reader, "Připojení na VPN z mobilu");

        Assert.Equal(new[] { handbook.Id, vpn.Id }, result.Items.Select(item => item.Id));
        // S7d: the 0-1 similarity the SQL already computes; the list is ordered by it and the result threshold is 0.35.
        Assert.All(result.Items, item => Assert.InRange(item.Score, 0.35f, 1f));
        Assert.True(result.Items[0].Score >= result.Items[1].Score);
        Assert.Equal(1f, (await Similar(f, f.Reader, "Připojení k VPN")).Items[0].Score);
        // Folded: a title typed without diacritics or in other case finds the same page.
        Assert.Equal(vpn.Id, (await Similar(f, f.Reader, "PRIPOJENI K VPN")).Items[0].Id);
        var title = result.Items[1];
        Assert.Equal(("title", "Připojení k VPN", false, f.SpaceId, "History"), (title.MatchedBy, title.Title, title.IsDraft, title.SpaceId, title.SpaceName));
        Assert.Equal(("Z mobilu", "z-mobilu"), (title.HeadingTitle, title.HeadingId));
        var section = result.Items[0];
        Assert.Equal(("heading", "Připojení na VPN", "pripojeni-na-vpn"), (section.MatchedBy, section.HeadingTitle, section.HeadingId));
        var alias = Assert.Single((await Similar(f, f.Reader, "Vzdálený přístup do sítě")).Items);
        Assert.Equal((imported.Id, "alias", "VPN"), (alias.Id, alias.MatchedBy, alias.Title));
        Assert.Null(alias.HeadingId);
    }

    [PostgreSqlFact]
    public async Task Similar_pages_never_include_unreadable_pages_or_other_peoples_drafts()
    {
        await using var f = await SearchFixture.CreateAsync();
        // Titles of this test's own word only: a wiki administrator reads every space of the shared database, where other tests
        // have pages about connecting too, and the answer holds five pages.
        var vpn = "VPN" + Guid.NewGuid().ToString("N")[..6];
        var visible = await Published(f, $"{vpn}");
        var restricted = Guid.NewGuid();
        await f.Host.SeedAsync(session => session.SaveAsync(new Space { Id = restricted, Name = "Restricted" }));
        var hidden = await f.Pages.CreateAsync(new(restricted, null, $"{vpn} v kanceláři", SearchFixture.Rich("x"), "x"), f.Admin, default);
        await f.Pages.PublishAsync(hidden.Id, new(1), f.Admin, default);
        var foreignDraft = await f.Pages.CreateAsync(new(f.SpaceId, null, $"{vpn} doma", SearchFixture.Rich("x"), "x"), f.OtherWriter, default);
        var template = await Published(f, $"Šablona {vpn}");
        await f.Execute("UPDATE \"Page\" SET \"IsTemplate\"=true WHERE \"Id\"=@id", template.Id);
        var trashed = await Published(f, $"{vpn} pro hosty");
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        await f.As<OrganizationChange>(f.Writer, new TrashPage(trashed.Id, new TrashPageRequest(false, tree.Version, 1)));
        var ownDraft = await f.Create($"{vpn} z domu");

        var writer = await Similar(f, f.Writer, $"{vpn}", limit: 5);

        Assert.Equal(new[] { visible.Id, ownDraft.Id }.Order(), writer.Items.Select(item => item.Id).Order());
        Assert.True(writer.Items.Single(item => item.Id == ownDraft.Id).IsDraft);
        Assert.False(writer.Items.Single(item => item.Id == visible.Id).IsDraft);
        Assert.Equal(new[] { visible.Id }, (await Similar(f, f.Reader, $"{vpn}", limit: 5)).Items.Select(item => item.Id));
        // A WikiAdmin reads the restricted space, but never-published drafts stay their owners' own.
        Assert.Equal(new[] { visible.Id, hidden.Id }.Order(),
            (await Similar(f, f.Admin, $"{vpn}", limit: 5)).Items.Select(item => item.Id).Order());
        Assert.Equal(new[] { ownDraft.Id }, (await Similar(f, f.Writer, $"{vpn}", exclude: visible.Id, limit: 5)).Items.Select(item => item.Id));
        Assert.DoesNotContain(ownDraft.Id, (await Similar(f, f.OtherWriter, $"{vpn} z domu", limit: 5)).Items.Select(item => item.Id));
        Assert.Contains(foreignDraft.Id, (await Similar(f, f.OtherWriter, $"{vpn}", limit: 5)).Items.Select(item => item.Id));
    }

    [PostgreSqlFact]
    public async Task Short_or_unrelated_titles_return_nothing_and_at_most_three_by_default()
    {
        await using var f = await SearchFixture.CreateAsync();
        foreach (var number in Enumerable.Range(1, 5)) await Published(f, $"Zálohování serveru {number}");

        Assert.Empty((await Similar(f, f.Reader, "Dovolená a nemoc")).Items);
        Assert.Empty((await Similar(f, f.Reader, "ab")).Items);
        Assert.Equal(3, (await Similar(f, f.Reader, "Zálohování serveru")).Items.Count);
        Assert.Equal(5, (await Similar(f, f.Reader, "Zálohování serveru", limit: 5)).Items.Count);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => Similar(f, f.Reader, "Zálohování serveru", limit: 6))).Status);
    }

    [PostgreSqlFact]
    public async Task Similar_pages_answer_nothing_instead_of_waiting_past_the_time_limit()
    {
        await using var f = await SearchFixture.CreateAsync();
        await Published(f, "Zálohování serveru");
        await using var connection = new NpgsqlConnection(f.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            // A revocation holding the account row: the lookup's FOR SHARE read waits until statement_timeout.
            await using var hold = new NpgsqlCommand("SELECT 1 FROM \"UserAccount\" WHERE \"Id\"=@id FOR UPDATE", connection, transaction);
            hold.Parameters.AddWithValue("id", f.Reader.Id);
            await hold.ExecuteScalarAsync();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Empty((await Similar(f, f.Reader, "Zálohování serveru")).Items);
            Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }
        Assert.Single((await Similar(f, f.Reader, "Zálohování serveru")).Items);
    }

    /// <summary>Plan §12 acceptance (similar-pages ≤ 500 ms): the saved-draft benchmark corpus of
    /// SearchQueryPostgresTests, with a heading per page, first as drafts and then published. Runs only with
    /// ATLAS_SIMILAR_PERF_OUTPUT set; writes timings and EXPLAIN ANALYZE plans there.</summary>
    [PostgreSqlFact]
    public async Task Similar_pages_benchmark_on_the_search_corpus()
    {
        if (Environment.GetEnvironmentVariable("ATLAS_SIMILAR_PERF_OUTPUT") is not { Length: > 0 } output) return;
        await using var f = await SearchFixture.CreateAsync(pooling: true);
        // The fixture's own space only: 1000 pages of this test (ids by its writer), their documents refreshed, not the shared wiki's.
        await f.ExecuteInSpace("""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              SELECT CAST(md5('similar-perf-'||CAST(@id AS text)||n) AS uuid),g."SpaceId",@id,1 FROM generate_series(1,1000) n
              CROSS JOIN "SpaceGrant" g WHERE g."UserId"=@id AND g."ActiveTo" IS NULL;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT gen_random_uuid(),CAST(md5('similar-perf-'||CAST(@id AS text)||n) AS uuid),1,'Provozní příručka '||n,
                CAST(jsonb_build_object('format','atlas-rich','version',1,'document',jsonb_build_object('type','doc','content',jsonb_build_array(
                  jsonb_build_object('type','heading','attrs',jsonb_build_object('level',2,'headingId','postup-'||n),'content',jsonb_build_array(jsonb_build_object('type','text','text','Postup obnovy zálohy'))),
                  jsonb_build_object('type','paragraph','content',jsonb_build_array(jsonb_build_object('type','text','text',body)))))) AS text),
                '## Postup obnovy zálohy'||chr(10)||body,@id,clock_timestamp(),'web' FROM generate_series(1,1000) n CROSS JOIN (
                  SELECT repeat('Postup obnovy zálohy popisuje kontrolu souborů a ověření výsledku. Při chybě HTTP 401 zkontrolujte přihlášení a oprávnění účtu. ',40) AS body
                ) content;
            ANALYZE "Page"; ANALYZE "PageRevision"; ANALYZE "WorkingSearchDocument";
            """, f.Writer.Id);
        var queries = new[] { "Provozní příručka pro obnovu zálohy", "Postup obnovy zálohy na serveru", "Kontrola souborů po chybě HTTP 401", "unmatchedzephyr nebula" };
        var drafts = await Measure(f, queries);
        await f.ExecuteInSpace("""
            INSERT INTO "PagePublication"("Id","PageId","RevisionId","PublisherId","ActiveFrom","Channel")
              SELECT gen_random_uuid(),r."PageId",r."Id",@id,clock_timestamp(),'web' FROM "PageRevision" r JOIN "Page" p ON p."Id"=r."PageId" WHERE p."OwnerId"=@id;
            UPDATE "Page" p SET "PublishedRevisionId"=r."Id" FROM "PageRevision" r WHERE r."PageId"=p."Id" AND p."OwnerId"=@id;
            ANALYZE "Page"; ANALYZE "SearchDocument"; ANALYZE "PagePublication";
            """, f.Writer.Id);
        var published = await Measure(f, queries);
        await File.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(new { documents = 1000, drafts, published }));
    }

    private static async Task<object[]> Measure(SearchFixture f, string[] queries)
    {
        var results = new List<object>();
        foreach (var query in queries)
        {
            var samples = new List<double>();
            var count = 0;
            for (var i = 0; i < 20; i++)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                count = (await Similar(f, f.Writer, query)).Items.Count;
                samples.Add(timer.Elapsed.TotalMilliseconds);
            }
            var normalized = Atlas.Server.Features.Search.Domain.SearchText.Normalize(query);
            await using var connection = new NpgsqlConnection(f.ConnectionString);
            await connection.OpenAsync();
            await using var explain = new NpgsqlCommand("SET pg_trgm.similarity_threshold=0.3; SET jit=off; EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) "
                + Atlas.Server.Features.Search.SearchQueries.Similar, connection);
            explain.Parameters.AddWithValue("query", normalized);
            explain.Parameters.AddWithValue("words", Atlas.Server.Features.Search.Domain.SearchText.AnyWordQuery(normalized));
            explain.Parameters.AddWithValue("actor", f.Writer.Id);
            explain.Parameters.AddWithValue("stamp", f.Writer.SecurityStamp);
            explain.Parameters.AddWithValue("space", f.SpaceId);
            explain.Parameters.Add(new NpgsqlParameter("exclude", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = DBNull.Value });
            explain.Parameters.AddWithValue("take", 3);
            var plan = (string)(await explain.ExecuteScalarAsync())!;
            results.Add(new { query, count, p95Milliseconds = samples.Order().ElementAt(18), samples, plan });
        }
        return results.ToArray();
    }

    /// <summary>P1 phase 2 (V5): pages similar in meaning follow the lexical suggestions, published pages only, with the
    /// createSuggestions capability; the title is embedded before the handler and never for a space excluded from AI.</summary>
    [PostgreSqlFact]
    public async Task Meaning_matches_follow_lexical_ones_and_never_include_drafts()
    {
        await using var f = await SuggestionFixture.CreateAsync(text =>
            text.Contains("Připojení na VPN z mobilu", StringComparison.Ordinal) || text.Contains("Vzdálená síť pro zaměstnance", StringComparison.Ordinal)
            || text.Contains("tunel", StringComparison.Ordinal) ? "koncept mobilni vpn" : text);
        var vpn = await f.PublishAsync("Připojení k VPN");
        var remote = await f.PublishAsync("Vzdálená síť pro zaměstnance");
        var hidden = await f.PublishAsync("Skrytý tunel", spaceId: f.RestrictedSpaceId, author: f.Admin);
        var draft = await f.DraftAsync("Mobilní tunel");
        var generation = await f.ActivateAsync();
        // Even a draft with an index row (a broken worker) is never offered: only the live publication counts.
        await f.IndexDraftAsync(generation.Id, draft.Id, "Mobilní tunel");
        const string title = "Připojení na VPN z mobilu";

        var writer = await SimilarWithMeaning(f, f.Writer, title, f.SpaceId);

        Assert.Equal([(vpn.Id, "title"), (remote.Id, "meaning")], writer.Items.Select(x => (x.Id, x.MatchedBy)));
        var meaning = writer.Items[1];
        Assert.Equal((false, "Engineering", "Vzdálená síť pro zaměstnance", (string?)null), (meaning.IsDraft, meaning.SpaceName, meaning.Title, meaning.HeadingId));
        Assert.InRange(meaning.Score, 0.75f, 1.0001f);
        Assert.Equal(["query: " + title], f.Sent(AiTaskCatalog.PageCreateSuggest));
        // A WikiAdmin reads the restricted page; the limit is kept and lexical suggestions stay first.
        Assert.Equal(new[] { remote.Id, hidden.Id }.Order(), (await SimilarWithMeaning(f, f.Admin, title, f.SpaceId)).Items.Skip(1).Select(x => x.Id).Order());
        Assert.Equal([vpn.Id], (await SimilarWithMeaning(f, f.Writer, title, f.SpaceId, limit: 1)).Items.Select(x => x.Id));

        // Without the capability: today's answer, byte for byte, and no model call.
        await f.SetTaskAsync(AiTaskCatalog.PageCreateSuggest, false);
        var lexical = await SimilarWithMeaning(f, f.Writer, title + " doma", f.SpaceId);
        var today = await f.Index.As<SimilarPagesResponse>(f.Writer, new FindSimilarPages(title + " doma", f.SpaceId, null, 3));
        Assert.Equal(JsonSerializer.Serialize(today), JsonSerializer.Serialize(lexical));
        Assert.Single(f.Sent(AiTaskCatalog.PageCreateSuggest));
        // A space excluded from AI: its titles never leave Atlas.
        await f.SetTaskAsync(AiTaskCatalog.PageCreateSuggest, true);
        await f.ExcludeAsync(f.OtherSpaceId);
        await SimilarWithMeaning(f, f.Writer, title + " v kanceláři", f.OtherSpaceId);
        Assert.Single(f.Sent(AiTaskCatalog.PageCreateSuggest));
    }

    private static async Task<SimilarPagesResponse> SimilarWithMeaning(SuggestionFixture f, UserAccount actor, string title, Guid spaceId, int limit = 3)
    {
        f.Index.Host.Actor.Current = actor;
        using var scope = f.Index.Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().FindSimilarPagesAsync(
            scope.ServiceProvider.GetRequiredService<WorkingTextEmbedder>(), new FindSimilarPages(title, spaceId, null, limit), default);
    }

    private static Task<SimilarPagesResponse> Similar(SearchFixture f, UserAccount actor, string title, Guid? exclude = null, int limit = FindSimilarPages.DefaultLimit)
        => f.As<SimilarPagesResponse>(actor, new FindSimilarPages(title, f.SpaceId, exclude, limit));

    private static async Task<PageDto> Published(SearchFixture f, string title, params (string Id, string Title)[] headings)
    {
        var rich = System.Text.Json.JsonSerializer.Serialize(new
        {
            format = "atlas-rich", version = 1, plugins = new[] { new { id = "heading-anchors", version = 1 } },
            document = new
            {
                type = "doc",
                content = headings.Select(heading => (object)new
                {
                    type = "heading", attrs = new { level = 2, headingId = heading.Id },
                    content = new[] { new { type = "text", text = heading.Title } },
                }).Append(new { type = "paragraph", content = new[] { new { type = "text", text = "Postup." } } }).ToArray(),
            },
        });
        var markdown = string.Concat(headings.Select(heading => $"## {heading.Title}\nPostup.\n"));
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, title, rich, markdown), f.Writer, default);
        return await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SimilarPagesCollection
{
    public const string Name = "Similar pages";
}
