using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.SearchGaps;

public sealed class SearchGapsHttpTests
{
    [PostgreSqlFact]
    public async Task Search_gap_endpoints_bind_days_and_resolution_body()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_search_gaps_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(admin));
        // A word of this test's own: a miss is recorded only when nothing answers the query, and a wiki administrator of the
        // shared site reads every test's pages and spaces.
        var word = string.Concat(Guid.NewGuid().ToString("N")[..8].Select(c => (char)('a' + c % 16)));
        var display = "chybějící návod " + word;
        var normalized = "chybejici navod " + word;

        // recordMiss and contextSpaceId bind on /api/search; the flush path stores the miss in the context space.
        await AtlasHttpSite.Json(await admin.GetAsync("/api/search?q=" + Uri.EscapeDataString("Chybějící návod " + word)
            + "&recordMiss=true&contextSpaceId=" + spaceId), HttpStatusCode.OK);
        await site.Services.GetRequiredService<SearchMissFlusher>().FlushAsync(all: true, default);
        Assert.Equal(1L, await site.ScalarAsync("""SELECT count(*) FROM "SearchQueryLog" WHERE "SpaceId"=@space AND "NormalizedQuery"=@query""",
            ("space", spaceId), ("query", normalized)));
        await site.ExecuteAsync("""
            INSERT INTO "SearchQueryLog"("SpaceId","NormalizedQuery","DisplayQuery","UserKey","OccurredOn")
            SELECT @space,@query,@display,decode(md5(random()::text),'hex'),(now() AT TIME ZONE 'UTC')::date FROM generate_series(1,2)
            """, ("space", spaceId), ("query", normalized), ("display", display));

        var gaps = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/search-gaps?days=30"), HttpStatusCode.OK);
        var gap = Assert.Single(gaps["items"]!.AsArray())!;
        Assert.Equal((display, normalized, 3, 3, 90), (gap["query"]!.GetValue<string>(), gap["normalizedQuery"]!.GetValue<string>(),
            gap["users"]!.GetValue<int>(), gaps["minimumUsers"]!.GetValue<int>(), gaps["retentionDays"]!.GetValue<int>()));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", gap["lastSearchedOn"]!.GetValue<string>());
        // Without a reported or unanswered person the JSON is what it was before phase 3 (Q5).
        Assert.Null(gap["reported"]);
        Assert.Null(gap["unanswered"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/spaces/{spaceId}/search-gaps?days=5")).StatusCode);

        // Q5 HTTP: the zero-result rescue and the feedback routes bind and answer 204 / Problem Details (401 without a session: AnonymousApiHttpTests).
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/search/report-missing",
            new { query = "Chybějící návod " + word, contextSpaceId = spaceId })).StatusCode);
        Assert.Equal(1L, await site.ScalarAsync("""SELECT count(*) FROM "SearchQueryLog" WHERE "Reason"='reported' AND "SpaceId"=@space""", ("space", spaceId)));
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/search/report-missing", new { query = "ab" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/ai/messages/{Guid.NewGuid()}/feedback", new { rating = "up" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/ai/messages/{Guid.NewGuid()}/feedback", new { rating = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/ai/messages/{Guid.NewGuid()}/report-missing")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post,
            $"/api/spaces/{spaceId}/search-gaps/resolutions", new { normalizedQuery = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post,
            $"/api/spaces/{spaceId}/search-gaps/resolutions", new { normalizedQuery = normalized })).StatusCode);
        var resolved = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/search-gaps"), HttpStatusCode.OK);
        Assert.Empty(resolved["items"]!.AsArray());
    }
}
