using System.Net;
using Atlas.Server.Features.Administration;
using Atlas.Server.Features.Auth.OpenIdConnect;
using Atlas.Server.Features.Hub;
using Atlas.Server.Features.Hub.Contracts;
using Atlas.Server.Features.Hub.Domain;
using Atlas.Server.Tests.Platform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Atlas.Server.Tests.Hub;

public sealed class HubAdministrationBoundaryTests
{
    [PostgreSqlFact]
    public async Task Failed_verification_saves_nothing_and_successful_http_runs_outside_authorization_transaction()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_hub_admin_boundary_", services =>
        {
            services.AddAdministration();
            services.AddDataProtection();
            services.AddSingleton(new AtlasOidcOptions { Providers = [new AtlasOidcProvider { Id = "hub", DisplayName = "Hub", Issuer = "https://hub.example.test/hub", ClientId = "atlas" }] });
        });
        host.Actor.Current = await host.SeedUserAsync("admin", true);
        using var handler = new VerificationHandler(async () =>
        {
            using var session = host.OpenSession();
            using var transaction = session.BeginTransaction();
            Assert.True(await session.CreateSQLQuery("SELECT pg_try_advisory_xact_lock(731145012)").UniqueResultAsync<bool>());
            Assert.Empty(await session.QueryOver<HubConnectionRevision>().ListAsync());
        });
        var scopes = host.Services.GetRequiredService<IServiceScopeFactory>();
        var protection = host.Services.GetRequiredService<IDataProtectionProvider>();
        var factory = new ClientFactory(handler);
        using var sync = new HubSynchronizer(scopes, factory, protection, TimeProvider.System);
        var service = new HubAdministrationService(scopes, factory, protection, TimeProvider.System, sync);
        var request = new SaveHubSettings("https://hub.example.test/hub", "https://hub.example.test/hub", "Preview", "synthetic-secret", null);
        var error = await Assert.ThrowsAsync<ApiException>(() => service.SaveAsync(request, CancellationToken.None));
        Assert.Equal(503, error.Status);
        Assert.DoesNotContain("upstream-secret", error.Message, StringComparison.Ordinal);
        using (var read = host.OpenSession()) Assert.Empty(await read.QueryOver<HubConnectionRevision>().ListAsync());
        handler.Available = true;
        var saved = await service.SaveAsync(request, CancellationToken.None);
        Assert.Equal("Preview", saved.Mode);
        Assert.True(saved.HasToken);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    private sealed class VerificationHandler(Func<Task> check) : HttpMessageHandler
    {
        public bool Available { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await check();
            if (!Available) return new(HttpStatusCode.Forbidden) { Content = new StringContent("upstream-secret") };
            var body = request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
                ? """{"id":"reader","banned":false,"guest":false,"transitiveProjectRoles":[{"project":{"id":"0","global":true},"role":{"permissions":[{"key":"jetbrains.jetpass.user-read-basic"},{"key":"jetbrains.jetpass.group-read"},{"key":"jetbrains.jetpass.role-read"}]}}]}"""
                : """{"usergroups":[],"skip":0,"top":50,"total":0}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
