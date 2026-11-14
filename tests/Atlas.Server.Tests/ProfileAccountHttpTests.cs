using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class ProfileAccountHttpTests
{
    private const string AccountPath = "/api/profile/account";

    [PostgreSqlFact]
    public async Task Reader_can_edit_own_account_with_CAS_without_changing_identity_or_other_users()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_profile_http_");
        using var admin = site.Client();
        var owner = await Setup(site, admin);
        var readerLogin = TestLogin.For("reader");
        var invitation = await Json(await Send(admin, HttpMethod.Post, "/api/admin/users",
            new { username = readerLogin, displayName = "Reader", isWikiAdmin = false }), HttpStatusCode.Created);
        var readerId = invitation["user"]!["id"]!.GetValue<Guid>();
        using var reader = site.Client();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(reader, HttpMethod.Post, "/api/auth/activate",
            new { token = invitation["token"]!.GetValue<string>(), newPassword = site.Password })).StatusCode);
        await Login(site, reader, readerLogin);
        var before = await Account(reader);
        Assert.Null(before["email"]);
        Assert.Equal("builtin", before["source"]!.GetValue<string>());
        Assert.True(before["canEditDisplayName"]!.GetValue<bool>());
        Assert.True(before["canEditEmail"]!.GetValue<bool>());
        var request = new { displayName = "  Reader updated  ", email = " reader@example.com ", expectedVersion = Version(before), userId = owner["id"]!.GetValue<string>() };
        var saved = await Json(await Send(reader, HttpMethod.Put, AccountPath, request), HttpStatusCode.OK);
        Assert.Equal("Reader updated", saved["displayName"]!.GetValue<string>());
        Assert.Equal("reader@example.com", saved["email"]!.GetValue<string>());
        Assert.NotEqual(Version(before), Version(saved));
        Assert.Equal("Administrator", (await Account(admin))["displayName"]!.GetValue<string>());
        Assert.Equal(readerLogin, (await Json(await reader.GetAsync("/api/auth/me"), HttpStatusCode.OK))["username"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(reader, HttpMethod.Put, AccountPath, request)).StatusCode);
        var unchanged = await Json(await Send(reader, HttpMethod.Put, AccountPath,
            new { displayName = "Reader updated", email = "reader@example.com", expectedVersion = Version(saved) }), HttpStatusCode.OK);
        Assert.Equal(Version(saved), Version(unchanged));
        foreach (var email in new[] { "invalid", "Name <a@example.com>", "a@example.com\r\nBcc: b@example.com", new string('a', 250) + "@example.com" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(reader, HttpMethod.Put, AccountPath,
                new { displayName = "Reader updated", email, expectedVersion = Version(saved) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(reader, HttpMethod.Put, AccountPath,
            new { displayName = " ", email = (string?)null, expectedVersion = Version(saved) })).StatusCode);

        // The same unverified contact is allowed on another account, and cannot link identities.
        var adminBefore = await Account(admin);
        await Json(await Send(admin, HttpMethod.Put, AccountPath,
            new { displayName = "Administrator", email = "reader@example.com", expectedVersion = Version(adminBefore) }), HttpStatusCode.OK);
        Assert.Equal(0L, await site.ScalarAsync("""SELECT count(*) FROM "ExternalIdentity" WHERE "UserId" IN (@reader, @owner)""", ("reader", readerId), ("owner", owner["id"]!.GetValue<Guid>())));
        Assert.True(Convert.ToInt64(await site.ScalarAsync("""SELECT count(*) FROM "UserAccountRevision" WHERE "UserId"=@reader AND "DisplayName"='Reader' AND "ActiveTo" IS NOT NULL AND "Email" IS NULL""", ("reader", readerId))) > 0);
        Assert.Equal(1L, await site.ScalarAsync("""SELECT count(*) FROM "UserAccountRevision" WHERE "UserId"=@reader AND "DisplayName"='Reader updated' AND "ActiveTo" IS NULL AND "Email"='reader@example.com' AND "ChangedBy"="UserId" """, ("reader", readerId)));

        await Json(await Send(admin, HttpMethod.Put, $"/api/admin/users/{owner["id"]!.GetValue<string>()}/api-access",
            new { canIssueTokens = true, expectedVersion = (string?)null }), HttpStatusCode.OK);
        await AtlasHttpSite.ReauthAsync(admin, site.Password);
        var token = await Json(await Send(admin, HttpMethod.Post, "/api/profile/api-tokens",
            new { name = "Profile boundary", scopes = new[] { "read", "write" }, expiresInDays = 1 }), HttpStatusCode.Created);
        using var bearer = site.Client();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token["secret"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync(AccountPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bearer.GetAsync("/api/v1/profile/account")).StatusCode);

        // The list is paged and holds every test's accounts: ask for this one by id.
        var users = await Json(await admin.GetAsync($"/api/admin/users?id={readerId}"), HttpStatusCode.OK);
        var account = users.AsArray().Single(x => x!["username"]!.GetValue<string>() == readerLogin)!;
        await Json(await Send(admin, HttpMethod.Put, $"/api/admin/users/{account["id"]!.GetValue<string>()}",
            new { displayName = "Reader updated", isActive = false, isWikiAdmin = false, expectedVersion = Version(account) }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync(AccountPath)).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Ai_preferences_bind_to_the_own_account_with_CAS_and_reach_features_on_the_current_user()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_profile_http_");
        using var admin = site.Client();
        await Setup(site, admin);
        const string path = "/api/me/ai-preferences";
        var before = await Json(await admin.GetAsync(path), HttpStatusCode.OK);
        Assert.Equal("on", before["editorSuggestions"]!.GetValue<string>());
        Assert.Empty(before["disabledSuggestionTypes"]!.AsArray());
        Assert.Null(before["version"]);
        Assert.Equal("auto", before["searchAnswer"]!.GetValue<string>());
        Assert.Equal("context", before["assistantScope"]!.GetValue<string>());
        var body = new { editorSuggestions = "off", disabledSuggestionTypes = new[] { "issue" }, expectedVersion = (string?)null, userId = Guid.NewGuid() };
        var saved = await Json(await Send(admin, HttpMethod.Put, path, body), HttpStatusCode.OK);
        Assert.Equal("off", saved["editorSuggestions"]!.GetValue<string>());
        Assert.Equal("issue", saved["disabledSuggestionTypes"]![0]!.GetValue<string>());
        Assert.Equal("auto", saved["searchAnswer"]!.GetValue<string>());
        var answers = await Json(await Send(admin, HttpMethod.Put, path,
            new { editorSuggestions = "off", disabledSuggestionTypes = new[] { "issue" }, expectedVersion = Version(saved), searchAnswer = "ask", assistantScope = "wiki" }), HttpStatusCode.OK);
        Assert.Equal("ask", answers["searchAnswer"]!.GetValue<string>());
        Assert.Equal("wiki", (await Json(await admin.GetAsync(path), HttpStatusCode.OK))["assistantScope"]!.GetValue<string>());
        saved = answers;
        var me = await Json(await admin.GetAsync("/api/auth/me"), HttpStatusCode.OK);
        Assert.False(me["features"]!["editorAssist"]!.GetValue<bool>());
        Assert.Equal("issue", me["features"]!["disabledSuggestionTypes"]![0]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(admin, HttpMethod.Put, path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(admin, HttpMethod.Put, path,
            new { editorSuggestions = "off", disabledSuggestionTypes = Array.Empty<string>(), expectedVersion = Version(saved), searchAnswer = "bogus" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(admin, HttpMethod.Put, path,
            new { editorSuggestions = "bogus", disabledSuggestionTypes = Array.Empty<string>(), expectedVersion = Version(saved) })).StatusCode);
    }

    [PostgreSqlFact]
    public async Task Concurrent_profile_writes_have_one_winner_and_clear_email_without_losing_history()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_profile_http_");
        using var first = site.Client();
        var owner = await Setup(site, first);
        var ownerId = owner["id"]!.GetValue<Guid>();
        using var second = site.Client();
        await Login(site, second, owner["username"]!.GetValue<string>());
        var current = await Account(first);
        var responses = await Task.WhenAll(
            Send(first, HttpMethod.Put, AccountPath, new { displayName = "First", email = "first@example.com", expectedVersion = Version(current) }),
            Send(second, HttpMethod.Put, AccountPath, new { displayName = "Second", email = "second@example.com", expectedVersion = Version(current) }));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var winner = await Account(first);
        var cleared = await Json(await Send(first, HttpMethod.Put, AccountPath,
            new { displayName = winner["displayName"]!.GetValue<string>(), email = " ", expectedVersion = Version(winner) }), HttpStatusCode.OK);
        Assert.Null(cleared["email"]);
        Assert.Equal(3L, await site.ScalarAsync("""SELECT count(*) FROM "UserAccountRevision" WHERE "UserId"=@id""", ("id", ownerId)));
        Assert.Equal(2L, await site.ScalarAsync("""SELECT count(*) FROM "UserAccountRevision" a JOIN "UserAccountRevision" b ON a."UserId"=b."UserId" AND a."ActiveTo"=b."ActiveFrom" WHERE a."UserId"=@id""", ("id", ownerId)));
    }

    [PostgreSqlFact]
    public async Task Password_refresh_preserves_current_authentication_while_latest_signin_can_be_another_device()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_profile_http_");
        using var original = site.Client();
        var ownerAccount = await Setup(site, original);
        var ownerId = ownerAccount["id"]!.GetValue<Guid>();
        var before = await Account(original);
        Assert.NotNull(before["currentSessionAuthenticatedAt"]);
        Assert.Equal("builtin", before["currentSessionAuthenticationMethod"]!.GetValue<string>());
        using var other = site.Client();
        await Login(site, other, ownerAccount["username"]!.GetValue<string>());
        var afterLogin = await Account(original);
        Assert.Equal(before["currentSessionAuthenticatedAt"]!.GetValue<string>(), afterLogin["currentSessionAuthenticatedAt"]!.GetValue<string>());
        Assert.NotEqual(before["lastSignInAt"]!.GetValue<string>(), afterLogin["lastSignInAt"]!.GetValue<string>());
        Assert.Equal(2L, await site.ScalarAsync("""SELECT count(*) FROM "UserSignIn" WHERE "UserId"=@id""", ("id", ownerId)));
        Assert.Equal(HttpStatusCode.NoContent, (await Send(original, HttpMethod.Post, "/api/profile/password",
            new { currentPassword = site.Password, newPassword = site.Password + "-changed" })).StatusCode);
        var refreshed = await Account(original);
        Assert.Equal(before["currentSessionAuthenticatedAt"]!.GetValue<string>(), refreshed["currentSessionAuthenticatedAt"]!.GetValue<string>());
        Assert.Equal(before["currentSessionAuthenticationMethod"]!.GetValue<string>(), refreshed["currentSessionAuthenticationMethod"]!.GetValue<string>());
        Assert.Equal(afterLogin["lastSignInAt"]!.GetValue<string>(), refreshed["lastSignInAt"]!.GetValue<string>());
        Assert.Equal(2L, await site.ScalarAsync("""SELECT count(*) FROM "UserSignIn" WHERE "UserId"=@id""", ("id", ownerId)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync(AccountPath)).StatusCode);
    }

    [PostgreSqlFact]
    public async Task External_and_linked_accounts_keep_local_editability_and_legacy_history_is_not_invented()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_profile_http_");
        using var client = site.Client();
        var ownerId = (await Setup(site, client))["id"]!.GetValue<Guid>();
        var subject = TestLogin.For("subject");
        await site.ScalarAsync("""
            INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
            VALUES (gen_random_uuid(),@id,'https://profile-idp.example.invalid/',@subject,clock_timestamp())
            """, ("id", ownerId), ("subject", subject));
        Assert.Equal("linked", (await Account(client))["source"]!.GetValue<string>());
        await site.ScalarAsync("""DELETE FROM "BuiltinCredential" WHERE "UserId"=@id; DELETE FROM "UserSignIn" WHERE "UserId"=@id""", ("id", ownerId));
        var external = await Account(client);
        Assert.Equal("external", external["source"]!.GetValue<string>());
        Assert.True(external["canEditDisplayName"]!.GetValue<bool>());
        Assert.True(external["canEditEmail"]!.GetValue<bool>());
        Assert.Null(external["lastSignInAt"]);
        Assert.Null(external["lastSignInMethod"]);
        var saved = await Json(await Send(client, HttpMethod.Put, AccountPath,
            new { displayName = "Local name", email = "contact@example.com", expectedVersion = Version(external) }), HttpStatusCode.OK);
        Assert.Equal("Local name", saved["displayName"]!.GetValue<string>());
        Assert.Equal("contact@example.com", saved["email"]!.GetValue<string>());
        Assert.Equal(subject, await site.ScalarAsync("""SELECT "Subject" FROM "ExternalIdentity" WHERE "UserId"=@id AND "ActiveTo" IS NULL""", ("id", ownerId)));
        Assert.Equal(1L, await site.ScalarAsync("""SELECT count(*) FROM "ExternalIdentity" WHERE "UserId"=@id""", ("id", ownerId)));
    }

    private static string Version(JsonNode value) => value["version"]!.GetValue<string>();
    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, object value) => AtlasHttpSite.SendAsync(client, method, path, value);
    private static async Task<JsonNode> Account(HttpClient client) => await Json(await client.GetAsync(AccountPath), HttpStatusCode.OK);
    private static Task<JsonNode> Setup(AtlasHttpSite site, HttpClient client) => site.CompleteSetupAsync(client, "Profile tests");
    private static async Task Login(AtlasHttpSite site, HttpClient client, string username) => await Json(await Send(client, HttpMethod.Post, "/api/auth/login", new { username, password = site.Password }), HttpStatusCode.OK);
    private static async Task<JsonNode> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }
}
