using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// The one WebApplicationFactory for HTTP tests: a disposable schema (a clone of the migrated template), temp data directories under it,
/// captured logs, and the CSRF dance every mutation needs. Feature specific settings arrive through
/// <c>configure</c>; feature specific services (a fake upstream, an OpenIddict handler) through <c>configureServices</c>.
/// </summary>
internal sealed class AtlasHttpSite : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> m_settings;
    private readonly Action<IServiceCollection>? m_configureServices;

    private AtlasHttpSite(string schema, string databaseConnection, Action<IDictionary<string, string?>>? configure, Action<IServiceCollection>? configureServices)
    {
        Schema = schema;
        DataPath = Path.Combine(Path.GetTempPath(), Schema);
        // Pooled like production; the pool goes with the database when the site is disposed (TestDatabases.DropAsync).
        ConnectionString = new NpgsqlConnectionStringBuilder(databaseConnection) { SearchPath = Schema + ",public", Pooling = true }.ConnectionString;
        m_configureServices = configureServices;
        m_settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:atlas-db"] = ConnectionString,
            // No durability agent, job queue or scheduler unless a test sets it: requested jobs stay queued for IJobRunner.
            ["Jobs:Durable"] = "false",
            ["Setup:BootstrapToken"] = BootstrapToken,
            ["Authentication:Builtin:Enabled"] = "true",
            ["Security:DataPath"] = Path.Combine(DataPath, "security"),
            ["DataProtection:KeysPath"] = Path.Combine(DataPath, "keys"),
            ["Attachments:DataPath"] = Path.Combine(DataPath, "attachments"),
            ["ContentImport:DataPath"] = Path.Combine(DataPath, "imports"),
            ["ContentExport:DataPath"] = ExportPath
        };
        configure?.Invoke(m_settings);
    }

    public string Schema { get; }

    public string ConnectionString { get; }

    public string DataPath { get; }

    public string ExportPath => Path.Combine(DataPath, "exports");

    public string BootstrapToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public string Password { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    public CaptureLoggerProvider Logs { get; } = new();

    /// <summary>The logs of every live site, for <see cref="Json"/> to find the server's record of a failed request.</summary>
    private static readonly ConditionalWeakTable<CaptureLoggerProvider, object> s_siteLogs = new();

    /// <summary>Runs before the host is torn down; a fixture holding an in-flight upstream request releases it here.</summary>
    public Action? OnDisposing { get; set; }

    public static async Task GrantOwnAiAsync(HttpClient admin)
    {
        var me = await Json(await admin.GetAsync("/api/auth/me"), HttpStatusCode.OK);
        await Json(await SendAsync(admin, HttpMethod.Put, $"/api/admin/users/{me["id"]!.GetValue<string>()}/ai-policy",
            new { enabled = true, searchAnswerDailyLimit = (int?)null, chatAnswerDailyLimit = (int?)null,
                pageSummaryDailyLimit = (int?)null, expectedVersion = (string?)null }), HttpStatusCode.OK);
    }

    /// <summary>
    /// Without a configuration of its own: the one site of the process on its one database, shared by every test. A test there
    /// gets its own administrator (<see cref="CompleteSetupAsync"/>) and makes its own space (<see cref="OwnSpaceAsync"/>);
    /// it keeps to the users and spaces it creates and never counts a whole table. With <paramref name="shared"/> the same holds
    /// for a site with settings: tests that ask for the same settings share one (the settings must be constants, not paths or
    /// ports of the test). Every other call gets a site and database of its own, as <see cref="CreateIsolatedAsync"/> does.
    /// </summary>
    public static async Task<AtlasHttpSite> CreateAsync(string schemaPrefix,
        Action<IDictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? configureServices = null, bool shared = false, string? sharedAs = null)
    {
        await SharedHandlerCompiler.PrepareAsync();
        // sharedAs: a named configuration with services, shared like AtlasTestHost's (the first call builds it; no state of one test).
        if ((sharedAs is not null || configureServices is null && (configure is null || shared)) && Environment.GetEnvironmentVariable("ATLAS_TEST_PRIVATE_HOSTS") is null)
        {
            var settings = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            configure?.Invoke(settings);
            var key = sharedAs + "|" + string.Join('\n', settings.Select(setting => setting.Key + "=" + setting.Value));
            return await s_shared.GetOrAdd(key, _ => new Lazy<Task<AtlasHttpSite>>(async () =>
            {
                var site = await CreateUnpreparedAsync("atlas_shared_site_", configure, configureServices);
                site.m_shared = true;
                // Started here, once: WebApplicationFactory does not lock its start, so tests that touched a new shared site at
                // the same moment each built a host of their own on the one database (17 were counted). A client then talked
                // to one host while site.Services answered from another, and everything a host keeps in memory was split.
                GC.KeepAlive(site.Services);
                return site;
            })).Value;
        }
        return await CreateUnpreparedAsync(schemaPrefix, configure, configureServices);
    }

    /// <summary>A site and a database of the test's own: for a test of the first start or the installation itself, one that changes
    /// the schema, or one that changes a setting the whole installation shares. Say why at the call.</summary>
    public static async Task<AtlasHttpSite> CreateIsolatedAsync(string schemaPrefix,
        Action<IDictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? configureServices = null)
    {
        await SharedHandlerCompiler.PrepareAsync();
        return await CreateUnpreparedAsync(schemaPrefix, configure, configureServices);
    }

    // The shared sites by their settings; never disposed (the databases of an ended process are swept by the next).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<AtlasHttpSite>>> s_shared = new(StringComparer.Ordinal);
    private static int s_clients;
    private readonly SemaphoreSlim m_installing = new(1, 1);
    private bool m_shared;
    private bool m_installed;

    /// <summary>A site over an empty schema: the test brings it to an earlier release (OutboxTests.ApplyBeforeAsync) before the first
    /// request, and the start migrates the rest as an upgrade does.</summary>
    public static async Task<AtlasHttpSite> CreateEmptyAsync(string schemaPrefix, Action<IDictionary<string, string?>>? configure = null)
    {
        await SharedHandlerCompiler.PrepareAsync();
        var schema = schemaPrefix + Guid.NewGuid().ToString("N");
        return new AtlasHttpSite(schema, await TestDatabases.CreateEmptyAsync(schema), configure, null);
    }

    internal static async Task<AtlasHttpSite> CreateUnpreparedAsync(string schemaPrefix,
        Action<IDictionary<string, string?>>? configure = null, Action<IServiceCollection>? configureServices = null)
    {
        // The schema is a clone of the run's migrated template in its own database (TestDatabases), so the
        // host's startup migration finds nothing to apply.
        TestCosts.Private(schemaPrefix);
        var schema = schemaPrefix + Guid.NewGuid().ToString("N");
        return new AtlasHttpSite(schema, await TestDatabases.CreateMigratedAsync(schema), configure, configureServices);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(Logs);
        });
        WatchLogs(Logs);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(m_settings));
        builder.ConfigureServices(SharedHandlerCompiler.Register);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestClientAddress>());
        // No HTTP test may reach a real model (plan §0.7): the fake provider replaces the client factory.
        builder.ConfigureServices(Atlas.Server.Tests.Ai.Fakes.FakeAi.Register);
        // The sweep changes rows of every site on its own clock (the linking graph); test hosts leave it out and the tests that need a
        // pass send SweepContentReferences. The account contacts are a job (AccountContactsJob): a test runs it with IJobRunner.
        builder.ConfigureServices(services =>
            services.Remove(services.Single(service => service.ImplementationType == typeof(Atlas.Server.Platform.Linking.ContentReferenceJob))));
        if (m_configureServices is { } configureServices)
        {
            builder.ConfigureServices(configureServices);
        }
    }

    /// <summary>
    /// TestServer gives every request no remote address, so on a shared site all tests would spend one address's rate limits
    /// (120 sign-ins a minute). A client of a shared site names an address of its own in this header and the request gets it.
    /// </summary>
    private sealed class TestClientAddress : IStartupFilter
    {
        public const string Header = "X-Atlas-Test-Client";

        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(following => context =>
            {
                if (context.Request.Headers.TryGetValue(Header, out var address) && IPAddress.TryParse(address.ToString(), out var parsed))
                {
                    context.Connection.RemoteIpAddress = parsed;
                }
                return following(context);
            });
            next(app);
        };
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Minimal hosting reads startup-module settings while Program is executing. Host configuration
        // must supply them before that eager registration happens; a late ConfigureAppConfiguration
        // override only reaches lazy runtime services.
        builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(m_settings));
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            TestCosts.Add("site start", startedAt);
        }
    }

    /// <summary>Whether the site serves over a real Kestrel on a loopback port (<see cref="UseLoopbackKestrel"/>) instead of TestServer.</summary>
    public bool RealServer { get; private set; }

    /// <summary>Serves over a real Kestrel on a free loopback port (plain HTTP; the Development cookie policy allows it) instead of
    /// TestServer, for what only a real server enforces, such as an endpoint's request body limit. Call before the first client.</summary>
    public void UseLoopbackKestrel()
    {
        UseKestrel(0);
        RealServer = true;
    }

    /// <summary>The address clients send to: the Kestrel port of a real server, else TestServer's https://localhost.</summary>
    public Uri ServerAddress
    {
        get
        {
            if (!RealServer)
            {
                return new Uri("https://localhost");
            }
            StartServer();
            return ClientOptions.BaseAddress;
        }
    }

    public HttpClient Client()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = ServerAddress,
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        if (m_shared)
        {
            var number = Interlocked.Increment(ref s_clients);
            client.DefaultRequestHeaders.Add(TestClientAddress.Header, $"10.{number >> 16 & 255}.{number >> 8 & 255}.{number & 255}");
        }
        return client;
    }

    /// <summary>A new space of the signed-in client with a name of its own; returns its id. On a shared site a test works in
    /// spaces it made, never in the first one the installation lists.</summary>
    public static async Task<string> OwnSpaceAsync(HttpClient client, string name = "Test")
        => (await Json(await SendAsync(client, HttpMethod.Post, "/api/spaces", new { name = TestLogin.For(name) }), HttpStatusCode.OK))["id"]!.GetValue<string>();

    /// <summary>Fetches the CSRF token for this client and sends the mutation with it. Static: it needs no site state.</summary>
    /// <param name="reauth">A verified re-authentication proof for the X-Atlas-Reauth header of a security change.</param>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? payload = null, string? reauth = null)
    {
        var csrf = await Json(await client.GetAsync("/api/auth/csrf"), HttpStatusCode.OK);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", csrf["token"]!.GetValue<string>());
        if (reauth is not null)
        {
            request.Headers.Add("X-Atlas-Reauth", reauth);
        }
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }
        return await client.SendAsync(request);
    }

    /// <summary>A fresh re-authentication of the signed-in builtin account for a security change of the target (null: global
    /// security, or {vaultId} or {secretId}); returns the verified proof.</summary>
    public static async Task<string> ReauthAsync(HttpClient client, string password, Guid? vaultId = null, Guid? secretId = null, string operation = "security")
    {
        var started = await Json(await SendAsync(client, HttpMethod.Post, "/api/security/reauth", new { operation, vaultId, secretId }), HttpStatusCode.OK);
        Assert.Equal("password", started["method"]!.GetValue<string>());
        var id = started["id"]!.GetValue<string>();
        var verified = await Json(await SendAsync(client, HttpMethod.Post, $"/api/security/reauth/{id}/password", new { password }), HttpStatusCode.OK);
        Assert.Equal("verified", verified["state"]!.GetValue<string>());
        return id;
    }

    public static async Task<JsonNode> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            Assert.Fail(await UnexpectedAnswerAsync(response, expected));
        }
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    internal static void WatchLogs(CaptureLoggerProvider logs) => s_siteLogs.AddOrUpdate(logs, logs);

    /// <summary>
    /// The request, the answer's body and, when the body names a traceId (an unhandled error), the server's log lines of that request
    /// with the exception, which the problem body leaves out: an intermittent 500 on CI is then readable from the failure alone.
    /// </summary>
    internal static async Task<string> UnexpectedAnswerAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        var failure = $"Expected {(int)expected} {expected}, got {(int)response.StatusCode} {response.StatusCode} for "
            + $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: {body}";
        string? traceId;
        try
        {
            traceId = JsonNode.Parse(body)?["traceId"]?.GetValue<string>();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            traceId = null;
        }
        if (string.IsNullOrEmpty(traceId))
        {
            return failure;
        }
        var logged = s_siteLogs.SelectMany(site => site.Key.Messages).Where(line => line.Contains(traceId, StringComparison.Ordinal)).ToArray();
        return failure + "\nServer log of the request:\n" + (logged.Length == 0 ? "(none)" : string.Join('\n', logged));
    }

    public async Task<JsonNode> CompleteSetupAsync(HttpClient admin, string workspaceName = "Atlas tests",
        string username = "administrator", string displayName = "Administrator")
    {
        if (m_shared)
        {
            return await OwnAdministratorAsync(admin, username, displayName);
        }
        var response = await SendAsync(admin, HttpMethod.Post, "/api/setup/complete", new
        {
            bootstrapToken = BootstrapToken, workspaceName, username, displayName, password = Password
        });
        return await Json(response, HttpStatusCode.OK);
    }

    /// <summary>On a shared site the installation is completed once, by nobody's client; each test then gets a wiki administrator
    /// of its own (the login gets a unique suffix, the password is <see cref="Password"/>), signed in on <paramref name="admin"/>.
    /// Returns what the setup returns: the signed-in account.</summary>
    private async Task<JsonNode> OwnAdministratorAsync(HttpClient admin, string username, string displayName)
    {
        if (!m_installed)
        {
            await m_installing.WaitAsync();
            try
            {
                if (!m_installed)
                {
                    using var founder = Client();
                    // The host's first initialization may have failed on a busy server; its initializer retries only every 5 s and
                    // until then every request answers 503. Initialize now (a no-op once done); a lasting failure is reported below.
                    string? failure = null;
                    try
                    {
                        await Services.GetRequiredService<AtlasDatabase>().InitializeAsync(CancellationToken.None);
                        var response = await SendAsync(founder, HttpMethod.Post, "/api/setup/complete", new
                        {
                            bootstrapToken = BootstrapToken, workspaceName = "Atlas tests", username = "founder", displayName = "Founder", password = Password
                        });
                        if (response.StatusCode != HttpStatusCode.OK)
                        {
                            failure = $"The founder's setup answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
                        }
                    }
                    catch (ApiException e)
                    {
                        failure = $"The site's database did not initialize ({e.Status}): {e.Message}";
                    }
                    if (failure is not null)
                    {
                        Assert.Fail(failure + "\nSite warnings and errors:\n" + string.Join('\n', Logs.Entries
                            .Where(entry => entry.Level >= LogLevel.Warning).Select(entry => $"{entry.Level} {entry.Category}: {entry.Message}")));
                    }
                    m_installed = true;
                }
            }
            finally
            {
                m_installing.Release();
            }
        }
        var account = new UserAccount { Id = Guid.NewGuid(), Username = TestLogin.For(username), DisplayName = displayName, IsWikiAdmin = true };
        using (var scope = Services.CreateScope())
        using (var session = scope.ServiceProvider.GetRequiredService<AtlasDatabase>().OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<UserAccount>>();
            await session.SaveAsync(account);
            await AccountHistory.RecordAsync(session, account, null, CancellationToken.None);
            await session.SaveAsync(new Atlas.Server.Features.Auth.Domain.BuiltinCredential { UserId = account.Id, PasswordHash = hasher.HashPassword(account, Password) });
            await session.SaveAsync(new Atlas.Server.Features.Auth.Domain.UserAppearancePreference { UserId = account.Id });
            await transaction.CommitAsync();
        }
        await Json(await SendAsync(admin, HttpMethod.Post, "/api/auth/login", new { username = account.Username, password = Password }), HttpStatusCode.OK);
        return await Json(await admin.GetAsync("/api/auth/me"), HttpStatusCode.OK);
    }

    /// <summary>Runs a statement inside this site's schema. Test fixtures only: production paths never bypass NHibernate.</summary>
    public async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteScalarAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (m_shared)
        {
            return;
        }
        OnDisposing?.Invoke();
        await TestCosts.MeasureAsync("site stop", async () => await base.DisposeAsync());
        await TestDatabases.DropAsync(ConnectionString);
        var directory = Path.GetFullPath(DataPath);
        if (!directory.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(directory), Schema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unexpected test directory.");
        }
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
