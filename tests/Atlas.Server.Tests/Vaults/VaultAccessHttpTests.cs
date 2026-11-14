using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Vaults;

public sealed class VaultAccessHttpTests
{
    private const string Fixture = "argon2d-aes-gzip.kdbx";

    /// <summary>
    /// Canary for the access API (WP4 V3): the space Admin grants, explains and revokes vault and secret access over HTTP and reads
    /// security/me. Every success is uncompressed, and no body or log line carries the master password, an entry value
    /// or a Data Protection payload.
    /// </summary>
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Access_api_never_returns_or_logs_secret_values()
    {
        var folder = Path.Combine(Path.GetTempPath(), "atlas-vault-access-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await using var site = await AtlasHttpSite.CreateAsync("atlas_vault_access_http_", settings => settings["Vaults:RefreshSeconds"] = "0", shared: true);
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var bodies = new List<string>();
        async Task<JsonNode> Call(HttpMethod method, string path, object? payload, HttpStatusCode expected, string? reauth = null)
        {
            var response = method == HttpMethod.Get ? await admin.GetAsync(path) : await AtlasHttpSite.SendAsync(admin, method, path, payload, reauth);
            var body = await response.Content.ReadAsStringAsync();
            bodies.Add(body);
            Assert.True(expected == response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {body}");
            if (response.IsSuccessStatusCode)
            {
                Assert.Null(response.Content.Headers.ContentEncoding.FirstOrDefault());
            }
            return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!;
        }

        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var location = await Call(HttpMethod.Post, "/api/vault-locations", new { name = TestLogin.For("Místní"), kind = "local", root = folder }, HttpStatusCode.OK);
        File.WriteAllBytes(Path.Combine(folder, "provoz.kdbx"), Read(Fixture));
        var master = Password(Fixture);
        var vault = await Call(HttpMethod.Post, "/api/vaults/attach",
            new { spaceId, locationId = location["id"]!.GetValue<string>(), name = "Provoz", fileName = "provoz.kdbx", password = master }, HttpStatusCode.OK);
        var vaultId = vault["id"]!.GetValue<string>();
        Assert.Equal("manage", vault["level"]!.GetValue<string>());

        var me = await Call(HttpMethod.Get, "/api/security/me", null, HttpStatusCode.OK);
        Assert.True(me["globalSecurityAdmin"]!.GetValue<bool>());
        Assert.True(me["managesAllVaults"]!.GetValue<bool>());
        Assert.Empty(me["vaultManagerSpaces"]!.AsArray());
        Assert.Equal(("supported", "password"), (me["freshAuth"]!.GetValue<string>(), me["freshAuthMethod"]!.GetValue<string>()));
        // A wiki administrator sees the vaults of every test on the shared site: this one's by its id.
        Assert.Equal("manage", me["vaults"]!.AsArray().Single(v => v!["vaultId"]!.GetValue<string>() == vaultId)!["level"]!.GetValue<string>());

        var userId = Guid.NewGuid();
        var login = TestLogin.For("operator");
        await site.ExecuteAsync("INSERT INTO \"UserAccount\"(\"Id\",\"Username\",\"DisplayName\",\"IsWikiAdmin\",\"IsActive\",\"SecurityStamp\") VALUES(@id,@login,'Operátor',false,true,'stamp')",
            ("id", userId), ("login", login));
        var candidates = await Call(HttpMethod.Get, $"/api/vaults/{vaultId}/grant-candidates?q={login}", null, HttpStatusCode.OK);
        Assert.Equal(userId.ToString(), candidates.AsArray().Single(c => c!["kind"]!.GetValue<string>() == "user")!["id"]!.GetValue<string>());
        await Call(HttpMethod.Put, $"/api/vaults/{vaultId}/grants", new { userId, level = "write" }, HttpStatusCode.BadRequest);
        // WP4 V4: a grant change needs a fresh re-authentication; a proof for this vault is used once, after which the session's
        // ten-minute window (owner decision 28. 9.) covers further changes without one.
        var denied = await Call(HttpMethod.Put, $"/api/vaults/{vaultId}/grants", new { userId, level = "read" }, HttpStatusCode.Forbidden);
        Assert.Equal("reauth_required", denied["code"]!.GetValue<string>());
        var vaultProof = await AtlasHttpSite.ReauthAsync(admin, site.Password, vaultId: Guid.Parse(vaultId));
        await Call(HttpMethod.Put, $"/api/vaults/{vaultId}/grants", new { userId, level = "read" }, HttpStatusCode.NoContent, vaultProof);
        // Past the guard in the window, this change without its CAS version is refused as a conflict, not for re-authentication.
        await Call(HttpMethod.Put, $"/api/vaults/{vaultId}/grants", new { userId, level = "manage" }, HttpStatusCode.Conflict);
        var access = await Call(HttpMethod.Get, $"/api/vaults/{vaultId}/access", null, HttpStatusCode.OK);
        Assert.True(access["canManage"]!.GetValue<bool>());
        var person = access["people"]!.AsArray().Single(p => p!["userId"]!.GetValue<string>() == userId.ToString())!;
        var version = person["sources"]![0]!["version"]!.GetValue<string>();
        Assert.Equal("resource_grant.added", access["lastChange"]!["action"]!.GetValue<string>());

        var secrets = await Call(HttpMethod.Get, $"/api/vaults/{vaultId}/secrets", null, HttpStatusCode.OK);
        Assert.Equal(4, secrets.AsArray().Count);
        var secretId = secrets[0]!["secretId"]!.GetValue<string>();
        await Call(HttpMethod.Put, $"/api/secrets/{secretId}/grants", new { userId, level = "manage" }, HttpStatusCode.NoContent,
            await AtlasHttpSite.ReauthAsync(admin, site.Password, secretId: Guid.Parse(secretId)));
        var secretAccess = await Call(HttpMethod.Get, $"/api/secrets/{secretId}/access", null, HttpStatusCode.OK);
        var secretVersion = secretAccess["people"]!.AsArray().Single(p => p!["userId"]!.GetValue<string>() == userId.ToString())!["sources"]!.AsArray()
            .Single(s => s!["kind"]!.GetValue<string>() == "secret")!["version"]!.GetValue<string>();
        await Call(HttpMethod.Delete, $"/api/secrets/{secretId}/grants?userId={userId}&expectedVersion={Uri.EscapeDataString(secretVersion)}", null, HttpStatusCode.NoContent,
            await AtlasHttpSite.ReauthAsync(admin, site.Password, secretId: Guid.Parse(secretId)));
        // A refused change leaves the proof unconsumed: the conflict rolls it back, the retry uses it.
        vaultProof = await AtlasHttpSite.ReauthAsync(admin, site.Password, vaultId: Guid.Parse(vaultId));
        await Call(HttpMethod.Delete, $"/api/vaults/{vaultId}/grants?userId={userId}&expectedVersion={Uri.EscapeDataString("stale")}", null, HttpStatusCode.Conflict, vaultProof);
        await Call(HttpMethod.Delete, $"/api/vaults/{vaultId}/grants?userId={userId}&expectedVersion={Uri.EscapeDataString(version)}", null, HttpStatusCode.NoContent, vaultProof);
        await Call(HttpMethod.Get, $"/api/vaults/{Guid.NewGuid()}/access", null, HttpStatusCode.NotFound);
        await Call(HttpMethod.Get, $"/api/secrets/{Guid.NewGuid()}/access", null, HttpStatusCode.NotFound);
        await Call(HttpMethod.Get, "/api/vaults", null, HttpStatusCode.OK);
        // The vault's audit tab reads the access audit narrowed to the grants of this vault and its secrets, newest first.
        var audit = await Call(HttpMethod.Get, $"/api/admin/access-audit?vaultId={vaultId}", null, HttpStatusCode.OK);
        Assert.Equal(["resource_grant.removed", "resource_grant.removed", "resource_grant.added", "resource_grant.added"],
            audit["items"]!.AsArray().Select(item => item!["action"]!.GetValue<string>()).ToArray());
        Assert.All(audit["items"]!.AsArray(), item => Assert.Equal(userId.ToString(), item!["userId"]!.GetValue<string>()));
        Assert.Empty((await Call(HttpMethod.Get, $"/api/admin/access-audit?vaultId={Guid.NewGuid()}", null, HttpStatusCode.OK))["items"]!.AsArray());
        Assert.Equal(0L, await site.ScalarAsync("SELECT count(*) FROM \"ResourceGrant\" WHERE \"UserId\"=@id AND \"ActiveTo\" IS NULL", ("id", userId)));

        var canaries = VaultHttpTests.Canaries(Fixture).Append(master).Append("CfDJ8").ToArray();
        foreach (var body in bodies)
        {
            foreach (var canary in canaries)
            {
                Assert.DoesNotContain(canary, body, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("protected", body, StringComparison.OrdinalIgnoreCase);
        }
        var logs = string.Join("\n", site.Logs.Messages);
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, logs, StringComparison.Ordinal);
        }
        Directory.Delete(folder, true);
    }
}
