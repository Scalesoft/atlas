using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Tests.Mcp;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Projector.ProjectorDocuments;

namespace Atlas.Server.Tests.Projector;

/// <summary>/api/v1/projector (spec §6): the same operations as the MCP tools for personal tokens on the channel rest, with the scopes of
/// /api/v1/pages (read; draft for every write) and an API activity row per write attempt.</summary>
public sealed class ProjectorApiTests
{
    internal static readonly string[] Routes =
    [
        "DELETE /api/v1/projector/pages/{pageId:guid}/requirements/{blockId}",
        "GET /api/v1/projector/pages/{pageId:guid}/definitions",
        "GET /api/v1/projector/pages/{pageId:guid}/requirements",
        "GET /api/v1/projector/pages/{pageId:guid}/requirements/cycle",
        "GET /api/v1/projector/pages/{pageId:guid}/requirements/{blockId}/dependents",
        "GET /api/v1/projector/pages/{pageId:guid}/requirements/{blockId}/history",
        "GET /api/v1/projector/prerequisites",
        "GET /api/v1/projector/spaces/{spaceId:guid}/catalog",
        "GET /api/v1/projector/spaces/{spaceId:guid}/overview",
        "GET /api/v1/projector/spaces/{spaceId:guid}/presets",
        "GET /api/v1/projector/spaces/{spaceId:guid}/resolve",
        "GET /api/v1/projector/spaces/{spaceId:guid}/waiting",
        "PATCH /api/v1/projector/pages/{pageId:guid}/requirements",
        "PATCH /api/v1/projector/pages/{pageId:guid}/requirements/{blockId}",
        "POST /api/v1/projector/pages/{pageId:guid}/deployments",
        "POST /api/v1/projector/pages/{pageId:guid}/requirements",
        "POST /api/v1/projector/pages/{pageId:guid}/requirements/from-deployment",
        "POST /api/v1/projector/pages/{pageId:guid}/requirements/new",
        "POST /api/v1/projector/pages/{pageId:guid}/requirements/{blockId}/move",
        "PUT /api/v1/projector/pages/{pageId:guid}/catalog",
    ];

    // A requirement heading is addressed by its headingId (3.6); a requirement cannot nest, so a move goes after another one.
    private const string B1 = "portal.ssl", C1 = "00000000-0000-4000-8000-0000000000c1",
        C2 = "00000000-0000-4000-8000-0000000000c2";

    [PostgreSqlFact]
    public async Task Every_route_is_listed_and_every_write_needs_the_draft_scope_and_is_audited()
    {
        await using var f = await ProjectorMcpToolTests.SiteAsync();
        var endpoints = f.Site.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/projector", StringComparison.Ordinal) == true).ToArray();

        Assert.Equal(Routes, endpoints.Select(Route).Order(StringComparer.Ordinal));
        var writes = endpoints.Where(e => !Route(e).StartsWith("GET ", StringComparison.Ordinal)).ToArray();
        Assert.All(writes, e => Assert.Contains(e.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == PersonalApiEndpoints.WritePolicy));
        Assert.All(writes, e => Assert.NotNull(e.Metadata.GetMetadata<ApiWriteAudit>()));
        // Each write parses and validates a whole page: bounded per token and account as the Markdown writes are.
        Assert.All(writes, e => Assert.Same(PersonalApiConcurrency.MarkdownWrites, e.Metadata.GetMetadata<PersonalApiConcurrency>()));
        Assert.All(endpoints, e => Assert.Contains(e.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == PersonalApiEndpoints.ReadPolicy));
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task A_draft_token_runs_every_operation_as_its_owner_and_a_read_token_writes_nothing()
    {
        await using var f = await ProjectorMcpToolTests.SiteAsync();
        var space = await ProjectorMcpToolTests.FirstSpaceAsync(f);
        var catalog = await McpSite.PageAsync(f.Admin, space, "Katalog", Json(Block(C1, "portal.requirements", "Strukturované požadavky"),
            Block(C2, "portal.orders", "Objednávky", requires: "portal.requirements")), "");
        var page = await McpSite.PageAsync(f.Admin, space, "KBN", Json(RequirementHeading(B1, "SSL", direction: "inbound")), "");
        await f.SetLevelAsync(f.AdminId, "write");
        var (tokenId, secret) = await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read", "write"], ["rest"]);
        using var api = Bearer(f, secret);

        await Ok(await api.PutAsJsonAsync($"/api/v1/projector/pages/{catalog.Id}/catalog", new { isCatalog = true }));
        var keys = (await Ok(await api.GetAsync($"/api/v1/projector/spaces/{space}/catalog")))["definitions"]!.AsArray().Select(x => x!["key"]!.GetValue<string>());
        Assert.Equal(["portal.requirements", "portal.orders"], keys);
        var resolved = await Ok(await api.GetAsync($"/api/v1/projector/spaces/{space}/resolve?keys=portal.orders&presentKeys=portal.requirements"));
        Assert.Equal(["portal.orders"], resolved["items"]!.AsArray().Select(x => x!["definition"]!["key"]!.GetValue<string>()));
        var version = (await Ok(await api.GetAsync($"/api/v1/projector/pages/{page.Id}/requirements")))["version"]!.GetValue<int>();

        var updated = await Ok(await api.PatchAsJsonAsync($"/api/v1/projector/pages/{page.Id}/requirements/{B1}",
            new { fields = new { state = "waiting_for_input", party = "IT KBN" }, expectedVersion = version }));
        var batch = await Ok(await api.PatchAsJsonAsync($"/api/v1/projector/pages/{page.Id}/requirements",
            new { updates = new[] { new { blockId = B1, change = new { date = "2026-10-01" } } }, expectedVersion = updated["version"]!.GetValue<int>() }));
        var inserted = await Ok(await api.PostAsJsonAsync($"/api/v1/projector/pages/{page.Id}/requirements",
            new { keys = new[] { "portal.orders" }, position = new { afterBlockId = B1 }, expectedVersion = batch["version"]!.GetValue<int>() }));
        Assert.Equal(["portal.requirements", "portal.orders"], inserted["inserted"]!.AsArray().Select(x => x!["key"]!.GetValue<string>()));
        var orders = inserted["inserted"]![1]!["blockId"]!.GetValue<string>();
        var moved = await Ok(await api.PostAsJsonAsync($"/api/v1/projector/pages/{page.Id}/requirements/{orders}/move",
            new { position = new { afterBlockId = B1 }, expectedVersion = inserted["version"]!.GetValue<int>() }));
        var removed = await Ok(await api.DeleteAsync($"/api/v1/projector/pages/{page.Id}/requirements/{orders}?expectedVersion={moved["version"]!.GetValue<int>()}"));
        var stale = await api.DeleteAsync($"/api/v1/projector/pages/{page.Id}/requirements/{B1}?expectedVersion={version}");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("IT KBN", (await Ok(await api.GetAsync($"/api/v1/projector/spaces/{space}/waiting")))["parties"]![0]!["party"]!.GetValue<string>());
        Assert.Contains("portal.ssl", (await Ok(await api.GetAsync($"/api/v1/projector/spaces/{space}/overview?filter=all")))["keys"]!.ToJsonString());
        var copy = await api.PostAsJsonAsync($"/api/v1/projector/pages/{page.Id}/deployments", new { spaceId = space, title = "Kostelec" });
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var copyId = (await copy.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>())!["id"]!.GetValue<string>();

        Assert.Equal(version + 5, removed["version"]!.GetValue<int>());
        Assert.Equal("api", await f.Site.ScalarAsync("""SELECT "Channel" FROM "PageRevision" WHERE "PageId"=@page AND "ActiveTo" IS NULL""", ("page", Guid.Parse(page.Id))));
        Assert.Equal(["projector.catalog_set:ok:", $"requirement.update:ok:{version + 1}", $"requirement.update_batch:ok:{version + 2}",
            $"requirement.insert:ok:{version + 3}", $"requirement.move:ok:{version + 4}", $"requirement.remove:ok:{version + 5}",
            "requirement.remove:version_conflict:", "projector.deployment_create:ok:1"], await ProjectorMcpToolTests.AuditAsync(f, tokenId));
        Assert.Equal(Guid.Parse(copyId), await f.Site.ScalarAsync("""SELECT "PageId" FROM "ApiActivity" WHERE "TokenId"=@token AND "Operation"='projector.deployment_create'""",
            ("token", Guid.Parse(tokenId))));

        // A read token reads, and every write is 403 scope_missing, audited.
        var (readId, readSecret) = await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read"], ["rest"]);
        using var reader = Bearer(f, readSecret);
        await Ok(await reader.GetAsync($"/api/v1/projector/pages/{page.Id}/requirements"));
        var refused = await reader.PatchAsJsonAsync($"/api/v1/projector/pages/{page.Id}/requirements/{B1}", new { fields = new { state = "done" }, expectedVersion = version + 5 });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(["requirement.update:scope_missing:"], await ProjectorMcpToolTests.AuditAsync(f, readId));
    }

    internal static HttpClient Bearer(McpSite f, string secret)
    {
        var client = f.Site.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    private static async Task<System.Text.Json.Nodes.JsonNode> Ok(HttpResponseMessage response) => await AtlasHttpSite.Json(response, HttpStatusCode.OK);

    private static string Route(RouteEndpoint endpoint) => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single() + " " + endpoint.RoutePattern.RawText;
}
