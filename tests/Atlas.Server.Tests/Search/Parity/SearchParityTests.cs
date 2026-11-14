using NpgsqlTypes;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.AiSuggestions;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;
using static Atlas.Server.Tests.Search.Parity.LegacySearch;

namespace Atlas.Server.Tests.Search.Parity;

/// <summary>
/// Atlas 3.6, no logic in the database: every read-side search function (search, similar pages, hybrid ranking, link candidates,
/// answer sources) and the C# query text that replaced it return the same rows for the same arguments, over one corpus that has
/// what they distinguish: headings with and without anchors, a renamed page (an alias), drafts beside publications, a trashed page,
/// a template, a child page, spaces the reader cannot read and one excluded from AI, a long body, technical words. Each case binds
/// its arguments as production does and runs under its caller's settings.
/// </summary>
public sealed class SearchParityTests
{
    private const string SearchSettings = """
        SELECT set_config('pg_trgm.word_similarity_threshold','0.45',true), set_config('pg_trgm.similarity_threshold','0.45',true),
          set_config('plan_cache_mode','force_custom_plan',true), set_config('jit','off',true),
          set_config('hnsw.iterative_scan','relaxed_order',true), set_config('hnsw.ef_search','100',true), set_config('hnsw.max_scan_tuples','20000',true)
        """;
    private const string SimilarSettings = """
        SELECT set_config('pg_trgm.similarity_threshold','0.3',true), set_config('plan_cache_mode','force_custom_plan',true), set_config('jit','off',true),
          set_config('hnsw.iterative_scan','relaxed_order',true), set_config('hnsw.ef_search','100',true), set_config('hnsw.max_scan_tuples','20000',true)
        """;

    /// <summary>The query text that replaced <paramref name="function"/>, its parameters named as the function's without <c>p_</c>.</summary>
    private static string Current(string function, IReadOnlyList<Arg> args)
        => SearchTexts.TryGetValue(function, out var text) || SuggestionTexts.TryGetValue(function, out text) ? $"SELECT * FROM ({text}) moved" : Call(function, args);

    /// <summary>Search, similar pages and hybrid ranking (SearchQueries).</summary>
    private static readonly Dictionary<string, string> SearchTexts = new(StringComparer.Ordinal)
    {
        ["atlas_search_published"] = SearchQueries.Published,
        ["atlas_search_published_filtered"] = SearchQueries.PublishedFiltered(),
        ["atlas_search_drafts"] = SearchQueries.Drafts(),
        ["atlas_search_all"] = SearchQueries.All,
        ["atlas_search_spaces"] = SearchQueries.Spaces,
        ["atlas_search_did_you_mean"] = SearchQueries.DidYouMean,
        ["atlas_search_hybrid_candidates"] = SearchQueries.HybridCandidates(),
        ["atlas_search_hybrid_all"] = SearchQueries.HybridAll,
        ["atlas_search_hybrid_published"] = SearchQueries.HybridPublished,
        ["atlas_similar_pages"] = SearchQueries.Similar,
        ["atlas_similar_pages_semantic"] = SearchQueries.SimilarSemantic,
    };

    /// <summary>Link candidates, placement, templates, ranking by context and answer sources.</summary>
    private static readonly Dictionary<string, string> SuggestionTexts = new(StringComparer.Ordinal)
    {
        ["atlas_link_candidates"] = Atlas.Server.Features.AiSuggestions.SemanticSuggestionQueries.LinkCandidates,
        ["atlas_link_candidates_semantic"] = Atlas.Server.Features.AiSuggestions.SemanticSuggestionQueries.LinkCandidatesSemantic,
        ["atlas_rank_by_context"] = Atlas.Server.Features.AiSuggestions.SemanticSuggestionQueries.RankByContext,
        ["atlas_placement_parent"] = Atlas.Server.Features.AiSuggestions.SemanticSuggestionQueries.Placement,
        ["atlas_template_suggestion"] = Atlas.Server.Features.AiSuggestions.SemanticSuggestionQueries.Template,
        ["atlas_answer_sources"] = Atlas.Server.Features.Assistant.Sources.AnswerSourceQueries.Sources,
        ["atlas_answer_source_status"] = Atlas.Server.Features.Assistant.Sources.AnswerSourceQueries.Status,
    };

    [Fact]
    public async Task Every_read_function_and_its_query_text_return_the_same_rows()
    {
        await using var f = await SuggestionFixture.CreateAsync();
        var index = f.Index;
        var printer = await index.PublishNewAsync("Tiskárna v kanceláři",
            "Úvod k tiskárně.\n## Nastavení tiskárny\nZapni tiskarnu a ověř toner.\n## Kontakty\nVolej C# tým, C++ knihovna PageRevisionStore a my_func.name.\n## Kontakty\nDruhý seznam.",
            null, ("Nastavení tiskárny", "nastaveni"), ("Kontakty", "kontakty"));
        var scanner = await index.PublishNewAsync("Skener dokumentů", "Skenování smluv do archivu.");
        await index.EditAndPublishAsync(scanner.Id, "Skenování", "Skenování smluv do archivu a tisk.");
        var supplies = await f.DraftAsync("Kancelářské potřeby", "Papír do tiskárny a sešívačka.");
        var backup = await index.PublishNewAsync("Postup obnovy zálohy", "## Obnova\nObnov zálohu databáze z tiskového serveru.", null, ("Obnova", "obnova"));
        await index.SaveDraftAsync(backup.Id, "Postup obnovy zálohy databáze", "## Obnova\nObnov zálohu databáze. Tiskárna stojí.", ("Obnova", "obnova"));
        var archive = await index.PublishNewAsync("Archivní tiskárna", "Stará tiskárna v archivu.", f.OtherSpaceId);
        var secret = await f.PublishAsync("Tajný tiskový server", "Tiskový server pro vedení.", f.RestrictedSpaceId, author: f.Admin);
        var excluded = await f.PublishAsync("Vyloučená tiskárna", "Tiskárna mimo AI.", f.ExcludedSpaceId);
        var trashed = await index.PublishNewAsync("Stará tiskárna", "Vyřazená tiskárna.");
        var template = await f.PublishAsync("Provozní postup", "## Postup\nKroky obnovy.");
        await f.MakeTemplateAsync(template.Id);
        var network = await f.PublishAsync("Síť", "Síťová infrastruktura.");
        var vpn = await f.PublishAsync("VPN připojení", "Připojení k síti přes VPN.", parentId: network.Id);
        var restricted = await f.PublishAsync("Omezená tiskárna", "Jen pro vlastníka.");
        await f.RestrictAsync(restricted.Id);
        var longBody = string.Join(' ', Enumerable.Range(0, 3000).Select(i => i % 997 == 996 ? "tiskarnou" : "slovo" + (i % 50)));
        var manual = await index.PublishNewAsync("Dlouhý návod", longBody);
        await f.ExcludeAsync(f.ExcludedSpaceId);
        var generation = await f.ActivateAsync();
        await f.TrashAsync(trashed.Id);
        await LegacySearch.InstallAsync(index.Host.ConnectionString);
        var connection = index.Host.ConnectionString;
        var since = DateTime.UtcNow.AddDays(-1);
        float[] Vector(string text) => FakeAi.Vector(f.Fake.Meaning(text));
        string VectorText(string text) => QueryVectorText.Format(Vector(text));
        string QueryVectorOf(string text) => QueryVectorText.Format(FakeAi.Vector(f.Fake.Meaning(IndexFixture.Embeddings().QueryPrefix + text)));

        var rows = new Dictionary<string, int>();
        async Task Same(string function, string settings, string because, params Arg[] args)
        {
            var count = await AssertSameAsync(connection, settings, Call(function, args), Current(function, args), args, $"{function}, {because}");
            lock (rows)
            {
                rows[function] = rows.GetValueOrDefault(function) + count;
            }
        }

        Arg[] Lexical(string raw, UserAccount actor, Guid? space)
        {
            var p = SearchQueryParameters.Create(raw, SearchText.Normalize(raw), actor.Id, actor.SecurityStamp, space);
            return [new("query", p.Query, NpgsqlDbType.Text), new("prefix", p.Prefix, NpgsqlDbType.Text), new("fts", p.Fts, NpgsqlDbType.Text),
                new("phrase", p.Phrase, NpgsqlDbType.Boolean), new("fuzzy", p.Fuzzy, NpgsqlDbType.Boolean), new("actor", p.Actor, NpgsqlDbType.Uuid),
                new("stamp", p.Stamp, NpgsqlDbType.Text), new("space", p.Space, NpgsqlDbType.Uuid)];
        }
        Arg[] Window(int take, int skip, DateTime? changedSince, bool latest) =>
            [new("take", take, NpgsqlDbType.Integer), new("skip", skip, NpgsqlDbType.Integer),
             new("changed_since", changedSince, NpgsqlDbType.TimestampTz), new("latest", latest, NpgsqlDbType.Boolean)];
        Arg[] Semantic(string raw, float minSimilarity = 0.45f) =>
            [new("generation", generation.Id, NpgsqlDbType.Uuid), new("embedding", QueryVectorOf(raw), NpgsqlDbType.Text, "vector"),
             new("min_similarity", minSimilarity, NpgsqlDbType.Real)];

        string[] queries = ["tiskarna", "tisk", "\"nastaveni tiskarny\"", "skener", "kancelar", "tiskrna", "c#", "page revision store",
            "zaloha databaze", "kontakty", "neexistujici slovo", "postup obnovy", "slovo7", "tiskarnou", "engineering", "archiv",
            "tiskarna v kancelri", "skenr dokumentu"];
        UserAccount[] actors = [f.Writer, f.Reader, f.Admin];
        // Every comparison reads on a connection and in a transaction of its own, so the queries run side by side: one after
        // another they took 145 s and set the wall time of the batch.
        await Parallel.ForEachAsync(queries, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (raw, _) =>
        {
            foreach (var actor in actors)
            {
                var who = $"{raw} as {actor.Username}";
                foreach (var space in new Guid?[] { null, f.SpaceId })
                {
                    var lexical = Lexical(raw, actor, space);
                    await Same("atlas_search_published", SearchSettings, who, [.. lexical, new("take", 6, NpgsqlDbType.Integer), new("skip", 0, NpgsqlDbType.Integer)]);
                    await Same("atlas_search_spaces", SearchSettings, who, lexical);
                    await Same("atlas_search_did_you_mean", SearchSettings, who, lexical[0], lexical[5], lexical[6], lexical[7]);
                    foreach (var (take, skip, changedSince, latest) in new[] { (6, 0, (DateTime?)null, false), (2, 1, since, false), (6, 0, null, true) })
                    {
                        var window = Window(take, skip, changedSince, latest);
                        await Same("atlas_search_published_filtered", SearchSettings, who, [.. lexical, .. window]);
                        await Same("atlas_search_drafts", SearchSettings, who, [.. lexical, .. window]);
                        await Same("atlas_search_all", SearchSettings, who, [.. lexical, .. window]);
                        await Same("atlas_search_hybrid_published", SearchSettings, who, [.. lexical, .. window, .. Semantic(raw)]);
                        await Same("atlas_search_hybrid_all", SearchSettings, who, [.. lexical, .. window, .. Semantic(raw)]);
                        await Same("atlas_search_hybrid_candidates", SearchSettings, who,
                            [.. lexical, new("lexical_take", take + skip + 20, NpgsqlDbType.Integer), window[2], window[3], .. Semantic(raw)]);
                    }
                }
                var normalized = SearchText.Normalize(raw);
                foreach (var exclude in new Guid?[] { null, printer.Id })
                {
                    await Same("atlas_similar_pages", SimilarSettings, who,
                        new("query", normalized, NpgsqlDbType.Text), new("words", SearchText.AnyWordQuery(normalized), NpgsqlDbType.Text),
                        new("actor", actor.Id, NpgsqlDbType.Uuid), new("stamp", actor.SecurityStamp, NpgsqlDbType.Text),
                        new("space", null, NpgsqlDbType.Uuid), new("exclude", exclude, NpgsqlDbType.Uuid), new("take", 5, NpgsqlDbType.Integer));
                    await Same("atlas_similar_pages_semantic", SimilarSettings, who,
                        new("generation", generation.Id, NpgsqlDbType.Uuid), new("vector", VectorText(raw), NpgsqlDbType.Text, "vector"),
                        new("actor", actor.Id, NpgsqlDbType.Uuid), new("stamp", actor.SecurityStamp, NpgsqlDbType.Text),
                        new("space", null, NpgsqlDbType.Uuid), new("exclude", exclude, NpgsqlDbType.Uuid), new("take", 5, NpgsqlDbType.Integer),
                        new("min_similarity", 0.75f, NpgsqlDbType.Real));
                }
                foreach (var page in new Guid?[] { null, printer.Id })
                {
                    var p = SearchQueryParameters.Create(raw, normalized, actor.Id, actor.SecurityStamp, null);
                    await Same("atlas_answer_sources", SearchSettings, who,
                        new("actor", actor.Id, NpgsqlDbType.Uuid), new("stamp", actor.SecurityStamp, NpgsqlDbType.Text), new("space", null, NpgsqlDbType.Uuid),
                        new("page", page, NpgsqlDbType.Uuid), new("query", p.Query, NpgsqlDbType.Text), new("prefix", p.Prefix, NpgsqlDbType.Text),
                        new("fts", p.Fts, NpgsqlDbType.Text), new("phrase", false, NpgsqlDbType.Boolean), new("generation", generation.Id, NpgsqlDbType.Uuid),
                        new("embedding", QueryVectorOf(raw), NpgsqlDbType.Text, "vector"), new("min_similarity", 0.45f, NpgsqlDbType.Real),
                        new("pages", 4, NpgsqlDbType.Integer), new("take", 6, NpgsqlDbType.Integer), new("per_page", 2, NpgsqlDbType.Integer));
                }
                foreach (var space in new[] { f.SpaceId, f.OtherSpaceId, f.ExcludedSpaceId })
                {
                    await Same("atlas_placement_parent", SimilarSettings, $"{who} in {space}",
                        new("generation", generation.Id, NpgsqlDbType.Uuid), new("vector", VectorText(raw), NpgsqlDbType.Text, "vector"),
                        new("space", space, NpgsqlDbType.Uuid), new("actor", actor.Id, NpgsqlDbType.Uuid), new("stamp", actor.SecurityStamp, NpgsqlDbType.Text),
                        new("root_allowed", space == f.SpaceId, NpgsqlDbType.Boolean), new("min_similarity", 0f, NpgsqlDbType.Real));
                    await Same("atlas_template_suggestion", SimilarSettings, $"{who} in {space}",
                        new("generation", generation.Id, NpgsqlDbType.Uuid), new("vector", VectorText(raw), NpgsqlDbType.Text, "vector"),
                        new("title", normalized, NpgsqlDbType.Text), new("space", space, NpgsqlDbType.Uuid), new("actor", actor.Id, NpgsqlDbType.Uuid),
                        new("stamp", actor.SecurityStamp, NpgsqlDbType.Text), new("min_similarity", 0f, NpgsqlDbType.Real));
                }
            }
        });

        var pages = new[] { printer.Id, scanner.Id, supplies.Id, backup.Id, archive.Id, secret.Id, excluded.Id, trashed.Id, template.Id, network.Id,
            vpn.Id, restricted.Id, manual.Id };
        string[] phrases = ["tiskarna", "skenovani", "postup obnovy zalohy", "vpn pripojeni", "sit", "neznamy pojem"];
        var phraseArray = "{" + string.Join(',', phrases.Select(x => "\"" + x + "\"")) + "}";
        var vectorArray = "{" + string.Join(',', phrases.Select(x => "\"" + VectorText(x) + "\"")) + "}";
        foreach (var actor in actors)
        {
            foreach (var exclude in new[] { printer.Id, vpn.Id })
            {
                await Same("atlas_link_candidates", SimilarSettings, $"as {actor.Username} without {exclude}",
                    new("phrases", phraseArray, NpgsqlDbType.Text, "text[]"), new("actor", actor.Id, NpgsqlDbType.Uuid),
                    new("stamp", actor.SecurityStamp, NpgsqlDbType.Text), new("exclude", exclude, NpgsqlDbType.Uuid), new("space", f.SpaceId, NpgsqlDbType.Uuid));
                await Same("atlas_link_candidates_semantic", SimilarSettings, $"as {actor.Username} without {exclude}",
                    new("generation", generation.Id, NpgsqlDbType.Uuid), new("phrases", phraseArray, NpgsqlDbType.Text, "text[]"),
                    new("vectors", vectorArray, NpgsqlDbType.Text, "vector[]"), new("actor", actor.Id, NpgsqlDbType.Uuid),
                    new("stamp", actor.SecurityStamp, NpgsqlDbType.Text), new("exclude", exclude, NpgsqlDbType.Uuid), new("min_similarity", 0f, NpgsqlDbType.Real));
            }
            foreach (var context in new[] { "tiskarna", "sit a vpn" })
            {
                await Same("atlas_rank_by_context", SimilarSettings, $"{context} as {actor.Username}",
                    new("generation", generation.Id, NpgsqlDbType.Uuid), new("vector", VectorText(context), NpgsqlDbType.Text, "vector"),
                    new("candidates", "{" + string.Join(',', pages) + "}", NpgsqlDbType.Text, "uuid[]"), new("actor", actor.Id, NpgsqlDbType.Uuid),
                    new("stamp", actor.SecurityStamp, NpgsqlDbType.Text));
            }
            var revisions = new List<Guid>();
            using (var session = index.Host.OpenSession())
            {
                foreach (var page in pages)
                {
                    revisions.Add((await session.GetAsync<Page>(page)).PublishedRevisionId ?? Guid.NewGuid());
                }
            }
            revisions[0] = Guid.NewGuid(); // a changed source
            await Same("atlas_answer_source_status", SearchSettings, $"as {actor.Username}",
                new("actor", actor.Id, NpgsqlDbType.Uuid), new("stamp", actor.SecurityStamp, NpgsqlDbType.Text),
                new("pages", "{" + string.Join(',', pages) + "}", NpgsqlDbType.Text, "uuid[]"),
                new("revisions", "{" + string.Join(',', revisions) + "}", NpgsqlDbType.Text, "uuid[]"));
        }
        // A stale security stamp sees nothing, in every function.
        var stale = Lexical("tiskarna", f.Writer, null);
        stale[6] = stale[6] with { Value = "stale" };
        await Same("atlas_search_published", SearchSettings, "stale stamp", [.. stale, new("take", 6, NpgsqlDbType.Integer), new("skip", 0, NpgsqlDbType.Integer)]);
        await Same("atlas_search_all", SearchSettings, "stale stamp", [.. stale, .. Window(6, 0, null, false)]);
        // The corpus gives every function something to return, so equal rows are not two empty lists.
        Assert.All(rows, pair => Assert.True(pair.Value > 0, pair.Key + " returned nothing in every case: " + string.Join(", ", rows)));
        Assert.Equal(18, rows.Count);
    }
}
