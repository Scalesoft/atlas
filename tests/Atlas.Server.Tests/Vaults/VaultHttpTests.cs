using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Vaults;

public sealed class VaultHttpTests
{
    private const string Fixture = "argon2d-aes-gzip.kdbx";

    /// <summary>
    /// Canary: the whole administrator journey over HTTP, then every response body and every log line is searched
    /// for the master password, the entry values of the KeePass file and Data Protection payloads. The generated
    /// password of a new vault is the one value that may appear, and only in its creation response.
    /// </summary>
    [PostgreSqlFact]
    public async Task Vault_api_never_returns_or_logs_secret_values_passwords_or_protected_payloads()
    {
        var folder = Path.Combine(Path.GetTempPath(), "atlas-vault-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await using var site = await AtlasHttpSite.CreateAsync("atlas_vault_http_", settings => settings["Vaults:RefreshSeconds"] = "0", shared: true);
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var bodies = new List<string>();
        async Task<JsonNode> Call(HttpMethod method, string path, object? payload, HttpStatusCode expected)
        {
            var response = method == HttpMethod.Get ? await admin.GetAsync(path) : await AtlasHttpSite.SendAsync(admin, method, path, payload);
            var body = await response.Content.ReadAsStringAsync();
            bodies.Add(body);
            Assert.True(expected == response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {body}");
            if (response.IsSuccessStatusCode && path.StartsWith("/api/vault", StringComparison.Ordinal))
            {
                Assert.Null(response.Content.Headers.ContentEncoding.FirstOrDefault());
            }
            return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!;
        }

        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var test = await Call(HttpMethod.Post, "/api/vault-locations/test", new { kind = "local", root = folder }, HttpStatusCode.OK);
        Assert.StartsWith("Přístup ověřen", test["message"]!.GetValue<string>(), StringComparison.Ordinal);
        var location = await Call(HttpMethod.Post, "/api/vault-locations", new { name = TestLogin.For("Místní"), kind = "local", root = folder }, HttpStatusCode.OK);
        var locationId = location["id"]!.GetValue<string>();
        await Call(HttpMethod.Post, "/api/vault-locations", new { name = TestLogin.For("Síť"), kind = "smb", root = "smb://files.example.test/keepass", username = "atlas", domain = "EX", password = "smb-canary-Heslo" }, HttpStatusCode.OK);
        await Call(HttpMethod.Get, "/api/vault-locations", null, HttpStatusCode.OK);

        File.WriteAllBytes(Path.Combine(folder, "provoz.kdbx"), Read(Fixture));
        var master = Password(Fixture);
        await Call(HttpMethod.Post, "/api/vaults/attach", new { spaceId, locationId, name = "Provoz", fileName = "provoz.kdbx", password = "wrong password" }, HttpStatusCode.BadRequest);
        var vault = await Call(HttpMethod.Post, "/api/vaults/attach", new { spaceId, locationId, name = "Provoz", fileName = "provoz.kdbx", password = master }, HttpStatusCode.OK);
        var vaultId = vault["id"]!.GetValue<string>();
        Assert.Equal("unlocked", vault["status"]!.GetValue<string>());
        Assert.Equal(4, vault["entryCount"]!.GetValue<int>());
        await Call(HttpMethod.Get, "/api/vaults", null, HttpStatusCode.OK);

        // The attach step picks a file: *.kdbx of the folder itself, registered files marked with the vault that uses them.
        File.WriteAllBytes(Path.Combine(folder, "zaloha.kdbx"), Read(Fixture));
        File.WriteAllText(Path.Combine(folder, "poznamky.txt"), "x");
        Directory.CreateDirectory(Path.Combine(folder, "archiv"));
        File.WriteAllBytes(Path.Combine(folder, "archiv", "stary.kdbx"), Read(Fixture));
        var listing = await Call(HttpMethod.Get, $"/api/vault-locations/{locationId}/files", null, HttpStatusCode.OK);
        Assert.False(listing["truncated"]!.GetValue<bool>());
        Assert.Equal(["provoz.kdbx", "zaloha.kdbx"], listing["files"]!.AsArray().Select(file => file!["name"]!.GetValue<string>()));
        Assert.Equal(["name", "sizeBytes", "lastWriteAt", "registered", "usedBy"], listing["files"]![0]!.AsObject().Select(p => p.Key));
        Assert.True(listing["files"]![0]!["registered"]!.GetValue<bool>());
        Assert.Equal("Provoz", listing["files"]![0]!["usedBy"]!.GetValue<string>());
        Assert.False(listing["files"]![1]!["registered"]!.GetValue<bool>());
        Assert.Equal(Read(Fixture).Length, listing["files"]![1]!["sizeBytes"]!.GetValue<long>());
        var narrowed = await Call(HttpMethod.Get, $"/api/vault-locations/{locationId}/files?spaceId={spaceId}&q=ZAL", null, HttpStatusCode.OK);
        Assert.Equal(["zaloha.kdbx"], narrowed["files"]!.AsArray().Select(file => file!["name"]!.GetValue<string>()));
        await Call(HttpMethod.Get, $"/api/vault-locations/{locationId}/files?q={new string('x', 201)}", null, HttpStatusCode.BadRequest);
        await Call(HttpMethod.Get, $"/api/vault-locations/{Guid.NewGuid()}/files", null, HttpStatusCode.NotFound);
        await Call(HttpMethod.Get, $"/api/vault-locations/{locationId}/files?spaceId={Guid.NewGuid()}", null, HttpStatusCode.NotFound);

        var secrets = await Call(HttpMethod.Get, $"/api/vaults/{vaultId}/secrets", null, HttpStatusCode.OK);
        Assert.Equal(4, secrets.AsArray().Count);
        Assert.Equal(["secretId", "entryUuid", "title", "groupPath", "availableFields", "firstSeenAt", "removedAt", "level"], secrets[0]!.AsObject().Select(p => p.Key));
        var verify = await Call(HttpMethod.Post, $"/api/vaults/{vaultId}/verify", null, HttpStatusCode.OK);
        Assert.Equal(4, verify["entryCount"]!.GetValue<int>());
        Assert.Empty(verify["missing"]!.AsArray());
        await Call(HttpMethod.Put, $"/api/vaults/{vaultId}/password", new { password = "wrong password" }, HttpStatusCode.BadRequest);
        await Call(HttpMethod.Put, $"/api/vaults/{vaultId}/password", new { password = master }, HttpStatusCode.OK);

        var createdResponse = await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/vaults/new", new { spaceId, locationId, name = "Nový", fileName = "novy.kdbx", generatePassword = true });
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        var created = JsonNode.Parse(await createdResponse.Content.ReadAsStringAsync())!;
        var generated = created["generatedPassword"]!.GetValue<string>();
        Assert.Equal(28, generated.Length);
        await Call(HttpMethod.Get, "/api/vaults", null, HttpStatusCode.OK);
        await Call(HttpMethod.Post, $"/api/vaults/{created["vault"]!["id"]!.GetValue<string>()}/archive", null, HttpStatusCode.NoContent);
        await Call(HttpMethod.Delete, $"/api/vault-locations/{locationId}", null, HttpStatusCode.Conflict);

        var canaries = Canaries(Fixture).Append(master).Append("smb-canary-Heslo").Append("CfDJ8").ToArray();
        foreach (var body in bodies)
        {
            foreach (var canary in canaries.Append(generated))
            {
                Assert.DoesNotContain(canary, body, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("protected", body, StringComparison.OrdinalIgnoreCase);
        }
        var logs = string.Join("\n", site.Logs.Messages);
        foreach (var canary in canaries.Append(generated).Append("wrong password"))
        {
            Assert.DoesNotContain(canary, logs, StringComparison.Ordinal);
        }
        var stored = (string)(await site.ScalarAsync("SELECT \"ProtectedKdbxPassword\" FROM \"Vault\" WHERE \"Id\"=@id", ("id", Guid.Parse(vaultId))))!;
        Assert.DoesNotContain(master, stored, StringComparison.Ordinal);
        Assert.Equal(0L, await site.ScalarAsync("SELECT count(*) FROM \"VaultSecret\" s WHERE s.\"Title\" = ANY(@values) OR s.\"GroupPath\" = ANY(@values)", ("values", canaries)));
        Directory.Delete(folder, true);
    }

    /// <summary>Entry values of a fixture that are distinctive enough to search for.</summary>
    internal static IEnumerable<string> Canaries(string name)
    {
        foreach (var entry in Fixture(name).GetProperty("entries").EnumerateArray())
        {
            foreach (var field in new[] { "password", "url", "userName" })
            {
                var value = entry.GetProperty(field).GetString();
                // User names such as "admin" or "uživatel" also occur in ordinary text; only the address-like one is distinctive.
                if (!string.IsNullOrEmpty(value) && (field != "userName" || value.Contains('@')))
                {
                    yield return value;
                }
            }
        }
    }
}
