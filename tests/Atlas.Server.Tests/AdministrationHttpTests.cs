using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Client;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Features.Search.Contracts;
using Xunit;
using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Recovery;

using Atlas.Server.Features.Administration.Contracts;
namespace Atlas.Server.Tests;

public sealed class AdministrationHttpTests
{
    [PostgreSqlFact]
    public async Task Space_creation_assigns_optional_groups_atomically_and_preserves_private_owner_access()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_http_");
        using var admin = site.Client();
        var ownerId = (await site.CompleteSetupAsync(admin))["id"]!.GetValue<Guid>();
        // The group name is unique on the shared installation; its role is what the group is granted.
        var groups = new List<(GroupSummary Group, string Role)>();
        foreach (var role in new[] { "Reader", "Writer", "Admin" })
            groups.Add((await Read<GroupSummary>(await Send(admin, HttpMethod.Post, "/api/admin/groups", new { name = TestLogin.For(role) }), HttpStatusCode.OK), role));
        var created = await Read<SpaceResponse>(await Send(admin, HttpMethod.Post, "/api/spaces", new
        {
            name = TestLogin.For("Shared space"), icon = "compass", color = "green",
            groups = groups.Select(entry => new { groupId = entry.Group.Id, role = entry.Role })
        }), HttpStatusCode.OK);
        var access = (await admin.GetFromJsonAsync<SpaceAccessSummary>($"/api/spaces/{created.Id}/access"))!;
        Assert.Equal(4, access.Grants.Count);
        Assert.Equal("Admin", Assert.Single(access.Grants, grant => grant.UserId == ownerId).Role);
        foreach (var (group, role) in groups)
            Assert.Equal(role, Assert.Single(access.Grants, grant => grant.GroupId == group.Id).Role);

        var privateSpace = await Read<SpaceResponse>(await Send(admin, HttpMethod.Post, "/api/spaces", new { name = TestLogin.For("Private space") }), HttpStatusCode.OK);
        var privateAccess = (await admin.GetFromJsonAsync<SpaceAccessSummary>($"/api/spaces/{privateSpace.Id}/access"))!;
        Assert.Equal(ownerId, Assert.Single(privateAccess.Grants).UserId);

        var rolledBack = TestLogin.For("Must roll back");
        var invalidSelections = new object[]
        {
            new[] { new { groupId = groups[0].Group.Id, role = "Reader" }, new { groupId = Guid.NewGuid(), role = "Writer" } },
            new[] { new { groupId = groups[0].Group.Id, role = "Reader" }, new { groupId = groups[0].Group.Id, role = "Admin" } },
            new[] { new { groupId = groups[0].Group.Id, role = "WikiAdmin" } },
            Enumerable.Range(0, 101).Select(_ => new { groupId = Guid.NewGuid(), role = "Reader" }).ToArray(),
            new object?[] { null }
        };
        foreach (var selection in invalidSelections)
        {
            var rejected = await Send(admin, HttpMethod.Post, "/api/spaces", new { name = rolledBack, groups = selection });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            // By its unique name in the table: the administrator's list holds only the first 200 spaces of the shared site.
            Assert.Equal(0L, await site.ScalarAsync("SELECT count(*) FROM \"Space\" WHERE \"Name\"=@name", ("name", rolledBack)));
        }

        var spaceAdminLogin = TestLogin.For("spaceadmin");
        var invitation = await Read<CreatedBuiltinUser>(await Send(admin, HttpMethod.Post, "/api/admin/users",
            new { username = spaceAdminLogin, displayName = "Space admin", isWikiAdmin = false }), HttpStatusCode.Created);
        using var localAdmin = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(localAdmin, HttpMethod.Post, "/api/auth/activate", new { token = invitation.Token, newPassword = site.Password })).StatusCode);
        await Read<UserDto>(await Send(localAdmin, HttpMethod.Post, "/api/auth/login", new { username = spaceAdminLogin, password = site.Password }), HttpStatusCode.OK);
        // Granting Admin needs a fresh sign-in (owner decision 3a), on the old endpoint as on /api/access.
        var grantAdmin = new { userId = invitation.User.Id, role = "Admin", expectedVersion = (string?)null };
        var refused = await Send(admin, HttpMethod.Put, $"/api/spaces/{created.Id}/access", grantAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("reauth_required", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/spaces/{created.Id}/access", grantAdmin,
            await AtlasHttpSite.ReauthAsync(admin, site.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await localAdmin.GetAsync("/api/admin/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(localAdmin, HttpMethod.Post, "/api/spaces",
            new { name = "Privilege escalation", groups = new[] { new { groupId = groups[0].Group.Id, role = "Admin" } } })).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Automatic_bootstrap_and_disk_recovery_work_through_real_CSRF_and_cookie_endpoints()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_http_", settings => settings["Setup:BootstrapToken"] = null);
        using var client = site.Client();
        var setup = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/setup/status");
        Assert.Matches(@"^\d+\.\d+\.\d+", setup.GetProperty("version").GetString());
        var source = setup.GetProperty("tokenSource");
        Assert.Equal("file", source.GetProperty("kind").GetString());
        var tokenPath = source.GetProperty("filePath").GetString()!;
        Assert.Equal(site.Services.GetRequiredService<ServerTokenFiles>().BootstrapPath, tokenPath);
        var bootstrap = (await File.ReadAllTextAsync(tokenPath)).Trim();
        await Read<UserDto>(await Send(client, HttpMethod.Post, "/api/setup/complete", new
        {
            bootstrapToken = bootstrap, workspaceName = "Automatic bootstrap", username = "administrator",
            displayName = "Administrator", password = site.Password
        }), HttpStatusCode.OK);
        var recovery = await client.GetFromJsonAsync<RecoveryStatus>("/api/auth/recovery/status");
        Assert.True(recovery!.Available);
        var file = System.Text.Json.JsonSerializer.Deserialize<RecoveryTokenFile>(await File.ReadAllTextAsync(site.Services.GetRequiredService<ServerTokenFiles>().RecoveryPath))!;
        var newPassword = site.Password + "-recovered";
        using var anonymous = site.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsJsonAsync("/api/auth/recovery", new { token = file.Token, newPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(anonymous, HttpMethod.Post, "/api/auth/recovery", new { token = new string('x', 64), newPassword })).StatusCode);
        var recovered = await Read<UserDto>(await Send(anonymous, HttpMethod.Post, "/api/auth/recovery", new { token = file.Token, newPassword }), HttpStatusCode.OK);
        Assert.Equal("administrator", recovered.Username);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.True((await anonymous.GetFromJsonAsync<UserDto>("/api/auth/me"))!.IsWikiAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(anonymous, HttpMethod.Post, "/api/auth/recovery", new { token = file.Token, newPassword })).StatusCode);
        await Read<UserDto>(await Send(anonymous, HttpMethod.Post, "/api/auth/login", new { username = recovered.Username, password = newPassword }), HttpStatusCode.OK);
        var rotated = System.Text.Json.JsonSerializer.Deserialize<RecoveryTokenFile>(await File.ReadAllTextAsync(site.Services.GetRequiredService<ServerTokenFiles>().RecoveryPath))!;
        Assert.NotEqual(file.Token, rotated.Token);
    }

    [PostgreSqlFact]
    public async Task Oidc_only_installation_hides_builtin_routes_and_requires_a_linked_administrator()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_http_", settings => Apply(settings, OidcConfiguration(builtinEnabled: false)));
        using var client = site.Client();
        var methods = await client.GetFromJsonAsync<AuthMethods>("/api/auth/methods");
        Assert.False(methods!.BuiltinEnabled);
        Assert.Equal("acceptance", Assert.Single(methods.Providers).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Post, "/api/auth/login", new { username = "administrator", password = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Post, "/api/auth/activate", new { token = "unissued-test-token", newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, HttpMethod.Post, "/api/setup/complete", new
        {
            bootstrapToken = site.BootstrapToken, workspaceName = "OIDC-only acceptance",
            username = "administrator", displayName = "Administrator", password = site.Password
        })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/auth/oidc/acceptance/login")).StatusCode);
        var setup = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/setup/status");
        Assert.True(setup.GetProperty("requiresSetup").GetBoolean());
    }

    [PostgreSqlFact]
    public async Task Callback_protocol_errors_never_log_code_state_or_sensitive_exception_payload()
    {
        const string code = "HTTP_CODE_SENTINEL_13dd9591";
        const string state = "HTTP_STATE_SENTINEL_9494f4fe";
        await using var site = await AtlasHttpSite.CreateAsync("atlas_http_",
            settings => Apply(settings, OidcConfiguration(builtinEnabled: true)),
            services => services.AddOpenIddict().AddClient(client =>
                client.AddEventHandler<OpenIddictClientEvents.ProcessAuthenticationContext>(handler =>
                    handler.SetOrder(int.MinValue + 100).UseInlineHandler(_ => throw new InvalidOperationException($"Remote response code={code}; state={state}")))));
        using var client = site.Client();
        var response = await client.GetAsync($"/signin-oidc/acceptance?code={code}&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?authError=external_login_failed", response.Headers.Location!.OriginalString);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("ScaleAtlas.Session=", StringComparison.Ordinal)));
        // This is non-vacuous: the real Program exception boundary must emit its sanitized diagnostic.
        Assert.Contains(site.Logs.Messages, message => message.Contains("the details stay out of the log", StringComparison.Ordinal) && message.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(site.Logs.Messages, message => message.Contains(code, StringComparison.Ordinal) || message.Contains(state, StringComparison.Ordinal) || message.Contains("Remote response", StringComparison.Ordinal));
        Assert.DoesNotContain(code, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task Server_token_signs_in_when_builtin_is_disabled_and_external_provider_is_unavailable()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_http_", settings => Apply(settings, OidcConfiguration(builtinEnabled: false)));
        using var client = site.Client();
        await client.GetAsync("/api/setup/status");
        // Seed an installed workspace independently of the disabled public setup/login routes.
        using (var scope = site.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>().InvokeAsync<UserAccount>(
                new Atlas.Server.Features.Setup.Commands.CompleteSetup(
                    site.BootstrapToken, "Recovery", "administrator", "Administrator", site.Password));
        }
        var status = (await client.GetFromJsonAsync<RecoveryStatus>("/api/auth/recovery/status"))!;
        Assert.True(status.Available);
        Assert.False(status.BuiltinEnabled);
        var token = System.Text.Json.JsonSerializer.Deserialize<RecoveryTokenFile>(await File.ReadAllTextAsync(site.Services.GetRequiredService<ServerTokenFiles>().RecoveryPath))!;
        var restored = await Read<UserDto>(await Send(client, HttpMethod.Post, "/api/auth/recovery",
            new { token = token.Token, newPassword = site.Password + "-recovered" }), HttpStatusCode.OK);
        Assert.True(restored.IsWikiAdmin);
        Assert.True((await client.GetFromJsonAsync<UserDto>("/api/auth/me"))!.IsWikiAdmin);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.False((await client.GetFromJsonAsync<AuthMethods>("/api/auth/methods"))!.BuiltinEnabled);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Post, "/api/auth/login",
            new { username = "administrator", password = site.Password + "-recovered" })).StatusCode);
    }

    private static Dictionary<string, string?> OidcConfiguration(bool builtinEnabled) => new()
    {
        ["Authentication:Builtin:Enabled"] = builtinEnabled.ToString(),
        ["Authentication:OpenIdConnect:PublicOrigin"] = "https://localhost",
        ["Authentication:OpenIdConnect:Providers:0:Id"] = "acceptance",
        ["Authentication:OpenIdConnect:Providers:0:DisplayName"] = "Acceptance provider",
        ["Authentication:OpenIdConnect:Providers:0:Issuer"] = "https://idp.example.invalid/",
        ["Authentication:OpenIdConnect:Providers:0:ClientId"] = "http-acceptance",
        ["Authentication:OpenIdConnect:Providers:0:ClientSecret"] = "test-fixture-only",
        ["Authentication:OpenIdConnect:Providers:0:AllowSignup"] = "false"
    };

    private static void Apply(IDictionary<string, string?> settings, Dictionary<string, string?> overrides)
    {
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Real_cookie_and_CSRF_flow_enforces_roles_activation_reset_and_immediate_access_revocation()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_http_");
        using var administrator = site.Client();
        await site.CompleteSetupAsync(administrator);
        using var anonymous = site.Client();
        var readerLogin = TestLogin.For("reader");
        var invitation = await Read<CreatedBuiltinUser>(await Send(administrator, HttpMethod.Post, "/api/admin/users",
            new { username = readerLogin, displayName = "Reader", isWikiAdmin = false }), HttpStatusCode.Created);
        using var reader = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(reader, HttpMethod.Post, "/api/auth/login", new { username = readerLogin, password = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(reader, HttpMethod.Post, "/api/auth/activate", new { token = invitation.Token, newPassword = site.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(anonymous, HttpMethod.Post, "/api/auth/activate", new { token = invitation.Token, newPassword = site.Password })).StatusCode);
        await Read<UserDto>(await Send(reader, HttpMethod.Post, "/api/auth/login", new { username = readerLogin, password = site.Password }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(reader, HttpMethod.Post, "/api/admin/groups", new { name = "Privilege escalation" })).StatusCode);

        var space = new SpaceResponse(Guid.Parse(await AtlasHttpSite.OwnSpaceAsync(administrator)));
        var group = await Read<GroupSummary>(await Send(administrator, HttpMethod.Post, "/api/admin/groups", new { name = TestLogin.For("Documentation readers") }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(administrator, HttpMethod.Put, $"/api/admin/groups/{group.Id}/members/{invitation.User.Id}",
            new { isMember = true, expectedVersion = (string?)null }, await AtlasHttpSite.ReauthAsync(administrator, site.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(administrator, HttpMethod.Put, $"/api/spaces/{space.Id}/access", new { groupId = group.Id, role = "Reader", expectedVersion = (string?)null })).StatusCode);

        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = await Read<PageDto>(await Send(administrator, HttpMethod.Post, "/api/pages", new { spaceId = space.Id, parentId = (Guid?)null, title = "Secret technical handbook", contentJson = rich, markdown = "Authentication handbook" }), HttpStatusCode.OK);
        await Read<PageDto>(await Send(administrator, HttpMethod.Post, $"/api/pages/{page.Id}/publish", new { expectedVersion = page.Version }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/pages/{page.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(reader, HttpMethod.Post, "/api/pages", new { spaceId = space.Id, parentId = (Guid?)null, title = "Unauthorized write", contentJson = rich, markdown = "" })).StatusCode);
        Assert.Single((await reader.GetFromJsonAsync<SearchResponse>("/api/search?q=handbook"))!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/pages/{page.Id}/history")).StatusCode);
        var history = await administrator.GetFromJsonAsync<PageHistoryList>($"/api/pages/{page.Id}/history?limit=1");
        var original = Assert.Single(history!.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/pages/{page.Id}/history/{original.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(reader, HttpMethod.Post, $"/api/pages/{page.Id}/restore", new { revisionId = original.Id, expectedVersion = page.Version })).StatusCode);
        var revision = await administrator.GetFromJsonAsync<PageHistoryRevision>($"/api/pages/{page.Id}/history/{original.Id}");
        Assert.Equal(rich, revision!.ContentJson);
        // 3.6-D: the reader's private copy revalidates by ETag; the answer is authorized before it is compared.
        var cached = await administrator.GetAsync($"/api/pages/{page.Id}/history/{original.Id}");
        Assert.True(cached.Headers.CacheControl is { Private: true, NoCache: true, Public: false });
        using var revalidation = new HttpRequestMessage(HttpMethod.Get, $"/api/pages/{page.Id}/history/{original.Id}");
        revalidation.Headers.IfNoneMatch.Add(cached.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await administrator.SendAsync(revalidation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await administrator.GetAsync($"/api/pages/{page.Id}/history?limit=0")).StatusCode);
        var changed = await Read<PageDto>(await Send(administrator, HttpMethod.Put, $"/api/pages/{page.Id}", new { title = "Changed draft", contentJson = rich, markdown = "Changed", expectedVersion = page.Version }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(administrator, HttpMethod.Post, $"/api/pages/{page.Id}/restore", new { revisionId = original.Id, expectedVersion = page.Version })).StatusCode);
        var restored = await Read<PageDto>(await Send(administrator, HttpMethod.Post, $"/api/pages/{page.Id}/restore", new { revisionId = original.Id, expectedVersion = changed.Version }), HttpStatusCode.OK);
        Assert.Equal(changed.Version + 1, restored.Version);
        Assert.Equal(page.Title, restored.Title);
        // The restored page is the published one again: the publication carries over (Zahodit změny).
        Assert.Equal(restored.Version, restored.PublishedVersion);
        Assert.Equal(page.Title, (await reader.GetFromJsonAsync<PageDto>($"/api/pages/{page.Id}"))!.Title);

        var access = await administrator.GetFromJsonAsync<SpaceAccessSummary>($"/api/spaces/{space.Id}/access");
        var grant = Assert.Single(access!.Grants, g => g.GroupId == group.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(administrator, HttpMethod.Put, $"/api/spaces/{space.Id}/access", new { groupId = group.Id, role = (string?)null, expectedVersion = grant.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/pages/{page.Id}")).StatusCode);
        Assert.Empty((await reader.GetFromJsonAsync<SearchResponse>("/api/search?q=handbook"))!.Items);

        // A space Admin still cannot administer global accounts or groups.
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(administrator, HttpMethod.Put, $"/api/spaces/{space.Id}/access",
            new { userId = invitation.User.Id, role = "Admin", expectedVersion = (string?)null }, await AtlasHttpSite.ReauthAsync(administrator, site.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/spaces/{space.Id}/access")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/admin/groups")).StatusCode);

        // The list is paged and holds every test's accounts: ask for this one by id.
        var users = await administrator.GetFromJsonAsync<UserSummary[]>($"/api/admin/users?id={invitation.User.Id}");
        var readerSummary = Assert.Single(users!, u => u.Id == invitation.User.Id);
        var reset = await Read<IssuedAccountToken>(await Send(administrator, HttpMethod.Post, $"/api/admin/users/{readerSummary.Id}/reset-password", new { expectedVersion = readerSummary.Version }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync("/api/auth/me")).StatusCode);
        using var recovered = site.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(recovered, HttpMethod.Post, "/api/auth/login", new { username = readerLogin, password = site.Password })).StatusCode);
        var newPassword = site.Password + "-new";
        Assert.Equal(HttpStatusCode.NoContent, (await Send(recovered, HttpMethod.Post, "/api/auth/activate", new { token = reset.Token, newPassword })).StatusCode);
        await Read<UserDto>(await Send(recovered, HttpMethod.Post, "/api/auth/login", new { username = readerLogin, password = newPassword }), HttpStatusCode.OK);

        using var oldSession = site.Client();
        await Read<UserDto>(await Send(oldSession, HttpMethod.Post, "/api/auth/login", new { username = readerLogin, password = newPassword }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(recovered, HttpMethod.Post, "/api/profile/password", new { currentPassword = newPassword, newPassword = site.Password + "-final" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await recovered.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/auth/me")).StatusCode);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object? payload = null) => AtlasHttpSite.SendAsync(client, method, path, payload);

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record SpaceResponse(Guid Id);
    private sealed record AuthMethods(bool BuiltinEnabled, AuthProvider[] Providers);
    private sealed record AuthProvider(string Id);
}
