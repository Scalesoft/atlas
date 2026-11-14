using Atlas.Server.Features.Assistant.Sources;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Platform.Content;
using Atlas.Server.Tests.AiIndex;
using Xunit;

namespace Atlas.Server.Tests.Search;

/// <summary>Migration 2026102010 (the owner's "Co je to freebusy" and "API" reports): OpenAPI specifications are searchable,
/// title words rank above the body, and a page without text never comes back as an empty meaning-only hit.</summary>
public sealed class SearchQualityTests
{
    private const string Spec = """
        openapi: 3.1.0
        info:
          title: NIS Portál
          version: '10.3.0'
          description: Synchronizační API mezi NIS a Portálem.
        paths:
          /v1/calendar/freebusy:
            parameters:
              - name: resourceId
                in: query
                description: Lékař nebo ordinace
            get:
              operationId: getCalendarAvailability
              tags: [Calendar]
              summary: Vrátí obsazenost kalendáře
              responses:
                '200':
                  description: Obsazené intervaly
                  content:
                    application/json:
                      schema:
                        $ref: '#/components/schemas/FreeBusy'
        components:
          schemas:
            FreeBusy:
              description: Obsazené a volné intervaly
              properties:
                busy:
                  type: array
                  description: Obsazené intervaly
        """;

    [Fact]
    public void The_ai_text_of_an_openapi_component_has_a_section_per_operation_and_schema()
    {
        var text = OpenApiSearchText.Of(OpenApiDocuments.Rich(Spec));

        Assert.Contains("## API NIS Portál v10.3.0", text);
        Assert.Contains("Synchronizační API mezi NIS a Portálem.", text);
        Assert.Contains("### GET /v1/calendar/freebusy\nVrátí obsazenost kalendáře", text);
        Assert.Contains("operationId: getCalendarAvailability · tagy: Calendar", text);
        Assert.Contains("- parametr resourceId (query) — Lékař nebo ordinace", text);
        Assert.Contains("- odpověď 200 — Obsazené intervaly FreeBusy", text);
        Assert.Contains("### Schéma FreeBusy\nObsazené a volné intervaly", text);
        Assert.Contains("- busy (array) — Obsazené intervaly", text);
        Assert.Equal("", OpenApiSearchText.Of(IndexFixture.Rich("Bez API komponenty")));
        Assert.Equal("", OpenApiSearchText.Of(OpenApiDocuments.Rich("openapi: [")));
    }

    [Theory]
    [InlineData("Co je to freebusy", "freebusy")]
    [InlineData("Jak se připojím k VPN z domova?", "připojím VPN domova?")]
    [InlineData("API 10.3", "API 10.3")]
    [InlineData("co je to", "co je to")]
    [InlineData("\"co je to\" freebusy", "\"co je to\" freebusy")]
    public void The_agent_searches_by_keywords(string query, string keywords)
        => Assert.Equal(keywords, AnswerQueryText.Keywords(query));

    [PostgreSqlFact]
    public async Task Operations_paths_and_schemas_of_an_openapi_component_are_found_by_words_and_by_meaning()
    {
        await using var f = await HybridSearchFixture.CreateAsync(text =>
            text.Contains("kdy má lékař volno", StringComparison.Ordinal) || text.Contains("Vrátí obsazenost kalendáře", StringComparison.Ordinal)
                ? "doctor availability calendar"
                : text);
        var page = await f.Index.Pages.CreateAsync(new CreatePageRequest(f.Index.SpaceId, null, "NIS – Portál – API", OpenApiDocuments.Rich(Spec), ""),
            f.Writer, default);
        await f.Index.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);
        await f.Index.DeliverAsync(page.Id);
        await f.IndexAsync();

        foreach (var words in new[] { "freebusy", "getCalendarAvailability", "resourceId", "obsazenost" })
        {
            var lexical = await f.InvokeAsync(f.Reader, words, null);
            Assert.Equal(page.Id, Assert.Single(lexical.Items).Id);
        }
        var semantic = await f.FindAsync(f.Reader, "kdy má lékař volno");
        var hit = Assert.Single(semantic.Items);
        Assert.True(hit.Semantic);
        Assert.Equal("GET /v1/calendar/freebusy", hit.Passage?.HeadingTitle);
        Assert.StartsWith("Vrátí obsazenost kalendáře", hit.Passage?.Text);
    }

    [PostgreSqlFact]
    public async Task A_query_word_in_the_title_ranks_above_pages_that_only_mention_it()
    {
        await using var f = await HybridSearchFixture.CreateAsync(text =>
            text.StartsWith("query: API", StringComparison.Ordinal) || text.Contains("Integrace volá API", StringComparison.Ordinal)
                ? "programming interface"
                : text);
        var mention = await f.PublishAsync("Integrace", "Integrace volá API portálu a API pojišťovny.");
        var titled = await f.PublishAsync("NIS – Portál – API", "Synchronizace mezi systémy.");
        await f.IndexAsync();

        var response = await f.FindAsync(f.Reader, "API");

        Assert.True(response.Semantic);
        Assert.Equal([titled, mention], response.Items.Select(x => x.Id));
        Assert.Equal(titled, (await f.InvokeAsync(f.Reader, "API", null)).Items[0].Id);
    }

    [PostgreSqlFact]
    public async Task Titles_then_earlier_titles_then_headings_then_the_body()
    {
        // Migration 2026102720 (the owner's "KBN" report): what is derived from a page title outranks a heading inside a page.
        await using var f = await HybridSearchFixture.CreateAsync();
        var body = await f.PublishAsync("Nasazení", "Verze na KBN je aktuální.");
        var heading = await f.PublishAsync("External access", "## KBN\nAdresy portálu.", null, ("KBN", "kbn"));
        var renamed = await f.PublishAsync("Deployment in KBN", "Servery.");
        await f.Index.EditAndPublishAsync(renamed, "Nemocnice Na Kopečku", "Servery.");
        var titled = await f.PublishAsync("Deployment in KBN", "## Popis nasazení v KBN\nServery.", null, ("Popis nasazení v KBN", "popis"));
        await f.IndexAsync();

        foreach (var response in new[] { await f.FindAsync(f.Reader, "KBN"), await f.InvokeAsync(f.Reader, "KBN", null) })
        {
            Assert.Equal([titled, renamed, heading, body], response.Items.Select(x => x.Id));
            Assert.Equal("Deployment in KBN", response.Items[1].MatchedAlias);
        }
    }

    [PostgreSqlFact]
    public async Task A_page_without_text_is_never_a_meaning_only_hit()
    {
        await using var f = await HybridSearchFixture.CreateAsync(text =>
            text.StartsWith("query: freebusy", StringComparison.Ordinal) || text.EndsWith("DisplayTools", StringComparison.Ordinal)
                ? "free busy display"
                : text);
        await f.PublishAsync("DisplayTools", "");
        await f.IndexAsync();

        var response = await f.FindAsync(f.Reader, "freebusy");

        Assert.True(response.Semantic);
        Assert.Empty(response.Items);
    }
}
