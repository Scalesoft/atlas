using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class ContentExportFinalAuthorizationTests
{
    [PostgreSqlFact]
    public Task Token_expiry_during_export_validation_denies_the_download()
        => VerifyTokenInvalidationAsync(expire: true);

    [PostgreSqlFact]
    public Task Token_revocation_during_export_validation_denies_the_download()
        => VerifyTokenInvalidationAsync(expire: false);

    private static async Task VerifyTokenInvalidationAsync(bool expire)
    {
        var barrier = new AuthorizationBarrier(expire);
        // Own site: the barrier pauses the first actor lock of any request after arming, and the expiry case creates a function (DDL).
        await using var site = await AtlasHttpSite.CreateAsync("atlas_export_final_", configureServices: services =>
        {
            services.AddScoped<IContentAccess>(provider => new PausedContentAccess(
                ActivatorUtilities.CreateInstance<ContentAccessService>(provider),
                provider.GetRequiredService<NHibernate.ISession>(), barrier));
        });
        using var admin = site.Client();
        var user = await site.CompleteSetupAsync(admin);
        var spaces = await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK);
        var spaceId = spaces[0]!["id"]!.GetValue<string>();
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages", new
        {
            spaceId, parentId = (string?)null, title = "Export", markdown = "private",
            contentJson = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}"""
        }), HttpStatusCode.OK);
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put,
            $"/api/admin/users/{user["id"]!.GetValue<string>()}/api-access",
            new { canIssueTokens = true, expectedVersion = (string?)null }), HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var token = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/profile/api-tokens",
            new { name = "Export", scopes = new[] { "read" }, expiresInDays = 1 }), HttpStatusCode.Created);
        using var api = site.Client();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["secret"]!.GetValue<string>());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        barrier.Armed = true;
        var download = api.GetAsync($"/api/v1/pages/{page["id"]!.GetValue<string>()}/export", deadline.Token);
        try
        {
            await barrier.Entered.Task.WaitAsync(deadline.Token);
            if (!expire)
            {
                Assert.Equal(1, await site.ExecuteAsync("UPDATE \"PersonalApiToken\" SET \"ActiveTo\"=clock_timestamp() WHERE \"ActiveTo\" IS NULL"));
            }
        }
        finally
        {
            barrier.Resume.TrySetResult();
        }
        using var response = await download;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(Directory.GetFiles(site.ExportPath));
    }

    private sealed class AuthorizationBarrier(bool expire)
    {
        public bool Armed { get; set; }
        public bool Expire { get; } = expire;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PausedContentAccess(IContentAccess inner, NHibernate.ISession session, AuthorizationBarrier barrier) : IContentAccess
    {
        public Task<PageAuthorization> RequirePageAsync(Page page, AccessOperation operation, CancellationToken ct) => inner.RequirePageAsync(page, operation, ct);

        public Task<PageAuthorization> AuthorizePageAsync(Page page, AccessOperation operation, CancellationToken ct) => inner.AuthorizePageAsync(page, operation, ct);

        public Task<IReadOnlyList<PageAuthorization>> AuthorizePagesAsync(IReadOnlyList<Page> pages, AccessOperation operation, CancellationToken ct) =>
            inner.AuthorizePagesAsync(pages, operation, ct);

        public Task<UserAccount> LockActorInSpacesAsync(IReadOnlyCollection<Guid> spaceIds, CancellationToken ct) => inner.LockActorInSpacesAsync(spaceIds, ct);

        public Task<SpaceRole> RequireSpaceAsync(Guid spaceId, SpaceRole required, CancellationToken ct) => inner.RequireSpaceAsync(spaceId, required, ct);

        /// <summary>The build is lock-free, so the first locked actor after arming is the revalidation; pause before its page and final checks.</summary>
        public async Task<UserAccount> LockActorAsync(Guid? spaceId, CancellationToken ct)
        {
            var actor = await inner.LockActorAsync(spaceId, ct);
            if (barrier.Armed)
            {
                barrier.Armed = false;
                barrier.Entered.TrySetResult();
                await barrier.Resume.Task.WaitAsync(ct);
                if (barrier.Expire)
                {
                    // Only this disposable schema/transaction advances the database clock. The credential
                    // remains unchanged, and transaction now() stays before its expiry.
                    await session.CreateSQLQuery("CREATE FUNCTION statement_timestamp() RETURNS timestamptz LANGUAGE SQL STABLE AS $$ SELECT pg_catalog.statement_timestamp() + interval '2 days' $$").ExecuteUpdateAsync(ct);
                    await session.CreateSQLQuery("SELECT set_config('search_path', quote_ident(current_schema()) || ',pg_catalog,public', true)").UniqueResultAsync<string>(ct);
                }
            }
            return actor;
        }
    }
}
