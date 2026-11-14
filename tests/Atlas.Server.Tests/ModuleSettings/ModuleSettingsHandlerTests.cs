using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.ModuleSettings.Commands;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using Atlas.Server.Features.ModuleSettings.Queries;
using Atlas.Server.Features.Setup.Domain;
using Xunit;

namespace Atlas.Server.Tests.ModuleSettings;

public sealed class ModuleSettingsHandlerTests
{
    private const string OidcSettings = "saves the sign-in settings of the installation (OidcSettingsRevision)";

    [PostgreSqlFact]
    public async Task Administrator_mappings_roundtrip_per_provider_and_revision()
    {
        await using var f = await ModuleSettingsFixture.CreateAsync(privateBecause: OidcSettings);
        var request = new SaveOidcSettings(null, "https://wiki.example", [
            new("hub", "Hub", "https://hub.example", "client", null, false, ["openid"], true,
                [new("groups", "atlas-admins"), new("roles", "WikiAdmin")]),
            new("other", "Other", "https://other.example", "client", null, false, ["openid"], true)]);
        var saved = await f.As<OidcSettingsView>(f.Admin, new SaveOidcConfiguration(request));
        var loaded = await f.As<OidcSettingsView>(f.Admin, new GetOidcSettings());
        Assert.Equal(saved.Version, loaded.Version);
        Assert.Equal(request.Providers[0].AdminMappings, loaded.Providers.Single(x => x.Id == "hub").AdminMappings);
        Assert.Empty(loaded.Providers.Single(x => x.Id == "other").AdminMappings!);
        // UX-N-12: what removing a provider costs, the accounts linked through its issuer (a trailing slash is the same issuer).
        Assert.All(loaded.Providers, provider => Assert.Equal(0, provider.LinkedAccounts));
        using (var seed = f.Host.OpenSession())
        {
            await seed.CreateSQLQuery("INSERT INTO \"ExternalIdentity\"(\"Id\",\"UserId\",\"Issuer\",\"Subject\",\"ActiveFrom\") VALUES(:id,:user,'https://hub.example/','member',now())")
                .SetParameter("id", Guid.NewGuid()).SetParameter("user", f.Member.Id).ExecuteUpdateAsync();
        }
        var linked = await f.As<OidcSettingsView>(f.Admin, new GetOidcSettings());
        Assert.Equal((1, 0), (linked.Providers.Single(x => x.Id == "hub").LinkedAccounts, linked.Providers.Single(x => x.Id == "other").LinkedAccounts));
        var cleared = await f.As<OidcSettingsView>(f.Admin, new SaveOidcConfiguration(request with
        {
            ExpectedVersion = saved.Version,
            Providers = [request.Providers[0] with { AdminMappings = [] }]
        }));
        Assert.Empty(cleared.Providers[0].AdminMappings!);
        using var session = f.Host.OpenSession();
        Assert.Equal(2, await session.QueryOver<OidcAdminMappingRow>().Where(x => x.SettingsId == saved.Version).RowCountAsync());
    }

    [PostgreSqlFact]
    public async Task Stalled_discovery_body_times_out_and_releases_content_locks()
    {
        await using var f = await DiscoveryFixture(TimeSpan.FromMilliseconds(100));
        f.Host.Actor.Current = f.Admin;
        // Generous: only the 100 ms client timeout may end the probe, and the first invocation also compiles the
        // handler, which takes seconds while the whole suite runs in parallel.
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await f.Host.InvokeAsync<OidcConnectionTest>(new TestOidcConnection(new("hub")), caller.Token);

        Assert.False(result.Ok);
        Assert.False(caller.IsCancellationRequested);
        using var scope = f.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var transaction = session.BeginTransaction();
        using var lockTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await scope.ServiceProvider.GetRequiredService<IAtlasLocks>().AcquireContentExclusiveAsync(lockTimeout.Token);
        await transaction.CommitAsync(lockTimeout.Token);
    }

    [PostgreSqlFact]
    public async Task Caller_cancellation_while_reading_discovery_body_is_not_reported_as_provider_failure()
    {
        var upstream = new StalledBodyHandler();
        // Both limits only guard against a hang; the test cancels the caller itself once the body has started.
        await using var f = await DiscoveryFixture(TimeSpan.FromSeconds(60), upstream);
        f.Host.Actor.Current = f.Admin;
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var pending = f.Host.InvokeAsync<OidcConnectionTest>(new TestOidcConnection(new("hub")), caller.Token);
        await upstream.BodyStarted.Task.WaitAsync(caller.Token);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [PostgreSqlFact]
    public async Task Discovery_requires_matching_issuer_and_safe_https_endpoints()
    {
        const string valid = """{"issuer":"https://hub.example","authorization_endpoint":"https://hub.example/authorize","token_endpoint":"https://hub.example/token"}""";
        var cases = new List<(string Body, bool Ok)>
        {
            (valid, true),
            (valid.Replace("\"issuer\":\"https://hub.example\"", "\"issuer\":\"https://other.example\"", StringComparison.Ordinal), false),
            ("{}", false),
            ("{\"issuer\":42}", false),
            ("invalid-json-provider-secret", false)
        };
        foreach (var endpoint in new object?[] { null, 42, "/relative", "http://hub.example/token", "https://user:secret@hub.example/token", "https://hub.example/token#fragment" })
        {
            cases.Add((JsonSerializer.Serialize(new { issuer = "https://hub.example", authorization_endpoint = "https://hub.example/authorize", token_endpoint = endpoint }), false));
        }
        var responses = new Queue<HttpResponseMessage>(cases.Select(item => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(item.Body) }));
        await using var f = await DiscoveryFixture(TimeSpan.FromSeconds(5), new ResponseHandler(() => responses.Dequeue()));
        foreach (var item in cases)
        {
            var result = await f.As<OidcConnectionTest>(f.Admin, new TestOidcConnection(new("hub")));
            Assert.Equal(item.Ok, result.Ok);
            Assert.DoesNotContain("provider-secret", result.Message);
        }
    }

    [PostgreSqlFact]
    public async Task Discovery_rejects_failed_responses_and_oversized_declared_or_streamed_bodies()
    {
        var responses = new Queue<HttpResponseMessage>([
            new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("provider-secret") },
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[262145]) },
            new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[262145])) }
        ]);
        await using var f = await DiscoveryFixture(TimeSpan.FromSeconds(5), new ResponseHandler(() => responses.Dequeue()));
        for (var index = 0; index < 3; index++)
        {
            var result = await f.As<OidcConnectionTest>(f.Admin, new TestOidcConnection(new("hub")));
            Assert.False(result.Ok);
            Assert.DoesNotContain("provider-secret", result.Message);
        }
    }

    [PostgreSqlFact]
    public async Task Stalled_discovery_headers_time_out_without_caller_cancellation()
    {
        await using var f = await DiscoveryFixture(TimeSpan.FromMilliseconds(100), new StalledHeadersHandler());
        f.Host.Actor.Current = f.Admin;
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await f.Host.InvokeAsync<OidcConnectionTest>(new TestOidcConnection(new("hub")), caller.Token);

        Assert.False(result.Ok);
        Assert.False(caller.IsCancellationRequested);
    }

    /// <summary>A host of its own: the test's fake discovery upstream and the sign-in settings of the installation (OidcSettingsRevision).</summary>
    private static async Task<ModuleSettingsFixture> DiscoveryFixture(TimeSpan timeout, HttpMessageHandler? upstream = null)
    {
        var fixture = await ModuleSettingsFixture.CreateAsync(services =>
            services.AddHttpClient("oidc-settings-discovery", client => client.Timeout = timeout)
                .ConfigurePrimaryHttpMessageHandler(() => upstream ?? new StalledBodyHandler()));
        await fixture.As<OidcSettingsView>(fixture.Admin, new SaveOidcConfiguration(new(null, "https://wiki.example",
            [new("hub", "Hub", "https://hub.example", "client", null, false, ["openid"], false)])));
        return fixture;
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        public TaskCompletionSource BodyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(BodyStarted)) });
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response());
    }

    private sealed class StalledHeadersHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => new TaskCompletionSource<HttpResponseMessage>().Task.WaitAsync(cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class StalledStream(TaskCompletionSource started) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
            return 0;
        }
    }

    [PostgreSqlFact]
    public async Task Module_catalog_and_empty_oidc_settings_are_wiki_admin_only()
    {
        await using var f = await ModuleSettingsFixture.CreateAsync();
        var modules = await f.As<IReadOnlyList<ModuleSettingsDescriptor>>(f.Admin, new ListModules());
        Assert.Equal(["oidc", "youtrack", "ai", "editor-assist", "wiki-js-import"], modules.Select(x => x.Id));

        var view = await f.As<OidcSettingsView>(f.Admin, new GetOidcSettings());
        Assert.Null(view.Version);
        Assert.Empty(view.Providers);
        Assert.False(view.RestartRequired);
        Assert.True(view.BuiltinEnabled);

        Assert.False(await f.As<bool>(f.Admin, new GetSetupState()));
        await ModuleSettingsFixture.Denied(403, () => f.As<IReadOnlyList<ModuleSettingsDescriptor>>(f.Member, new ListModules()));
        await ModuleSettingsFixture.Denied(403, () => f.As<OidcSettingsView>(f.Member, new GetOidcSettings()));
        await ModuleSettingsFixture.Denied(403, () => f.As<bool>(f.Member, new GetSetupState()));
    }

    [PostgreSqlFact]
    public async Task Saving_settings_is_compare_and_swap_keeps_the_secret_and_writes_one_current_revision()
    {
        await using var f = await ModuleSettingsFixture.CreateAsync(privateBecause: OidcSettings);
        var request = new SaveOidcSettings(null, "https://wiki.example",
            [new("hub", "Hub", "https://hub.example", "client", "fixture-only-secret", false, ["openid", "profile"], false)]);
        var saved = await f.As<OidcSettingsView>(f.Admin, new SaveOidcConfiguration(request));
        Assert.NotNull(saved.Version);
        Assert.True(saved.RestartRequired);
        Assert.True(Assert.Single(saved.Providers).HasSecret);
        Assert.Equal("https://wiki.example/signin-oidc/hub", saved.Providers[0].CallbackUrl);

        await ModuleSettingsFixture.Denied(409, () => f.As<OidcSettingsView>(f.Admin, new SaveOidcConfiguration(request)));
        var preserved = request with { ExpectedVersion = saved.Version, Providers = [request.Providers[0] with { ClientSecret = null, DisplayName = "Corporate" }] };
        var updated = await f.As<OidcSettingsView>(f.Admin, new SaveOidcConfiguration(preserved));
        Assert.True(updated.Providers[0].HasSecret);
        Assert.Equal("Corporate", updated.Providers[0].DisplayName);

        await ModuleSettingsFixture.Denied(400, () => f.As<OidcSettingsView>(f.Admin, new SaveOidcConfiguration(
            preserved with { ExpectedVersion = updated.Version, Providers = [preserved.Providers[0] with { Issuer = "https://other.example" }] })));
        await ModuleSettingsFixture.Denied(400, () => f.As<OidcSettingsView>(f.Admin, new SaveOidcConfiguration(
            preserved with { ExpectedVersion = updated.Version, Providers = [preserved.Providers[0] with { Issuer = "http://insecure.example" }] })));
        await ModuleSettingsFixture.Denied(403, () => f.As<OidcSettingsView>(f.Member, new SaveOidcConfiguration(preserved with { ExpectedVersion = updated.Version })));

        using var session = f.Host.OpenSession();
        var revisions = await session.QueryOver<OidcSettingsRevision>().OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(revisions[0].ActiveTo, revisions[1].ActiveFrom);
        Assert.Null(revisions[1].ActiveTo);
        Assert.Equal(f.Admin.Id, revisions[1].AuthorId);
        var stored = await session.QueryOver<OidcProviderSettings>().Where(x => x.SettingsId == revisions[1].Id).SingleOrDefaultAsync();
        Assert.DoesNotContain("fixture-only-secret", stored.ProtectedSecret, StringComparison.Ordinal);
        Assert.Equal(2, await session.QueryOver<OidcProviderScope>().Where(x => x.SettingsId == revisions[1].Id).RowCountAsync());
    }

    [PostgreSqlFact]
    public async Task Unknown_provider_cannot_be_tested_and_setup_completion_is_idempotent()
    {
        await using var f = await ModuleSettingsFixture.CreateAsync(privateBecause: "completes the module setup of the installation (ModuleOnboarding)");
        await ModuleSettingsFixture.Denied(404, () => f.As<OidcConnectionTest>(f.Admin, new TestOidcConnection(new("missing"))));

        var onboarding = Guid.NewGuid();
        await f.Host.SeedAsync(session => session.SaveAsync(new ModuleOnboarding
        {
            Id = onboarding, WorkspaceId = 1, ActiveFrom = DateTime.UtcNow
        }));
        Assert.True(await f.As<bool>(f.Admin, new GetSetupState()));
        await ModuleSettingsFixture.Denied(403, () => f.As(f.Member, new CompleteModuleSetup()));

        await f.As(f.Admin, new CompleteModuleSetup());
        await f.As(f.Admin, new CompleteModuleSetup());
        Assert.False(await f.As<bool>(f.Admin, new GetSetupState()));

        using var session = f.Host.OpenSession();
        var row = await session.GetAsync<ModuleOnboarding>(onboarding);
        Assert.Equal(f.Admin.Id, row.CompletedById);
        Assert.True(row.ActiveTo > row.ActiveFrom);
    }

    [PostgreSqlFact]
    public async Task An_invalid_payload_from_a_non_admin_is_refused_as_forbidden_not_as_bad_request()
    {
        await using var f = await ModuleSettingsFixture.CreateAsync();
        var tooMany = Enumerable.Range(0, 11)
            .Select(i => new SaveOidcProvider("hub" + i, "Hub", "https://hub" + i + ".example", "client", null, false, ["openid"], false))
            .ToArray();
        // Authorization is decided before the payload is looked at, so the reader never learns the request was malformed.
        await ModuleSettingsFixture.Denied(403, () => f.As<OidcSettingsView>(f.Member, new SaveOidcConfiguration(new(null, "https://wiki.example", tooMany))));
        await ModuleSettingsFixture.Denied(400, () => f.As<OidcSettingsView>(f.Admin, new SaveOidcConfiguration(new(null, "https://wiki.example", tooMany))));
    }
}
