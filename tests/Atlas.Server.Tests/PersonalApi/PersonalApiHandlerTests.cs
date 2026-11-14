using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Security.Commands;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalApi.Commands;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Queries;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.PersonalApi;

public sealed class PersonalApiHandlerTests
{
    [PostgreSqlFact]
    public async Task Api_access_is_wiki_admin_only_and_reports_the_current_permission()
    {
        await using var f = await PersonalApiFixture.CreateAsync();
        var initial = await f.As<ApiPermissionDto>(f.Admin, new GetApiAccess(f.Owner.Id));
        Assert.False(initial.CanIssueTokens);
        Assert.Null(initial.Version);
        await Denied(403, () => f.As<ApiPermissionDto>(f.Owner, new GetApiAccess(f.Owner.Id)));
        await Denied(404, () => f.As<ApiPermissionDto>(f.Admin, new GetApiAccess(Guid.NewGuid())));
    }

    [PostgreSqlFact]
    public async Task Api_access_versions_every_real_change_and_never_resurrects_old_tokens()
    {
        await using var f = await PersonalApiFixture.CreateAsync();
        var enabled = await f.As<ApiPermissionDto>(f.Admin, new SetApiAccess(f.Owner.Id, new ChangeApiPermission(true, null)));
        Assert.True(enabled.CanIssueTokens);
        Assert.NotNull(enabled.Version);
        // A no-op change keeps the same revision, so the version does not move.
        Assert.Equal(enabled, await f.As<ApiPermissionDto>(f.Admin, new SetApiAccess(f.Owner.Id, new ChangeApiPermission(true, enabled.Version))));
        await Denied(409, () => f.As<ApiPermissionDto>(f.Admin, new SetApiAccess(f.Owner.Id, new ChangeApiPermission(false, null))));
        var token = await f.As<CreatedApiToken>(f.Owner, new IssueApiToken(new CreateApiToken("Integration", ["read"], 30)));
        var disabled = await f.As<ApiPermissionDto>(f.Admin, new SetApiAccess(f.Owner.Id, new ChangeApiPermission(false, enabled.Version)));
        Assert.False(disabled.CanIssueTokens);
        var profile = await f.As<ApiTokenProfile>(f.Owner, new ListApiTokens());
        Assert.NotNull(Assert.Single(profile.Tokens, x => x.Id == token.Token.Id).ActiveTo);
        await Denied(403, () => f.As<ApiPermissionDto>(f.Owner, new SetApiAccess(f.Owner.Id, new ChangeApiPermission(true, disabled.Version))));
    }

    [PostgreSqlFact]
    public async Task Tokens_require_permission_are_validated_bounded_and_revocable_once()
    {
        await using var f = await PersonalApiFixture.CreateAsync();
        await Denied(403, () => f.As<CreatedApiToken>(f.Owner, new IssueApiToken(new CreateApiToken("Denied", ["read"], 30))));
        await f.As<ApiPermissionDto>(f.Admin, new SetApiAccess(f.Owner.Id, new ChangeApiPermission(true, null)));
        await Denied(400, () => f.As<CreatedApiToken>(f.Owner, new IssueApiToken(new CreateApiToken("  ", ["read"], 30))));
        await Denied(400, () => f.As<CreatedApiToken>(f.Owner, new IssueApiToken(new CreateApiToken("Bad days", ["read"], 400))));
        await Denied(400, () => f.As<CreatedApiToken>(f.Owner, new IssueApiToken(new CreateApiToken("Bad scopes", ["write"], 30))));
        await Denied(400, () => f.As<CreatedApiToken>(f.Owner, new IssueApiToken(new CreateApiToken("Duplicate", ["read", "read"], 30))));
        var created = await f.As<CreatedApiToken>(f.Owner, new IssueApiToken(new CreateApiToken("Integration", ["write", "read"], 30)));
        Assert.StartsWith("sat_", created.Secret, StringComparison.Ordinal);
        Assert.Equal(68, created.Secret.Length);
        Assert.Equal(created.Secret[..12], created.Token.Prefix);
        Assert.Equal(new[] { "read", "write" }, created.Token.Scopes);
        Assert.DoesNotContain(created.Secret, created.ToString(), StringComparison.Ordinal);
        var profile = await f.As<ApiTokenProfile>(f.Owner, new ListApiTokens());
        Assert.True(profile.CanIssueTokens);
        Assert.Equal("/api/v1", profile.ApiBaseUrl);
        Assert.Equal(new[] { "read", "write" }, Assert.Single(profile.Tokens).Scopes);
        await f.As(f.Owner, new RevokeApiToken(created.Token.Id));
        var revoked = Assert.Single((await f.As<ApiTokenProfile>(f.Owner, new ListApiTokens())).Tokens);
        Assert.NotNull(revoked.ActiveTo);
        await f.As(f.Owner, new RevokeApiToken(created.Token.Id));
        Assert.Equal(revoked.ActiveTo, Assert.Single((await f.As<ApiTokenProfile>(f.Owner, new ListApiTokens())).Tokens).ActiveTo);
        await Denied(404, () => f.As(f.Outsider, new RevokeApiToken(created.Token.Id)));
    }

    [PostgreSqlFact]
    public async Task Personal_api_lists_only_accessible_spaces_and_pages_with_role_dependent_titles()
    {
        await using var f = await PersonalApiFixture.CreateAsync();
        var page = await f.Create(f.Owner, "Published handbook");
        await f.Pages.PublishAsync(page.Id, new(page.Version), f.Owner, default);
        var draft = await f.Create(f.Owner, "Owner draft");
        var spaces = await f.As<ApiList<ApiSpace>>(f.Owner, new ListApiSpaces(null, null));
        var space = Assert.Single(spaces.Items);
        Assert.Equal(f.SpaceId, space.Id);
        Assert.Equal("Writer", space.Role);
        Assert.Equal(0, spaces.Offset);
        Assert.Equal(50, spaces.Limit);
        Assert.False(spaces.HasMore);
        // A wiki administrator lists every space of the host, this test's among them.
        Assert.Equal("Admin", Assert.Single((await f.As<ApiList<ApiSpace>>(f.Admin, new ListApiSpaces(null, 100))).Items, x => x.Id == f.SpaceId).Role);
        Assert.Empty((await f.As<ApiList<ApiSpace>>(f.Outsider, new ListApiSpaces(null, null))).Items);
        var pages = await f.As<ApiList<ApiPageEntry>>(f.Owner, new ListApiSpacePages(f.SpaceId, null, null));
        Assert.Equal(new[] { page.Id, draft.Id }.Order(), pages.Items.Select(x => x.Id).Order());
        Assert.Contains(pages.Items, x => x.Title == "Owner draft" && !x.IsPublished);
        var firstPage = await f.As<ApiList<ApiPageEntry>>(f.Owner, new ListApiSpacePages(f.SpaceId, 0, 1));
        Assert.True(firstPage.HasMore);
        // The window is validated before the space is resolved, so a bad limit on an unknown space is still 400.
        await Denied(400, () => f.As<ApiList<ApiPageEntry>>(f.Owner, new ListApiSpacePages(Guid.NewGuid(), 0, 101)));
        // TR_SpaceGrant_Immutable allows no UPDATE but the closing one, so a downgrade closes the grant and inserts the new role.
        await f.Execute("""
            UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "UserId"=:user AND "SpaceId"=:space AND "ActiveTo" IS NULL;
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","ActiveFrom")
              VALUES (gen_random_uuid(),:space,:user,1,clock_timestamp());
            """, ("user", f.Owner.Id), ("space", f.SpaceId));
        var readerView = await f.As<ApiList<ApiPageEntry>>(f.Owner, new ListApiSpacePages(f.SpaceId, null, null));
        Assert.Equal(page.Id, Assert.Single(readerView.Items).Id);
        Assert.Equal("Published handbook", Assert.Single(readerView.Items).Title);
        await Denied(404, () => f.As<ApiList<ApiPageEntry>>(f.Outsider, new ListApiSpacePages(f.SpaceId, null, null)));
        await Denied(400, () => f.As<ApiList<ApiSpace>>(f.Owner, new ListApiSpaces(-1, null)));
        await Denied(400, () => f.As<ApiList<ApiPageEntry>>(f.Owner, new ListApiSpacePages(f.SpaceId, 0, 101)));
        // Both lists re-read the account under their own lock, so a rotated stamp stops them before any row.
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='rotated' WHERE \"Id\"=:user", ("user", f.Owner.Id));
        await Denied(401, () => f.As<ApiList<ApiSpace>>(f.Owner, new ListApiSpaces(null, null)));
        await Denied(401, () => f.As<ApiList<ApiPageEntry>>(f.Owner, new ListApiSpacePages(f.SpaceId, null, null)));
    }

    [PostgreSqlFact]
    public async Task Api_actor_lock_rereads_the_account_even_when_the_request_actor_came_from_the_same_session()
    {
        var accessor = new FixedHttpContextAccessor();
        // Private: the host's actor is the production resolver over this test's one signed-in request (a fake for the whole host).
        await using var host = await AtlasTestHost.CreateAsync("atlas_personal_api_", services =>
        {
            services.AddScoped<PersonalApiActor>();
            services.AddSingleton<IHttpContextAccessor>(accessor);
            // The production actor resolver, so the request actor and the locked row are the same instance.
            services.AddScoped<ICurrentActor, HttpCurrentActor>();
        });
        var user = await host.SeedUserAsync("api-owner");
        accessor.SignIn(user.Id, user.SecurityStamp);
        var error = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<Guid>(new ProbeStalePersonalApiActor(user.Id)));
        Assert.Equal(401, error.Status);
        Assert.Equal("Přihlášení vypršelo.", error.Message);
    }

    private static async Task Denied(int status, Func<Task> action)
        => Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);

    [PostgreSqlFact]
    public async Task Logout_winning_the_account_lock_prevents_waiting_token_issuance()
    {
        var accessor = new FixedHttpContextAccessor();
        // Private: the host's actor is the production resolver over this test's one signed-in request (a fake for the whole host).
        await using var host = await AtlasTestHost.CreateAsync("atlas_api_logout_", services =>
        {
            services.AddScoped<PersonalApiActor>();
            services.AddSingleton<IHttpContextAccessor>(accessor);
            services.AddScoped<ICurrentActor, HttpCurrentActor>();
        });
        var user = await host.SeedUserAsync("api-owner", isWikiAdmin: true);
        accessor.SignIn(user.Id, user.SecurityStamp);
        ((ClaimsIdentity)accessor.HttpContext!.User.Identity!).AddClaim(new Claim("atlas_session", Guid.NewGuid().ToString()));
        await host.InvokeAsync<ApiPermissionDto>(new SetApiAccess(user.Id, new ChangeApiPermission(true, null)));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var logoutScope = host.Services.CreateScope();
        var logoutSession = logoutScope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        using var logout = logoutSession.BeginTransaction();
        var logoutPid = await logoutSession.CreateSQLQuery("SELECT pg_backend_pid()").UniqueResultAsync<int>(deadline.Token);
        await ActivatorUtilities.CreateInstance<EndReauthWindowHandler>(logoutScope.ServiceProvider)
            .Handle(new EndReauthWindow(), deadline.Token);
        var issuing = host.InvokeAsync<CreatedApiToken>(new IssueApiToken(new CreateApiToken("Racing logout", ["read"], 30)), deadline.Token);
        try
        {
            await using var observer = new NpgsqlConnection(host.ConnectionString);
            await observer.OpenAsync(deadline.Token);
            await using var waiting = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @blocker = ANY(pg_blocking_pids(pid)))", observer);
            waiting.Parameters.AddWithValue("blocker", logoutPid);
            while (!Convert.ToBoolean(await waiting.ExecuteScalarAsync(deadline.Token))) await Task.Yield();
            await logout.CommitAsync(deadline.Token);
        }
        finally
        {
            if (logout.IsActive) await logout.RollbackAsync(CancellationToken.None);
        }

        Assert.Equal(401, (await Assert.ThrowsAsync<ApiException>(() => issuing)).Status);
        using var verify = host.OpenSession();
        Assert.Equal(0, await verify.QueryOver<Atlas.Server.Features.PersonalApi.Domain.PersonalApiToken>().RowCountAsync());
    }
}
