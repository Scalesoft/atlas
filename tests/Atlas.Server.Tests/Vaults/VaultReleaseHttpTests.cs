using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Vaults.Release;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Vaults;

public sealed class VaultReleaseHttpTests
{
    private const string Fixture = "argon2d-aes-gzip.kdbx";

    /// <summary>
    /// Canary for the release API (WP4 V5): a released value appears in exactly one place, the body of its own successful release
    /// (uncompressed even when the client accepts compression). It is never in another response or error body (403/404/
    /// 409/400, audit, requests), a log line, an audit row, the access audit views or a live event payload. Live
    /// events go to the account on the collaboration hub.
    /// </summary>
    [PostgreSqlFact]
    public async Task Released_values_appear_only_in_their_release_response()
    {
        var folder = Path.Combine(Path.GetTempPath(), "atlas-vault-release-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var policy = new FakeReleasePolicy();
        var events = new RecordingSecurityEvents();
        await using var site = await AtlasHttpSite.CreateAsync("atlas_vault_release_http_", settings =>
        {
            settings["Vaults:RefreshSeconds"] = "0";
            // Values count as shown for an hour, so a slow run cannot let them lapse before the archive re-check.
            settings["Vaults:Release:ValueSeconds"] = "3600";
        },
            FakeReleasePolicy.Register(policy, events));
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var bodies = new List<(string Path, HttpStatusCode Status, string Body)>();
        async Task<JsonNode> Call(HttpMethod method, string path, object? payload, HttpStatusCode expected)
        {
            var response = method == HttpMethod.Get ? await admin.GetAsync(path) : await AtlasHttpSite.SendAsync(admin, method, path, payload);
            var body = await response.Content.ReadAsStringAsync();
            bodies.Add((path, response.StatusCode, body));
            Assert.True(expected == response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {body}");
            if (path.StartsWith("/api/secrets", StringComparison.Ordinal) || path.StartsWith("/api/access-requests", StringComparison.Ordinal))
            {
                Assert.Null(response.Content.Headers.ContentEncoding.FirstOrDefault());
            }
            return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!;
        }

        var spaceId = (await Call(HttpMethod.Get, "/api/spaces", null, HttpStatusCode.OK))[0]!["id"]!.GetValue<string>();
        var location = await Call(HttpMethod.Post, "/api/vault-locations", new { name = "Místní", kind = "local", root = folder }, HttpStatusCode.OK);
        File.WriteAllBytes(Path.Combine(folder, "provoz.kdbx"), Read(Fixture));
        var vault = await Call(HttpMethod.Post, "/api/vaults/attach", new
        {
            spaceId, locationId = location["id"]!.GetValue<string>(), name = "Provoz", fileName = "provoz.kdbx", password = Password(Fixture)
        }, HttpStatusCode.OK);
        var vaultId = vault["id"]!.GetValue<string>();
        var secrets = (await Call(HttpMethod.Get, $"/api/vaults/{vaultId}/secrets", null, HttpStatusCode.OK)).AsArray();
        string Secret(string title) => secrets.Single(s => s!["title"]!.GetValue<string>() == title)!["secretId"]!.GetValue<string>();
        var router = Secret("Router");
        var mail = Secret("Mail");
        var github = Secret("GitHub");

        // From here on the client accepts compression: release responses must still arrive uncompressed.
        admin.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        admin.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        var released = new Dictionary<string, string>();
        async Task Release(string secret, string field, string expected)
        {
            var path = $"/api/secrets/{secret}/release";
            var value = await Call(HttpMethod.Post, path, new { fields = new[] { field }, operation = "copy" }, HttpStatusCode.OK);
            Assert.Equal(expected, value["values"]![field]!.GetValue<string>());
            Assert.Equal(3600, value["expiresInSeconds"]!.GetValue<int>());
            released[bodies[^1].Body] = expected;
        }
        await Release(router, "password", "r0uter-pw");
        await Release(mail, "userName", "atlas@example.test");
        await Release(github, "url", "https://github.com");
        // The single-field shorthand answers in the single shape.
        var single = await Call(HttpMethod.Post, $"/api/secrets/{router}/release", new { field = "password", operation = "reveal" }, HttpStatusCode.OK);
        Assert.Equal("r0uter-pw", single["value"]!.GetValue<string>());
        released[bodies[^1].Body] = "r0uter-pw";
        await Call(HttpMethod.Post, $"/api/secrets/{router}/release/report", new { fields = new[] { "password" }, operation = "copy" }, HttpStatusCode.NoContent);

        // Denials and errors: none of them carries a value.
        await Call(HttpMethod.Post, $"/api/secrets/{router}/release", new { fields = new[] { "secret" }, operation = "copy" }, HttpStatusCode.BadRequest);
        await Call(HttpMethod.Post, $"/api/secrets/{mail}/release", new { field = "notes", operation = "reveal" }, HttpStatusCode.NotFound);
        await Call(HttpMethod.Post, $"/api/secrets/{Guid.NewGuid()}/release", new { field = "password", operation = "reveal" }, HttpStatusCode.NotFound);
        policy.Decision = new ReleaseDecision("always", "vault", true, true, null);
        // The one re-authentication refusal of every endpoint: 403 reauth_required / fresh_auth_unsupported, here with the mode.
        var challenge = await Call(HttpMethod.Post, $"/api/secrets/{router}/release", new { field = "password", operation = "reveal" }, HttpStatusCode.Forbidden);
        Assert.Equal(("reauth_required", "always", "vault", true), (challenge["code"]!.GetValue<string>(), challenge["reauth"]!["mode"]!.GetValue<string>(),
            challenge["reauth"]!["modeSource"]!.GetValue<string>(), challenge["reauth"]!["supported"]!.GetValue<bool>()));
        policy.Decision = new ReleaseDecision("always", "instance", true, false, null);
        var unsupported = await Call(HttpMethod.Post, $"/api/secrets/{router}/release", new { field = "password", operation = "reveal", proofId = Guid.NewGuid() }, HttpStatusCode.Forbidden);
        Assert.Equal(("fresh_auth_unsupported", false), (unsupported["code"]!.GetValue<string>(), unsupported["reauth"]!["supported"]!.GetValue<bool>()));
        policy.Decision = new ReleaseDecision("never", "instance", false, true, null);
        Assert.Equal("access_already_granted", (await Call(HttpMethod.Post, $"/api/secrets/{router}/access-requests", new { level = "read", reason = "Servis" },
            HttpStatusCode.Conflict))["code"]!.GetValue<string>());
        await Call(HttpMethod.Get, "/api/access-requests?state=all", null, HttpStatusCode.OK);
        await Call(HttpMethod.Get, "/api/access-requests/mine", null, HttpStatusCode.OK);
        var audit = await Call(HttpMethod.Get, $"/api/vaults/{vaultId}/audit?kind=release", null, HttpStatusCode.OK);
        Assert.Equal(["secret.release_denied", "secret.release_denied", "secret.release_denied", "secret.client_report", "secret.released", "secret.released", "secret.released", "secret.released"],
            audit["items"]!.AsArray().Select(i => i!["action"]!.GetValue<string>()));
        await Call(HttpMethod.Get, $"/api/secrets/{router}/audit?result=released", null, HttpStatusCode.OK);
        await Call(HttpMethod.Get, "/api/admin/access-audit?limit=100", null, HttpStatusCode.OK);
        File.Delete(Path.Combine(folder, "provoz.kdbx"));
        Assert.Equal("vault_unavailable", (await Call(HttpMethod.Post, $"/api/secrets/{router}/release", new { field = "password", operation = "reveal" },
            HttpStatusCode.Conflict))["code"]!.GetValue<string>());
        await Call(HttpMethod.Post, $"/api/vaults/{vaultId}/archive", null, HttpStatusCode.NoContent);
        for (var attempt = 0; attempt < 100 && events.Revocations.All(r => r.SecretId != Guid.Parse(router)); attempt++)
        {
            await Task.Delay(100);
        }
        Assert.Contains(events.Revocations, r => r.SecretId == Guid.Parse(router));

        var canaries = VaultHttpTests.Canaries(Fixture).ToArray();
        Assert.Contains("r0uter-pw", canaries);
        foreach (var (path, status, body) in bodies)
        {
            var own = released.TryGetValue(body, out var value) ? value : null;
            foreach (var canary in canaries.Where(c => c != own))
            {
                Assert.False(body.Contains(canary, StringComparison.Ordinal), $"{path} ({(int)status}) carries a secret value");
            }
        }
        var logs = string.Join("\n", site.Logs.Messages);
        var auditRows = (string)(await site.ScalarAsync("""
            SELECT string_agg(row_to_json(e)::text, E'\n') FROM "SecretReleaseEvent" e
            """))!;
        var auditView = (string)(await site.ScalarAsync("""
            SELECT string_agg(row_to_json(v)::text, E'\n') FROM "AccessAuditView" v
            """))!;
        var payloads = string.Join("\n", events.Payloads);
        Assert.Contains("released", auditRows, StringComparison.Ordinal);
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, auditRows, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, auditView, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, payloads, StringComparison.Ordinal);
        }
        Directory.Delete(folder, true);
    }
}
