using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.ContentExport.Queries;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PersonalApi;

/// <summary>Token rights (MCP plan T1, 3.6): scopes read/write within the owner's current level, channels rest/mcp.</summary>
public sealed class ApiTokenRightsTests : IClassFixture<ApiTokenRightsTests.TokenSite>
{
    private readonly TokenSite m_site;

    public ApiTokenRightsTests(TokenSite site)
    {
        m_site = site;
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Effective_scopes_are_the_token_scopes_within_the_current_level()
    {
        await using var site = await RightsSite.CreateAsync();
        await site.SetLevelAsync("write");
        using var api = await site.TokenClientAsync(["read", "write"]);
        var spaceId = await site.FirstSpaceAsync(api);
        var page = await AtlasHttpSite.Json(await api.PostAsJsonAsync("/api/v1/pages",
            new { spaceId, parentId = (string?)null, title = "Ceiling", markdown = "Ceiling" }), HttpStatusCode.Created);
        var pageId = page["id"]!.GetValue<string>();

        await site.SetLevelAsync("read");
        // The very next request of the same token, without issuing it again.
        var refused = await api.PostAsJsonAsync($"/api/v1/pages/{pageId}/publish", new { expectedVersion = page["version"]!.GetValue<int>() });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = (await refused.Content.ReadFromJsonAsync<JsonNode>())!;
        Assert.Equal("scope_missing", problem["code"]!.GetValue<string>());
        Assert.Equal("Správce wiki vám snížil oprávnění API; token teď smí jen čtení.", problem["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PutAsJsonAsync($"/api/v1/pages/{pageId}",
            new { title = "Ceiling", markdown = "Changed", expectedVersion = page["version"]!.GetValue<int>() })).StatusCode);
        await AtlasHttpSite.Json(await api.GetAsync($"/api/v1/pages/{pageId}"), HttpStatusCode.OK);
    }

    /// <summary>3.6: only read and write exist; the former scopes and levels are refused, never mapped to write.</summary>
    [Fact]
    public void Only_read_and_write_remain()
    {
        Assert.Equal(["read", "write"], ApiScopes.All);
        Assert.Equal(["none", "read", "write"], ApiLevels.All);
        foreach (var former in new[] { "draft", "publish", "direct" })
        {
            Assert.False(ApiScopes.IsValid(["read", former]));
            Assert.False(ApiLevels.IsKnown(former));
        }
        // Guard: no scope constant beyond the two, so a "Draft" scope cannot come back unnoticed.
        Assert.Equal(["Read", "Write"], typeof(ApiScopes).GetFields().Where(field => field.IsLiteral).Select(field => field.Name));
        Assert.Equal(["None", "Read", "Write"], typeof(ApiLevels).GetFields().Where(field => field.IsLiteral).Select(field => field.Name));
    }

    [PostgreSqlFact]
    public async Task Level_none_rejects_and_closes_tokens_lowering_keeps_them()
    {
        await using var f = await PersonalApiFixture.CreateAsync();
        await f.SetLevel("write");
        var token = await f.Issue(["read", "write"]);

        var lowered = await f.SetLevel("read");
        Assert.Equal("read", lowered.Level);
        Assert.True(lowered.CanIssueTokens);
        var profile = await f.As<ApiTokenProfile>(f.Owner, new ListApiTokens());
        var kept = Assert.Single(profile.Tokens);
        Assert.Null(kept.ActiveTo);
        Assert.Equal(["read"], kept.EffectiveScopes);
        var principal = await f.Credentials.AuthenticateAsync(token.Secret, ApiChannels.Rest, default);
        Assert.NotNull(principal);
        Assert.Equal(["read"], principal.Scopes);
        Assert.Equal(["read", "write"], principal.GrantedScopes);

        var none = await f.SetLevel("none");
        Assert.False(none.CanIssueTokens);
        Assert.Null(await f.Credentials.AuthenticateAsync(token.Secret, ApiChannels.Rest, default));
        Assert.NotNull(Assert.Single((await f.As<ApiTokenProfile>(f.Owner, new ListApiTokens())).Tokens).ActiveTo);
        // Raising the level again never revives the closed token.
        await f.SetLevel("write");
        Assert.Null(await f.Credentials.AuthenticateAsync(token.Secret, ApiChannels.Rest, default));
    }

    [PostgreSqlFact]
    public async Task Issuing_respects_the_ceiling_and_requires_a_channel()
    {
        await using var f = await PersonalApiFixture.CreateAsync();
        // A fresh installation has nothing to review.
        Assert.False((await f.As<ApiTokensSummary>(f.Admin, new GetApiTokensSummary())).LevelsReview);
        await f.SetLevel("read");
        Assert.Equal(403, await Status(() => f.Issue(["read", "write"])));
        Assert.Equal(400, await Status(() => f.Issue(["read"], [])));
        Assert.Equal(400, await Status(() => f.Issue(["read"], ["rest", "sse"])));
        Assert.Equal(400, await Status(() => f.Issue(["write"])));
        // A client of the former scopes or levels is refused: asking for draft must never yield write.
        foreach (var former in new[] { "draft", "publish", "direct" })
        {
            Assert.Equal(400, await Status(() => f.Issue(["read", former])));
            Assert.Equal(400, await Status(() => f.SetLevel(former)));
        }
        await f.SetLevel("write");

        var rest = await f.Issue(["write", "read"]);
        Assert.Equal(["read", "write"], rest.Token.Scopes);
        Assert.Equal(["rest"], rest.Token.Channels);
        Assert.Equal(["read", "write"], rest.Token.EffectiveScopes);
        Assert.True(rest.Token.Usable);
        // MCP can be chosen before the module exists; such a token is listed as unusable until an administrator enables it.
        var mcp = await f.Issue(["read"], ["mcp"]);
        Assert.Equal(["mcp"], mcp.Token.Channels);
        Assert.False(mcp.Token.Usable);
        var profile = await f.As<ApiTokenProfile>(f.Owner, new ListApiTokens());
        Assert.Equal("write", profile.Level);
        Assert.False(profile.McpEnabled);
        Assert.Null(profile.McpUrl);
        var admin = await f.As<IReadOnlyList<AdminApiToken>>(f.Admin, new ListAdminApiTokens());
        Assert.Equal(["mcp"], Assert.Single(admin, x => x.Id == mcp.Token.Id).Channels);
    }

    [PostgreSqlTheory]
    [InlineData("POST", "/api/v1/pages")]
    [InlineData("PUT", "/api/v1/pages/{id}")]
    [InlineData("PUT", "/api/v1/pages/{id}/title")]
    [InlineData("POST", "/api/v1/pages/{id}/restore")]
    [InlineData("POST", "/api/v1/pages/{id}/versions")]
    [InlineData("POST", "/api/v1/pages/{id}/attachments")]
    [InlineData("PUT", "/api/v1/pages/{id}/favorite")]
    [InlineData("POST", "/api/v1/pages/{id}/visit")]
    [InlineData("POST", "/api/v1/pages/{id}/publish")]
    [InlineData("POST", "/api/v1/pages/{id}/move")]
    [InlineData("POST", "/api/v1/pages/{id}/move-space")]
    [InlineData("POST", "/api/v1/pages/{id}/trash")]
    [InlineData("POST", "/api/v1/pages/{id}/untrash")]
    [InlineData("POST", "/api/v1/objects/{id}/comments")]
    public async Task Write_scope_reaches_every_write_route_and_the_owners_rights_decide(string method, string route)
    {
        var client = await m_site.WriteClientAsync();
        // A write token reaches the handler, which finds no such page or space (or misses the probe's target space): publication and
        // the tree included (3.6).
        var status = (await client.SendAsync(Probe(method, route.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal)))).StatusCode;
        Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, status.ToString());
    }

    /// <summary>A read token is refused on every /api/v1 mutation by the write policy, before any handler runs.</summary>
    [PostgreSqlFact]
    public async Task Read_scope_is_refused_on_every_write_route()
    {
        var client = await m_site.ReadClientAsync();
        foreach (var endpoint in await MutationsAsync())
        {
            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();
            var route = string.Join('/', endpoint.RoutePattern.RawText!.Split('/').Select(segment => segment switch
            {
                _ when segment.Contains(":guid", StringComparison.Ordinal) => Guid.NewGuid().ToString(),
                _ when segment.Contains(":long", StringComparison.Ordinal) => "1",
                ['{', ..] => "probe",
                _ => segment
            }));
            var response = await client.SendAsync(Probe(method, route));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {route}: {response.StatusCode}");
            Assert.Equal("scope_missing", (await response.Content.ReadFromJsonAsync<JsonNode>())!["code"]!.GetValue<string>());
        }
    }

    /// <summary>A request the route accepts, so the status is authorization's or the handler's, never routing's.</summary>
    private static HttpRequestMessage Probe(string method, string route)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (route.EndsWith("/attachments", StringComparison.Ordinal) || route.EndsWith("/photo", StringComparison.Ordinal))
        {
            // Any other content type is answered 404 by routing before authorization runs.
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent("probe"u8.ToArray()), "file", "probe.txt");
            request.Content = form;
        }
        else
        {
            request.Content = JsonContent.Create(new
            {
                spaceId = Guid.NewGuid(), parentId = (string?)null, title = "Probe", markdown = "Probe",
                expectedVersion = 1, revisionId = Guid.NewGuid(), isFavorite = true
            });
        }
        return request;
    }

    /// <summary>Reads that take their (long) input as a POST body; they change nothing and keep the read policy.</summary>
    private static readonly HashSet<string> PostedReads = ["/api/v1/contacts/match", "/api/v1/contacts/live", "/api/v1/recognize"];

    private async Task<RouteEndpoint[]> MutationsAsync()
    {
        await m_site.WriteClientAsync();
        return m_site.Site!.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(m => m is not ("GET" or "HEAD")) == true)
            .Where(e => !PostedReads.Contains(e.RoutePattern.RawText!))
            .ToArray();
    }

    /// <summary>The /api/v1 groups default to the read policy, so a new mutation that names no scope would be open to read tokens.</summary>
    [PostgreSqlFact]
    public async Task Every_api_v1_mutation_names_the_write_policy()
    {
        var mutations = await MutationsAsync();
        // Listed, not counted: a new mutation must be named here and so reviewed for its policy.
        Assert.Equal(new[] {
            "POST /api/v1/pages/{id:guid}/tags/changes",
            "POST /api/v1/pages",
            "POST /api/v1/pages/{id:guid}/move",
            "POST /api/v1/pages/{id:guid}/move-space",
            "POST /api/v1/pages/{id:guid}/publish",
            "POST /api/v1/pages/{id:guid}/restore",
            // 3.6-C: „Uložit verzi“ names the working revision, a draft write like the restore.
            "POST /api/v1/pages/{id:guid}/versions",
            // 3.6-H: one revision's change undone as a whole (an agent's write), a live write of the page.
            "POST /api/v1/pages/{id:guid}/history/{revisionId:guid}/undo",
            "POST /api/v1/pages/{id:guid}/trash",
            "POST /api/v1/pages/{id:guid}/untrash",
            "POST /api/v1/pages/{id:guid}/visit",
            "POST /api/v1/pages/{pageId:guid}/attachments",
            "PATCH /api/v1/pages/{id:guid}/embeds/{attachmentId:guid}",
            "PUT /api/v1/pages/{id:guid}",
            "PUT /api/v1/pages/{id:guid}/decoration",
            "PUT /api/v1/pages/{id:guid}/favorite",
            "PUT /api/v1/pages/{id:guid}/publication",
            "PUT /api/v1/pages/{id:guid}/tags",
            "PUT /api/v1/pages/{id:guid}/title",
            "POST /api/v1/spaces/{spaceId:guid}/tree/references",
            "DELETE /api/v1/tree/references/{id:guid}",
            "POST /api/v1/tree/references/{id:guid}/move",
            // 4.0: „Řadit abecedně“ pins a tree level to its titles (or unpins it).
            "POST /api/v1/spaces/{spaceId:guid}/tree/sort",
            "POST /api/v1/objects/{objectId:guid}/restore",
            "POST /api/v1/spaces/{spaceId:guid}/boards",
            "DELETE /api/v1/boards/{boardId:guid}",
            "POST /api/v1/boards/{boardId:guid}/operations",
            "POST /api/v1/boards/{boardId:guid}/move",
            "POST /api/v1/boards/{boardId:guid}/untrash",
            "POST /api/v1/notifications/{id:guid}/read",
            "POST /api/v1/notifications/read-all",
            // The object path (notes, contacts), which develop added without naming it here.
            "POST /api/v1/objects",
            "POST /api/v1/objects/{objectId:guid}/changes",
            "DELETE /api/v1/objects/{objectId:guid}",
            // 3.1 contacts: resolve may create a wiki contact, merge trashes one.
            "POST /api/v1/contacts/resolve",
            "POST /api/v1/contacts/merge",
            // R3: a wiki admin links an account to a contact (merging the account's own one) or dismisses the pair (giving the account its own contact).
            "POST /api/v1/contacts/accounts",
            // P15: a link-only change of a page (published when no draft waits) and one mention decided.
            "POST /api/v1/pages/{pageId:guid}/link",
            "POST /api/v1/pages/{pageId:guid}/references",
            // 3.3: background jobs (run, retry, cancel, pause).
            "POST /api/v1/jobs/{jobId}/runs",
            "POST /api/v1/jobs/{jobId}/runs/{runId:long}/retry",
            "POST /api/v1/jobs/{jobId}/runs/{runId:long}/cancel",
            "PUT /api/v1/jobs/{jobId}/paused",
            // 3.5 G: a contact's logo or photo.
            "PUT /api/v1/contacts/{id:guid}/photo",
            // 5.1 Mapa znalostí: a rating (or its removal), a topic merged into another, a topic's broader or related topics.
            "PUT /api/v1/contacts/{id:guid}/skills",
            "POST /api/v1/skills/topics/{id:guid}/merge",
            "PUT /api/v1/skills/topics/{id:guid}/parents",
            // 5.2 Ověřený obsah: verify / steward / interval of a page, „Nahlásit neaktuální“, the bulk actions of Stav obsahu.
            "PUT /api/v1/pages/{id:guid}/verification",
            "POST /api/v1/pages/{id:guid}/verification/report",
            "POST /api/v1/spaces/{spaceId:guid}/content-health/bulk",
            // 5.2: discussions, the page chat's channel: an agent answers with a comment.
            "POST /api/v1/objects/{objectId:guid}/comments",
        }.Order(StringComparer.Ordinal), mutations.Select(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single() + " " + e.RoutePattern.RawText).Order(StringComparer.Ordinal));
        Assert.All(mutations, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            data => data.Policy == PersonalApiEndpoints.WritePolicy));
        // Every write attempt is audited (T3), refused ones included.
        Assert.All(mutations, endpoint => Assert.NotNull(endpoint.Metadata.GetMetadata<ApiWriteAudit>()));
    }

    /// <summary>Every route of a personal API channel (/api/v1, and /mcp once ApiChannels.ForPath names it) is rate limited. The limiter
    /// runs before authorization, so this budget is also what bounds refused requests and their audit rows.</summary>
    [PostgreSqlFact]
    public async Task Every_personal_api_channel_route_is_rate_limited()
    {
        await m_site.WriteClientAsync();
        var routes = m_site.Site!.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && ApiChannels.ForPath(new PathString(raw.Split('{')[0].TrimEnd('/'))) is not null)
            .ToArray();
        Assert.NotEmpty(routes);
        Assert.All(routes, endpoint =>
        {
            Assert.True(endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName is { Length: > 0 }, endpoint.RoutePattern.RawText);
            Assert.Null(endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>());
        });
    }

    [PostgreSqlFact]
    public async Task Channel_rest_is_required_for_api_v1_including_export_revalidation()
    {
        await using var f = await PersonalApiFixture.CreateAsync();
        await f.SetLevel("write");
        var mcp = await f.Issue(["read"], ["mcp"]);
        var rest = await f.Issue(["read"], ["rest"]);
        Assert.Null(await f.Credentials.AuthenticateAsync(mcp.Secret, ApiChannels.Rest, default));
        Assert.Equal(ApiChannels.Mcp, (await f.Credentials.AuthenticateAsync(mcp.Secret, ApiChannels.Mcp, default))!.Channel);
        Assert.Null(await f.Credentials.AuthenticateAsync(rest.Secret, ApiChannels.Mcp, default));

        // Long downloads revalidate the token in SQL, outside authentication: the channel counts there too.
        Assert.Equal(401, await Status(() => f.As<DownloadLease>(f.Owner, Export(mcp))));
        Assert.Equal(401, await Status(() => ReadLease(f, mcp)));
        await f.As<DownloadLease>(f.Owner, Export(rest));
        await ReadLease(f, rest);

        // So does the level, even while the token itself stays open (the permission row changes without closing tokens).
        await f.Execute("""
            UPDATE "UserApiPermission" SET "ActiveTo"=clock_timestamp() WHERE "UserId"=:user AND "ActiveTo" IS NULL;
            INSERT INTO "UserApiPermission"("Id","UserId","CanIssueTokens","Level","ChangedById","ActiveFrom")
              VALUES (gen_random_uuid(),:user,false,'none',:user,clock_timestamp());
            """, ("user", f.Owner.Id));
        Assert.Equal(401, await Status(() => f.As<DownloadLease>(f.Owner, Export(rest))));
        Assert.Equal(401, await Status(() => ReadLease(f, rest)));

        RevalidateContentExport Export(CreatedApiToken token) => new(f.SpaceId, [], token.Token.Id, true);
    }

    private static async Task ReadLease(PersonalApiFixture f, CreatedApiToken token)
    {
        using var session = f.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        var owner = await session.GetAsync<UserAccount>(f.Owner.Id);
        await DownloadCredentials.ReadAsync(session, owner, token.Token.Id, default);
    }

    private static async Task<int> Status(Func<Task> action) => (await Assert.ThrowsAsync<ApiException>(action)).Status;

    /// <summary>One HTTP site whose administrator holds level write, a read+write and a read token; created on first use, so the class
    /// skips cleanly without a database.</summary>
    public sealed class TokenSite : IAsyncLifetime
    {
        private readonly SemaphoreSlim m_gate = new(1, 1);
        private RightsSite? m_rights;
        private HttpClient? m_write;
        private HttpClient? m_read;

        internal AtlasHttpSite? Site => m_rights?.Site;

        public async Task<HttpClient> WriteClientAsync() => (await ClientsAsync()).Write;

        public async Task<HttpClient> ReadClientAsync() => (await ClientsAsync()).Read;

        private async Task<(HttpClient Write, HttpClient Read)> ClientsAsync()
        {
            await m_gate.WaitAsync();
            try
            {
                if (m_write is null || m_read is null)
                {
                    m_rights = await RightsSite.CreateAsync();
                    await m_rights.SetLevelAsync("write");
                    m_write = await m_rights.TokenClientAsync(["read", "write"]);
                    m_read = await m_rights.TokenClientAsync(["read"]);
                }
                return (m_write, m_read);
            }
            finally
            {
                m_gate.Release();
            }
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            if (m_rights is not null)
            {
                await m_rights.DisposeAsync();
            }
            m_gate.Dispose();
        }
    }
}

/// <summary>An HTTP site whose set-up administrator owns the tokens: WikiAdmin, so content rights never decide a status here.</summary>
internal sealed class RightsSite : IAsyncDisposable
{
    private readonly List<HttpClient> m_clients = [];
    private string? m_version;

    private RightsSite(AtlasHttpSite site, HttpClient owner, string ownerId)
    {
        Site = site;
        Owner = owner;
        OwnerId = ownerId;
    }

    public AtlasHttpSite Site { get; }
    public HttpClient Owner { get; }
    public string OwnerId { get; }

    /// <param name="isolated">A site of its own, for a test that changes what the whole installation shares (the AI profile, the index).</param>
    public static async Task<RightsSite> CreateAsync(bool isolated = false)
    {
        var site = isolated
            ? await AtlasHttpSite.CreateIsolatedAsync("atlas_api_rights_") /* own database: the caller changes the AI profile, switch and index, which the whole installation shares */
            : await AtlasHttpSite.CreateAsync("atlas_api_rights_");
        var owner = site.Client();
        var setup = await site.CompleteSetupAsync(owner);
        return new RightsSite(site, owner, setup["id"]!.GetValue<string>());
    }

    public async Task SetLevelAsync(string level)
    {
        var permission = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(Owner, HttpMethod.Put, $"/api/admin/users/{OwnerId}/api-access",
            new { level, expectedVersion = m_version }), HttpStatusCode.OK);
        Assert.Equal(level, permission["level"]!.GetValue<string>());
        m_version = permission["version"]?.GetValue<string>();
    }

    public async Task<HttpClient> TokenClientAsync(string[] scopes)
    {
        await AtlasHttpSite.ReauthAsync(Owner, Site.Password);
        var token = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(Owner, HttpMethod.Post, "/api/profile/api-tokens",
            new { name = "Rights " + string.Join('+', scopes), scopes, expiresInDays = 1 }), HttpStatusCode.Created);
        var client = Site.Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["secret"]!.GetValue<string>());
        m_clients.Add(client);
        return client;
    }

    private string? m_space;

    /// <summary>The owner's own space (a WikiAdmin lists every space of the installation, so "the first" is no one's): made once.</summary>
    public async Task<string> FirstSpaceAsync(HttpClient api) => m_space ??= await AtlasHttpSite.OwnSpaceAsync(Owner);

    public async ValueTask DisposeAsync()
    {
        foreach (var client in m_clients)
        {
            client.Dispose();
        }
        Owner.Dispose();
        await Site.DisposeAsync();
    }
}
