using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Mcp;

/// <summary>
/// An HTTP site for the MCP module with the C# SDK client over the test server's HttpClient (no model, no Claude Code). The set-up
/// administrator is a WikiAdmin; further accounts are invited, activated and signed in like real ones.
/// </summary>
internal sealed class McpSite : IAsyncDisposable
{
    private readonly List<IDisposable> m_disposables = [];
    private readonly List<IAsyncDisposable> m_clients = [];
    private readonly Dictionary<string, string?> m_levelVersions = [];
    private readonly Dictionary<string, string> m_logins = [];

    private McpSite(AtlasHttpSite site, HttpClient admin, string adminId)
    {
        Site = site;
        Admin = admin;
        AdminId = adminId;
    }

    public AtlasHttpSite Site { get; }
    public HttpClient Admin { get; }
    public string AdminId { get; }

    /// <param name="kestrel">Serve over a real Kestrel on a loopback port (request body limits); TestServer otherwise.</param>
    /// <param name="sharedAs">A site of its own under this name (and the settings), shared by every call with the name; the first
    /// call's services build it.</param>
    public static async Task<McpSite> CreateAsync(bool enabled = true, Action<IServiceCollection>? services = null,
        Action<IDictionary<string, string?>>? settings = null, bool kestrel = false, bool isolated = false, string? sharedAs = null)
    {
        // Settings are constants of the tests, so they share a site per settings; a fake service (or isolated) gets a private one.
        // Kestrel isolated: the loopback address and body limits belong to the one site; shared, they would change it for every test.
        var site = isolated || kestrel
            ? await AtlasHttpSite.CreateIsolatedAsync("atlas_mcp_", settings, services)
            : await AtlasHttpSite.CreateAsync("atlas_mcp_", settings, services, shared: true, sharedAs: sharedAs);
        if (kestrel)
        {
            site.UseLoopbackKestrel();
        }
        var admin = site.Client();
        var setup = await site.CompleteSetupAsync(admin);
        var result = new McpSite(site, admin, setup["id"]!.GetValue<string>());
        if (enabled)
        {
            await result.SetModuleAsync(true);
        }
        return result;
    }

    /// <summary>Switches the module. The switch is one row of the installation: when it already has the wanted state (another test
    /// of a shared site switched it on) nothing is written.</summary>
    public async Task<JsonNode> SetModuleAsync(bool enabled)
    {
        // Two tests switching it on at the same time: the loser reads the winner's state on the next round.
        for (var attempt = 0; ; attempt++)
        {
            var current = await AtlasHttpSite.Json(await Admin.GetAsync("/api/admin/mcp"), HttpStatusCode.OK);
            if (current["enabled"]!.GetValue<bool>() == enabled)
            {
                // The test that saved it forgets the cached state only after its commit: do not trust a read in that gap.
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<Atlas.Server.Features.Mcp.McpModuleState>(Site.Services).Invalidate();
                return current;
            }
            var response = await AtlasHttpSite.SendAsync(Admin, HttpMethod.Put, "/api/admin/mcp",
                new { enabled, expectedVersion = current["version"]?.GetValue<string>() });
            if (response.StatusCode == HttpStatusCode.Conflict && attempt < 3)
            {
                continue;
            }
            var saved = await AtlasHttpSite.Json(response, HttpStatusCode.OK);
            Assert.Equal(enabled, saved["enabled"]!.GetValue<bool>());
            // A request of another test may have read the old state while this save committed and cached it afterwards.
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Atlas.Server.Features.Mcp.McpModuleState>(Site.Services).Invalidate();
            return saved;
        }
    }

    public async Task SetLevelAsync(string userId, string level)
    {
        var permission = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(Admin, HttpMethod.Put, $"/api/admin/users/{userId}/api-access",
            new { level, expectedVersion = m_levelVersions.GetValueOrDefault(userId) }), HttpStatusCode.OK);
        m_levelVersions[userId] = permission["version"]?.GetValue<string>();
    }

    /// <summary>A token of the signed-in owner, after the fresh sign-in issuing one needs (SEC-A-2); returns its id and secret.</summary>
    public static async Task<(string Id, string Secret)> IssueAsync(HttpClient owner, string password, string[] scopes, string[] channels, string name = "Claude Code")
    {
        await AtlasHttpSite.ReauthAsync(owner, password);
        var token = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(owner, HttpMethod.Post, "/api/profile/api-tokens",
            new { name, scopes, channels, expiresInDays = 1 }), HttpStatusCode.Created);
        return (token["token"]!["id"]!.GetValue<string>(), token["secret"]!.GetValue<string>());
    }

    /// <summary>The administrator's token with level publish and the given scopes on the channel mcp.</summary>
    public async Task<string> AdminTokenAsync(params string[] scopes)
    {
        if (!m_levelVersions.ContainsKey(AdminId))
        {
            await SetLevelAsync(AdminId, "write");
        }
        return (await IssueAsync(Admin, Site.Password, scopes.Length == 0 ? ["read"] : scopes, ["mcp"])).Secret;
    }

    /// <summary>An invited, activated and signed-in account; returns its cookie client and id.</summary>
    public async Task<(HttpClient Client, string Id)> UserAsync(string name, string? level = "read")
    {
        var username = TestLogin.For(name);
        // Unique logins on a shared site; the test signs in again by this one (Login).
        var invitation = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(Admin, HttpMethod.Post, "/api/admin/users",
            new { username, displayName = name }), HttpStatusCode.Created);
        var id = invitation["user"]!["id"]!.GetValue<string>();
        m_logins[id] = username;
        m_loginsByName[name] = username;
        var client = Site.Client();
        m_disposables.Add(client);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = Site.Password })).StatusCode);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, "/api/auth/login", new { username, password = Site.Password }), HttpStatusCode.OK);
        if (level is not null)
        {
            await SetLevelAsync(id, level);
        }
        return (client, id);
    }

    /// <summary>The login of an account made by <see cref="UserAsync"/>.</summary>
    public string Login(string userId) => m_logins[userId];

    private readonly Dictionary<string, string> m_loginsByName = [];

    /// <summary>The login of the account <see cref="UserAsync"/> made under this name.</summary>
    public string LoginFor(string name) => m_loginsByName[name];

    private readonly Dictionary<string, string> m_spaceNames = [];

    /// <summary>A space with a name unique to the test (SpaceName reads it back, as the paths of the tools show it).</summary>
    public async Task<string> SpaceAsync(string name)
    {
        var unique = TestLogin.For(name);
        var id = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(Admin, HttpMethod.Post, "/api/spaces", new { name = unique }), HttpStatusCode.OK))["id"]!.GetValue<string>();
        m_spaceNames[id] = unique;
        return id;
    }

    public string SpaceName(string spaceId) => m_spaceNames[spaceId];

    private string? m_space;

    /// <summary>The test's own space (made on first use; the same one for every later call), where a shared site has no "first" one.</summary>
    public async Task<string> FirstSpaceAsync() => m_space ??= await SpaceAsync("Prostor");

    public async Task GrantAsync(string spaceId, string userId, string role)
        => Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(Admin, HttpMethod.Put, $"/api/spaces/{spaceId}/access",
            new { userId, role, expectedVersion = (string?)null })).StatusCode);

    public async Task ExcludeFromAiAsync(string spaceId)
        => await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(Admin, HttpMethod.Put, $"/api/spaces/{spaceId}/ai-settings",
            new { included = false, expectedVersion = (string?)null }), HttpStatusCode.OK);

    /// <summary>A page created (and optionally published) by the given cookie client; returns its id and version.</summary>
    public static async Task<(string Id, int Version)> PageAsync(HttpClient author, string spaceId, string title, string contentJson, string markdown,
        bool publish = true, string? parentId = null)
    {
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(author, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId, title, contentJson, markdown }), HttpStatusCode.OK);
        var id = page["id"]!.GetValue<string>();
        var version = page["version"]!.GetValue<int>();
        if (publish)
        {
            version = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(author, HttpMethod.Post, $"/api/pages/{id}/publish",
                new { expectedVersion = version }), HttpStatusCode.OK))["version"]!.GetValue<int>();
        }
        return (id, version);
    }

    public static async Task<int> SaveAsync(HttpClient author, string pageId, int expectedVersion, string title, string contentJson, string markdown)
        => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(author, HttpMethod.Put, $"/api/pages/{pageId}",
            new { title, contentJson, markdown, expectedVersion }), HttpStatusCode.OK))["version"]!.GetValue<int>();

    public async Task<McpClient> ClientAsync(string secret, DelegatingHandler? responseHandler = null)
    {
        var http = Site.CreateDefaultClient(Site.ServerAddress, responseHandler is null ? [new CanceledTestRequestHandler()] : [new CanceledTestRequestHandler(), responseHandler]);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Site.ServerAddress, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + secret }
        }, http, ownsHttpClient: true);
        var options = new McpClientOptions { ClientInfo = new Implementation { Name = "atlas-tests", Version = "1" } };
        var client = await McpClient.CreateAsync(transport, options);
        m_clients.Add(client);
        return client;
    }

    // TestServer can complete a cancelled request with Atlas's 499 response instead of surfacing cancellation.
    // Preserve the HTTP client's cancellation contract so the SDK's cancelled discovery probe can fall back.
    private sealed class CanceledTestRequestHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var original = response.Content;
                var stream = await original.ReadAsStreamAsync(cancellationToken);
                response.Content = new StreamContent(new CancellationReadStream(stream, original, cancellationToken));
                foreach (var header in original.Headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    // TestServer wraps an aborted response in IOException; the SDK otherwise treats it as a reply-less EOF.
    // Keep streaming, and only translate failures when a real client token cancelled.
    internal sealed class CancellationReadStream(Stream inner, HttpContent owner, CancellationToken requestCancellation) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            requestCancellation.ThrowIfCancellationRequested();
            try
            {
                var read = inner.Read(buffer, offset, count);
                requestCancellation.ThrowIfCancellationRequested();
                return read;
            }
            catch (IOException) when (requestCancellation.IsCancellationRequested)
            {
                requestCancellation.ThrowIfCancellationRequested();
                throw;
            }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            CheckCancellation(ct);
            try
            {
                var read = await inner.ReadAsync(buffer, ct);
                CheckCancellation(ct);
                return read;
            }
            catch (IOException) when (ct.IsCancellationRequested || requestCancellation.IsCancellationRequested)
            {
                CheckCancellation(ct);
                throw;
            }
        }
        private void CheckCancellation(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            requestCancellation.ThrowIfCancellationRequested();
        }
        protected override void Dispose(bool disposing) { if (disposing) owner.Dispose(); base.Dispose(disposing); }
    }
    /// <summary>A raw tools/list POST for the HTTP layer (status codes before MCP): optional bearer secret, cookie client and Origin.</summary>
    public async Task<HttpResponseMessage> PostAsync(string? secret = null, HttpClient? client = null, string? origin = null)
    {
        var http = client ?? Site.Client();
        if (client is null)
        {
            m_disposables.Add(http);
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (secret is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }
        return await http.SendAsync(request);
    }

    public static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    public static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    /// <summary>The items of a successful call. A tool error fails with its own code and message: its structured content has
    /// no items, and reading them anyway only reported a NullReferenceException.</summary>
    public static JsonArray Items(CallToolResult result)
    {
        Assert.True(result.IsError != true, $"The tool returned an error: {result.StructuredContent?.GetRawText() ?? Text(result)}");
        return Structured(result)["items"]!.AsArray();
    }

    public static Task<CallToolResult> CallAsync(McpClient client, string tool, object? arguments = null)
    {
        var values = arguments is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.SerializeToElement(arguments).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        return client.CallToolAsync(tool, values).AsTask();
    }

    /// <summary>A tool error: its structured code.</summary>
    public static string ErrorCode(CallToolResult result)
    {
        Assert.True(result.IsError, Text(result));
        return Structured(result)["code"]!.GetValue<string>();
    }

    public static string Rich(params string[] blocks) =>
        """{"format":"atlas-rich","version":1,"plugins":[{"id":"contacts","version":2},{"id":"heading-anchors","version":1},{"id":"pages","version":1},{"id":"youtrack","version":1}],"document":{"type":"doc","content":[""" + string.Join(",", blocks) + "]}}";

    public static string Paragraph(string text) => """{"type":"paragraph","content":[{"type":"text","text":""" + JsonSerializer.Serialize(text) + "}]}";

    public static string Heading(int level, string id, string text) =>
        """{"type":"heading","attrs":{"level":""" + level + ""","headingId":""" + JsonSerializer.Serialize(id) + """},"content":[{"type":"text","text":""" +
        JsonSerializer.Serialize(text) + "}]}";

    public async ValueTask DisposeAsync()
    {
        foreach (var client in m_clients)
        {
            await client.DisposeAsync();
        }
        foreach (var disposable in m_disposables)
        {
            disposable.Dispose();
        }
        Admin.Dispose();
        await Site.DisposeAsync();
    }
}
