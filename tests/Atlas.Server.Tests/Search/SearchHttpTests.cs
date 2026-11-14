using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Search;

public sealed class SearchHttpTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Prefix_pass_excludes_fuzzy_only_hits_and_full_search_still_returns_them()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_search_prefix_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        // The test's own space, searched by it: a wiki administrator of the shared site reads every test's pages.
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        foreach (var title in new[] { "Authentication handbook", "Authentification setup" })
        {
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
                new { spaceId, title, contentJson = SearchFixture.Rich(title), markdown = title }), HttpStatusCode.OK);
        }

        var prefix = await AtlasHttpSite.Json(await admin.GetAsync($"/api/search?q=authentication&fuzzy=false&spaceId={spaceId}"), HttpStatusCode.OK);
        Assert.Equal("Authentication handbook", Assert.Single(prefix["items"]!.AsArray())!["title"]!.GetValue<string>());
        var full = await AtlasHttpSite.Json(await admin.GetAsync($"/api/search?q=authentication&spaceId={spaceId}"), HttpStatusCode.OK);
        Assert.Equal(2, full["items"]!.AsArray().Count);
        using var anonymous = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/search?q=authentication&fuzzy=false")).StatusCode);
    }

    /// <summary>AI-24: without AI the search JSON is byte for byte the contract before phase 2. The body is read into a copy of
    /// that contract and written again with the server's own options: an extra field (semantic, passage, didYouMean), a
    /// different order or format would differ.</summary>
    [PostgreSqlFact]
    public async Task Search_json_is_unchanged_without_ai()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_search_json_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        // Searched in the test's own space: other tests' hits may carry fields (resources, relaxed) this contract has no case for.
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        foreach (var (title, publish) in new[] { ("Authentication handbook", true), ("Authentication setup", true), ("Authentication FAQ", true), ("Authentication draft", false) })
        {
            var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
                new { spaceId, title, contentJson = SearchFixture.Rich(title + " guide"), markdown = title + " guide" }), HttpStatusCode.OK);
            if (publish)
            {
                await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{page["id"]!.GetValue<string>()}/publish",
                    new { expectedVersion = page["version"]!.GetValue<int>() }), HttpStatusCode.OK);
            }
        }
        var options = site.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

        foreach (var query in new[] { "/api/search?q=authentication", "/api/search?q=authentication&content=published&limit=2", "/api/search?q=authentication&fuzzy=false" })
        {
            var response = await admin.GetAsync(query + "&spaceId=" + spaceId);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var legacy = JsonSerializer.Deserialize<LegacySearchResponse>(body, options)!;
            Assert.NotEmpty(legacy.Items);
            Assert.Equal(JsonSerializer.Serialize(legacy, options), body);
        }
    }

    [PostgreSqlFact]
    public async Task Similar_pages_endpoint_binds_parameters_and_answers_signed_in_users()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_similar_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        // Titles of this test's own words: similar pages read every space a wiki administrator reads, and other tests' titles such as
        // "Připojení k VPN" score above the 0.35 threshold against a query that shares their words.
        var (subject, target) = (Guid.NewGuid().ToString("N")[..10], Guid.NewGuid().ToString("N")[..10]);
        string pageId = "";
        foreach (var title in new[] { $"{subject} k {target}", $"{subject} k {target} doma" })
        {
            var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
                new { spaceId, title, contentJson = SearchFixture.Rich(title), markdown = title }), HttpStatusCode.OK);
            pageId = pageId == "" ? page["id"]!.GetValue<string>() : pageId;
        }
        var query = "/api/search/similar-pages?title=" + Uri.EscapeDataString($"{subject} na {target} z mobilu") + "&spaceId=" + spaceId;

        var similar = await AtlasHttpSite.Json(await admin.GetAsync(query + "&limit=1"), HttpStatusCode.OK);
        var item = Assert.Single(similar["items"]!.AsArray())!;
        Assert.Equal((pageId, spaceId, $"{subject} k {target}", true, "title"), (item["id"]!.GetValue<string>(), item["spaceId"]!.GetValue<string>(),
            item["title"]!.GetValue<string>(), item["isDraft"]!.GetValue<bool>(), item["matchedBy"]!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(item["spaceName"]!.GetValue<string>()));
        Assert.InRange(item["score"]!.GetValue<double>(), 0.35, 1.0);
        Assert.Null(item["headingId"]);
        var excluded = await AtlasHttpSite.Json(await admin.GetAsync(query + "&excludePageId=" + pageId), HttpStatusCode.OK);
        Assert.DoesNotContain(excluded["items"]!.AsArray(), hit => hit!["id"]!.GetValue<string>() == pageId);
        Assert.Single(excluded["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/search/similar-pages?title=" + new string('a', 201))).StatusCode);
    }

    /// <summary>The search contract before phase 2 (S7d), copied: positional members first, then the init members, in order.</summary>
    private sealed record LegacySearchResponse(IReadOnlyList<LegacySearchHit> Items, bool HasMore)
    {
        public IReadOnlyList<SearchSpaceHit> Spaces { get; init; } = [];
        public int? NextOffset { get; init; }
    }

    private sealed record LegacySearchHit(Guid Id, Guid SpaceId, string SpaceName, string Title, string Snippet)
    {
        public IReadOnlyList<SearchWidget> Widgets { get; init; } = [];
        public bool IsDraft { get; init; }
        public DateTime? ChangedAt { get; init; }
        public DateTime? PublishedAt { get; init; }
        public string? HeadingTitle { get; init; }
        public string? HeadingId { get; init; }
        public string? MatchedAlias { get; init; }
    }
}
