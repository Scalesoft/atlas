using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class HttpHostingTests : IDisposable
{
    // font-src data: carries Monaco's codicon font; connect-src 'self' covers the same-host SignalR WebSocket.
    private const string DocumentPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; font-src 'self' data:; " +
        "connect-src 'self'; worker-src 'self'; frame-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    private const string OwnPolicy = "default-src 'none'; frame-ancestors 'none'";

    private readonly string m_root = Path.Combine(Path.GetTempPath(), "atlas-static-hosting-" + Guid.NewGuid().ToString("N"));

    public HttpHostingTests()
    {
        Directory.CreateDirectory(Path.Combine(m_root, "assets"));
        File.WriteAllText(Path.Combine(m_root, "index.html"), "executable shell");
        File.WriteAllText(Path.Combine(m_root, "assets", "index-12345678.js"), "executable asset");
    }

    public void Dispose() => Directory.Delete(m_root, true);

    [Trait("Category", "Smoke")]
    [Theory]
    [InlineData("/", "executable shell", true, false)]
    [InlineData("/spaces/example", "executable shell", true, false)]
    [InlineData("/assets/index-12345678.js", "executable asset", false, false)]
    [InlineData("/s/example-capability", "executable shell", true, true)]
    [InlineData("/api/pages", "api body", false, false)]
    [InlineData("/scalar/", "scalar shell", false, false)]
    public async Task Static_routes_use_the_explicit_web_root(string path, string expected, bool reportOnlyPolicy, bool isSharedPage)
    {
        using var files = new PhysicalFileProvider(m_root);
        await using var app = await StartAsync(files, enforce: false);

        var response = await app.GetTestClient().GetAsync(path);

        response.EnsureSuccessStatusCode();
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        // Only HTML documents get the policy; /scalar keeps Scalar.AspNetCore's inline bootstrap working.
        Assert.Equal(reportOnlyPolicy ? DocumentPolicy : null, Header(response, "Content-Security-Policy-Report-Only"));
        Assert.Null(Header(response, "Content-Security-Policy"));
        if (isSharedPage)
        {
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
            Assert.Equal("noindex, nofollow, noarchive", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
        }
    }

    /// <summary>SEC-I-2: the policy is enforced unless the operator explicitly asks for Report-Only (Enforce=false).</summary>
    [Fact]
    public async Task Policy_is_enforced_without_configuration()
    {
        using var files = new PhysicalFileProvider(m_root);
        await using var app = await StartAsync(files, enforce: null);

        var response = await app.GetTestClient().GetAsync("/s/example-capability");

        Assert.Equal(DocumentPolicy, Header(response, "Content-Security-Policy"));
        Assert.Null(Header(response, "Content-Security-Policy-Report-Only"));
    }

    /// <summary>Report-Only is a development choice: in Production the switch is ignored and the policy stays enforced.</summary>
    [Fact]
    public async Task Production_ignores_report_only()
    {
        using var files = new PhysicalFileProvider(m_root);
        await using var app = await StartAsync(files, enforce: false, environment: "Production");

        var response = await app.GetTestClient().GetAsync("/s/example-capability");

        Assert.Equal(DocumentPolicy, Header(response, "Content-Security-Policy"));
        Assert.Null(Header(response, "Content-Security-Policy-Report-Only"));
    }

    [Fact]
    public async Task Html_response_with_its_own_policy_keeps_it()
    {
        using var files = new PhysicalFileProvider(m_root);
        await using var app = await StartAsync(files, enforce: false);

        var response = await app.GetTestClient().GetAsync("/signin-oidc/test");

        Assert.Equal(OwnPolicy, Header(response, "Content-Security-Policy"));
        Assert.Null(Header(response, "Content-Security-Policy-Report-Only"));
    }

    [Theory]
    [InlineData("GET", "/api/projector/spaces/1/overview", true)]
    [InlineData("GET", "/api/objects/1/relations", true)]
    [InlineData("GET", "/api/contacts", true)]
    [InlineData("GET", "/api/notifications/unread", true)]
    [InlineData("GET", "/api/ai/capabilities", true)]
    [InlineData("GET", "/api/boards/1", true)]
    [InlineData("GET", "/api/entities/contact", true)]
    [InlineData("GET", "/api/admin/logs", true)]
    [InlineData("GET", "/api/admin/jobs/1/runs", true)]
    [InlineData("GET", "/api/admin/users", false)]
    [InlineData("POST", "/api/boards/1/operations", false)]
    [InlineData("POST", "/api/contacts/live", false)]
    [InlineData("GET", "/api/auth/csrf", false)]
    [InlineData("GET", "/api/pages/1/share-links", false)]
    public void Content_reads_are_compressed_and_secret_bearing_ones_are_not(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        Assert.Equal(expected, AtlasHttpHosting.IsCompressedApiRead(context.Request));
    }

    [Fact]
    public void Trusted_proxy_addresses_are_literal_and_explicit()
    {
        var addresses = AtlasHttpHosting.ParseTrustedProxies(["10.0.0.10", "::1"]);
        Assert.Equal(2, addresses.Count);
        Assert.Equal("10.0.0.10", addresses[0].ToString());
        Assert.Empty(AtlasHttpHosting.ParseTrustedProxies([]));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("10.0.0.0/8")]
    [InlineData("proxy.example.com")]
    [InlineData("")]
    public void Wildcards_networks_dns_and_empty_proxy_values_are_rejected(string value) => Assert.Throws<ArgumentException>(() => AtlasHttpHosting.ParseTrustedProxies([value]));

    [Theory]
    [InlineData("/assets/index-D8h6cM9x.js", true)]
    [InlineData("/assets/index-Css_12ab.css", true)]
    [InlineData("/assets/editor-Css_12ab.woff2", true)]
    [InlineData("/index.html", false)]
    [InlineData("/assets/index.js", false)]
    [InlineData("/api/pages/index-D8h6cM9x.js", false)]
    [InlineData("/assets/index-D8h6cM9x.html", false)]
    public void Only_fingerprinted_static_assets_can_be_immutable(string path, bool expected) => Assert.Equal(expected, AtlasHttpHosting.IsFingerprintAsset(path));

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Build_time_variants_are_served_for_accepted_encodings_and_the_shell_revalidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-static-precompressed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "executable shell");
        await File.WriteAllTextAsync(Path.Combine(root, "assets", "index-12345678.js"), "executable asset");
        await File.WriteAllTextAsync(Path.Combine(root, "assets", "index-12345678.js.br"), "brotli variant");
        await File.WriteAllTextAsync(Path.Combine(root, "assets", "index-12345678.js.gz"), "gzip variant");
        try
        {
            using var files = new PhysicalFileProvider(root);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAtlasHttpHosting(builder.Configuration);
            await using var app = builder.Build();
            app.UseAtlasStaticFiles(files);
            app.MapFallbackToFile("index.html", AtlasHttpHosting.CreateStaticFileOptions(files));
            await app.StartAsync();
            var client = app.GetTestClient();

            async Task<HttpResponseMessage> Get(string path, string? acceptEncoding)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                if (acceptEncoding is not null) request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
                return await client.SendAsync(request);
            }

            var brotli = await Get("/assets/index-12345678.js", "gzip, deflate, br");
            Assert.Equal("brotli variant", await brotli.Content.ReadAsStringAsync());
            Assert.Equal("br", Assert.Single(brotli.Content.Headers.ContentEncoding));
            Assert.Equal("text/javascript", brotli.Content.Headers.ContentType?.MediaType);
            Assert.Contains("Accept-Encoding", brotli.Headers.Vary);
            Assert.Equal("public, max-age=31536000, immutable", brotli.Headers.CacheControl?.ToString());

            var gzip = await Get("/assets/index-12345678.js", "gzip, br;q=0");
            Assert.Equal("gzip variant", await gzip.Content.ReadAsStringAsync());
            Assert.Equal("gzip", Assert.Single(gzip.Content.Headers.ContentEncoding));

            var identity = await Get("/assets/index-12345678.js", null);
            Assert.Equal("executable asset", await identity.Content.ReadAsStringAsync());
            Assert.Empty(identity.Content.Headers.ContentEncoding);

            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Get("/assets/index-12345678.js.br", "br")).StatusCode);
            var shell = await Get("/", "br");
            Assert.Equal("executable shell", await shell.Content.ReadAsStringAsync());
            Assert.Equal("no-cache", shell.Headers.CacheControl?.ToString());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [PostgreSqlFact]
    public async Task Fingerprinted_asset_is_answered_before_authentication_and_the_api_still_checks_the_session()
    {
        // The real pipeline over this class's web root; AtlasPipeline and the SPA fallback resolve the last registration.
        using var files = new PhysicalFileProvider(m_root);
        // The paths whose session cookie the handler read. The cookie handler skips the lookup for a file served after authentication
        // (AtlasHttpHosting.IsAnsweredByStaticFiles), so the statements alone do not show that the asset left before it.
        var authenticated = new ConcurrentQueue<string>();
        await using var site = await AtlasHttpSite.CreateAsync("atlas_static_session_", configureServices: services =>
        {
            services.AddSingleton(files);
            services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                var validate = options.Events.OnValidatePrincipal;
                options.Events.OnValidatePrincipal = context =>
                {
                    authenticated.Enqueue(context.Request.Path);
                    return validate(context);
                };
            });
        });
        site.Server.PreserveExecutionContext = true;
        using var client = site.Client();
        await site.CompleteSetupAsync(client);

        // The client sends its session cookie with every request, as a browser does for the files of a cold start.
        var (asset, assetStatements) = await SqlStatementProbe.GetAsync(client, "/assets/index-12345678.js");
        Assert.Equal("executable asset", await asset.Content.ReadAsStringAsync());
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
        Assert.Empty(assetStatements);
        Assert.DoesNotContain("/assets/index-12345678.js", authenticated);

        var (me, apiStatements) = await SqlStatementProbe.GetAsync(client, "/api/auth/me");
        Assert.Equal(System.Net.HttpStatusCode.OK, me.StatusCode);
        Assert.Contains(apiStatements, statement => statement.Contains("RevokedBrowserSession", StringComparison.Ordinal));
        Assert.Contains("/api/auth/me", authenticated);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? Assert.Single(values) : null;

    /// <summary>The static shell behind the hosting middleware, plus stand-ins for JSON, Scalar and the OIDC mapping check page.</summary>
    private static async Task<WebApplication> StartAsync(IFileProvider files, bool? enforce, string environment = "Development")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        if (enforce is { } value)
        {
            builder.Configuration["Http:ContentSecurityPolicy:Enforce"] = value ? "true" : "false";
        }
        builder.Services.AddAtlasHttpHosting(builder.Configuration);
        var app = builder.Build();
        app.UseAtlasPublicSharingHeaders();
        app.UseAtlasContentSecurityPolicy();
        app.UseAtlasStaticFiles(files);
        app.MapGet("/api/pages", () => Results.Text("api body", "application/json"));
        app.MapGet("/scalar/", () => Results.Text("scalar shell", "text/html"));
        app.MapGet("/signin-oidc/test", (HttpContext context) =>
        {
            context.Response.Headers.ContentSecurityPolicy = OwnPolicy;
            return Results.Text("mapping check", "text/html; charset=utf-8");
        });
        app.MapFallbackToFile("index.html", AtlasHttpHosting.CreateStaticFileOptions(files));
        await app.StartAsync();
        return app;
    }
}
