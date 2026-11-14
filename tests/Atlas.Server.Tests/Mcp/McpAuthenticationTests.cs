using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

/// <summary>The /mcp transport (MCP plan C1): who reaches it, the module gate and the origin guard. Rules of the tools are in McpReadToolTests.</summary>
public sealed class McpAuthenticationTests
{
    // The SDK cancels its five-second discovery probe and then falls back to the initialize handshake.
    // TestServer must surface that as cancellation, even when Atlas catches RequestAborted and returns 499.
    [PostgreSqlFact]
    public async Task Cancelled_discovery_probe_falls_back_to_initialize_on_the_real_server()
    {
        var probes = 0;
        var fallbackRequests = 0;
        await using var f = await McpSite.CreateAsync(services: services =>
            services.Configure<HttpServerTransportOptions>(options => options.ConfigureSessionOptions = (context, _, ct) =>
            {
                if (context.Request.Headers["MCP-Protocol-Version"] == "2026-07-28")
                {
                    Interlocked.Increment(ref probes);
                    return Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                Interlocked.Increment(ref fallbackRequests);
                return Task.CompletedTask;
            }));
        var client = await f.ClientAsync(await f.AdminTokenAsync("read"));
        Assert.Contains(await client.ListToolsAsync(), tool => tool.Name == "atlas_get_page");
        Assert.Equal(1, probes);
        Assert.True(fallbackRequests > 0);
    }

    [PostgreSqlFact]
    public async Task Uncancelled_499_remains_an_http_failure_without_discovery_fallback()
    {
        await using var f = await McpSite.CreateAsync(services: services =>
            services.Configure<HttpServerTransportOptions>(options => options.ConfigureSessionOptions = (context, _, ct) =>
            {
                Assert.False(ct.IsCancellationRequested);
                context.Response.StatusCode = 499;
                return Task.CompletedTask;
            }));
        var secret = await f.AdminTokenAsync("read");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => f.ClientAsync(secret));
        Assert.Equal((HttpStatusCode)499, error.StatusCode);
    }

    [PostgreSqlFact]
    public async Task Cancelled_discovery_body_read_falls_back_to_initialize()
    {
        await using var f = await McpSite.CreateAsync();
        var body = new DeferredDiscoveryBody();
        var client = await f.ClientAsync(await f.AdminTokenAsync("read"), responseHandler: body);
        Assert.Contains(await client.ListToolsAsync(), tool => tool.Name == "atlas_get_page");
        Assert.Equal(1, body.DeferredResponses);
    }

    // Simulate a client body read scheduled after the probe deadline. Disposing the actual TestServer stream
    // aborts it; its next read throws IOException instead of cancellation, just like CheckAborted under load.
    private sealed class DeferredDiscoveryBody : DelegatingHandler
    {
        public int DeferredResponses { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.Headers.TryGetValues("MCP-Protocol-Version", out var versions) && versions.Contains("2026-07-28"))
            {
                DeferredResponses++;
                var original = response.Content;
                response.Content = new StreamContent(new DeferredRead(await original.ReadAsStreamAsync(ct), original));
                foreach (var header in original.Headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return response;
        }
    }

    private sealed class DeferredRead(Stream inner, HttpContent owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { inner.Dispose(); }
            return await inner.ReadAsync(buffer, ct);
        }
        protected override void Dispose(bool disposing) { if (disposing) owner.Dispose(); base.Dispose(disposing); }
    }
    [Fact]
    public async Task Uncancelled_body_errors_and_eof_are_preserved()
    {
        var failure = new IOException("Uncancelled transport failure");
        using var failedContent = new StreamContent(new FailedRead(failure));
        using var failed = new McpSite.CancellationReadStream(await failedContent.ReadAsStreamAsync(), failedContent, CancellationToken.None);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => failed.ReadAsync(new byte[1]).AsTask()));
        using var emptyContent = new StreamContent(new MemoryStream());
        using var empty = new McpSite.CancellationReadStream(await emptyContent.ReadAsStreamAsync(), emptyContent, CancellationToken.None);
        Assert.Equal(0, await empty.ReadAsync(new byte[1]));
    }

    private sealed class FailedRead(IOException failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => ValueTask.FromException<int>(failure);
    }
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Only_live_tokens_with_the_mcp_channel_reach_mcp()
    {
        await using var f = await McpSite.CreateAsync();
        await f.SetLevelAsync(f.AdminId, "write");
        var rest = await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read"], ["rest"]);
        var revoked = await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read"], ["mcp"]);
        var expired = await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read"], ["mcp"]);
        var stale = await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read"], ["mcp"]);
        var live = await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read"], ["rest", "mcp"]);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(f.Admin, HttpMethod.Delete, $"/api/profile/api-tokens/{revoked.Id}")).StatusCode);
        Assert.Equal(1, await f.Site.ExecuteAsync("UPDATE \"PersonalApiToken\" SET \"ExpiresAt\" = \"ActiveFrom\" + interval '1 microsecond' WHERE \"Id\"=@id",
            ("id", Guid.Parse(expired.Id))));
        Assert.Equal(1, await f.Site.ExecuteAsync("UPDATE \"PersonalApiToken\" SET \"SecurityStamp\" = 'rotated' WHERE \"Id\"=@id", ("id", Guid.Parse(stale.Id))));

        foreach (var secret in new[] { rest.Secret, revoked.Secret, expired.Secret, stale.Secret, "atl_" + new string('x', 40) })
        {
            var refused = await f.PostAsync(secret);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("Bearer realm=\"Atlas\"", refused.Headers.WwwAuthenticate.ToString());
        }
        // A signed-in browser session is never an MCP credential.
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.PostAsync(client: f.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.PostAsync()).StatusCode);

        var client = await f.ClientAsync(live.Secret);
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, tool => tool.Name == "atlas_get_page");
        // The same token keeps working on REST: the channels are independent.
        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", live.Secret);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/spaces")).StatusCode);
        // Stateless (D9): no event stream and no session to delete.
        using var stream = f.Site.Client();
        stream.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", live.Secret);
        stream.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await stream.GetAsync("/mcp")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Disabled_module_answers_404_and_rest_is_unchanged()
    {
        // Off after installation and upgrade (O2): nothing reaches /mcp until a WikiAdmin enables it.
        // Isolated: it switches the MCP module (one row, McpSetting, read by every /mcp request of the installation) off and on.
        await using var f = await McpSite.CreateAsync(enabled: false, isolated: true);
        await f.SetLevelAsync(f.AdminId, "write");
        var token = await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read"], ["rest", "mcp"]);
        var profile = await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/profile/api-tokens"), HttpStatusCode.OK);
        Assert.False(profile["mcpEnabled"]!.GetValue<bool>());
        Assert.Null(profile["mcpUrl"]);
        var mcpOnly = await McpSite.IssueAsync(f.Admin, f.Site.Password, ["read"], ["mcp"]);
        profile = await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/profile/api-tokens"), HttpStatusCode.OK);
        Assert.False(profile["tokens"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == mcpOnly.Id)!["usable"]!.GetValue<bool>());

        var disabled = await f.PostAsync(token.Secret);
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.Equal("application/problem+json", disabled.Content.Headers.ContentType?.MediaType);
        Assert.Equal("mcp_disabled", (await disabled.Content.ReadFromJsonAsync<JsonNode>())!["code"]!.GetValue<string>());
        // Even without a token: the gate answers before authentication, and never the SPA's index.html.
        Assert.Equal(HttpStatusCode.NotFound, (await f.PostAsync()).StatusCode);
        using var api = f.Site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Secret);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/spaces")).StatusCode);

        var enabled = await f.SetModuleAsync(true);
        Assert.Equal("https://localhost/mcp", enabled["url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await f.PostAsync(token.Secret)).StatusCode);
        profile = await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/profile/api-tokens"), HttpStatusCode.OK);
        Assert.True(profile["mcpEnabled"]!.GetValue<bool>());
        Assert.Equal("https://localhost/mcp", profile["mcpUrl"]!.GetValue<string>());
        Assert.True(profile["tokens"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == mcpOnly.Id)!["usable"]!.GetValue<bool>());
        Assert.True((await AtlasHttpSite.Json(await f.Admin.GetAsync("/api/auth/me"), HttpStatusCode.OK))["features"]!["mcp"]!.GetValue<bool>());

        await f.SetModuleAsync(false);
        Assert.Equal(HttpStatusCode.NotFound, (await f.PostAsync(token.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/spaces")).StatusCode);
        // Every switch is an interval of the append-only history and one audit event of the administrator.
        Assert.Equal(2L, await f.Site.ScalarAsync("SELECT count(*) FROM \"McpSetting\""));
        Assert.Equal(1L, await f.Site.ScalarAsync("SELECT count(*) FROM \"McpSetting\" WHERE \"ActiveTo\" IS NULL AND NOT \"Enabled\""));
        Assert.Equal("mcp.enabled,mcp.disabled", await f.Site.ScalarAsync(
            "SELECT string_agg(\"Action\", ',' ORDER BY \"Id\") FROM \"AccessAuditEvent\" WHERE \"McpSettingId\" IS NOT NULL AND \"ActorId\"=@admin",
            ("admin", Guid.Parse(f.AdminId))));

        // Only a WikiAdmin sees or switches the module.
        var (reader, _) = await f.UserAsync("module-reader", level: null);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AtlasHttpSite.SendAsync(reader, HttpMethod.Put, "/api/admin/mcp", new { enabled = true, expectedVersion = (string?)null })).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Foreign_origin_is_refused()
    {
        await using var f = await McpSite.CreateAsync();
        var secret = await f.AdminTokenAsync("read");
        var foreign = await f.PostAsync(secret, origin: "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal("origin_forbidden", (await foreign.Content.ReadFromJsonAsync<JsonNode>())!["code"]!.GetValue<string>());
        // DNS rebinding makes Origin and Host agree; without a configured public origin every browser origin is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await f.PostAsync(secret, origin: "https://localhost")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.PostAsync(secret)).StatusCode);
    }
}
