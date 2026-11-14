using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Atlas.Markdown.Sync;
using Atlas.Server.Tests.Collaboration;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Collaboration.CollaborationContentGuardHttpTests;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;

namespace Atlas.Server.Tests.Vaults;

/// <summary>
/// Password references through Atlas's real host (WP4 V6): the picker search and reference metadata (only what the
/// caller may Discover, never a value), a reference admitted into live collaboration over SignalR while an inline value is
/// refused, and a page export that keeps the reference but never carries a value.
/// </summary>
public sealed class SecretReferenceHttpTests
{
    private const string Fixture = "argon2d-aes-gzip.kdbx";
    /// <summary>The secret id of the editor corpus payload 003-raw_secretReference_block (CollaborationContentGuardHttpTests).</summary>
    private const string CorpusSecretId = "d847b3af-4a87-4e97-aab8-a74a4cf48c89";

    // 003-raw_secretReference_block.update.bin of the editor corpus: a secretReference block naming CorpusSecretId.
    private static readonly byte[] SecretNode = Convert.FromBase64String(
        "AQew6cHoAQDHm6HIlQ8Am6HIlQ8NAw9zZWNyZXRSZWZlcmVuY2UoALDpwegBAAhzZWNyZXRJZAF3JGQ4NDdiM2FmLTRhODctNGU5Ny1hYWI4LWE3NGE0Y2Y0OGM4OSgAsOnB6AEABWZpZWxkAXcIcGFzc3dvcmQoALDpwegBAAZmaWVsZHMBdQN3CHVzZXJuYW1ldwhwYXNzd29yZHcDdXJsKACw6cHoAQAIc2hvd0NvcHkBeCgAsOnB6AEAC3Nob3dEZXRhaWxzAXgoALDpwegBAA9hdXRvSGlkZVNlY29uZHMBfTwA");

    // Generated with yjs 13.6 over Baseline: a secretReference naming CorpusSecretId with an extra "value" attribute (CANARY-inline-value).
    private static readonly byte[] InlineValue = Convert.FromBase64String(
        "AQSK8937AgCHm6HIlQ8nAw9zZWNyZXRSZWZlcmVuY2UoAIrz3fsCAAhzZWNyZXRJZAF3JGQ4NDdiM2FmLTRhODctNGU5Ny1hYWI4LWE3NGE0Y2Y0OGM4OSgAivPd+wIABWZpZWxkAXcIcGFzc3dvcmQoAIrz3fsCAAV2YWx1ZQF3E0NBTkFSWS1pbmxpbmUtdmFsdWUA");

    [PostgreSqlFact]
    public async Task References_are_searched_resolved_collaborated_and_exported_without_values()
    {
        var folder = Path.Combine(Path.GetTempPath(), "atlas-secret-references-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await using var site = await AtlasHttpSite.CreateAsync("atlas_secret_refs_http_", settings => settings["Vaults:RefreshSeconds"] = "0",
            CollaborationContentGuardHttpTests.BrowserSeeds);
        var cookies = new CookieContainer();
        using var admin = site.CreateDefaultClient(BaseAddress, new CookieContainerHandler(cookies));
        await site.CompleteSetupAsync(admin);
        var bodies = new List<string>();
        async Task<JsonNode> Call(HttpMethod method, string path, object? payload, HttpStatusCode expected)
        {
            var response = method == HttpMethod.Get ? await admin.GetAsync(path) : await AtlasHttpSite.SendAsync(admin, method, path, payload);
            var body = await response.Content.ReadAsStringAsync();
            bodies.Add(body);
            Assert.True(expected == response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {body}");
            return body.Length == 0 ? new JsonObject() : JsonNode.Parse(body)!;
        }

        var spaceId = (await Call(HttpMethod.Get, "/api/spaces", null, HttpStatusCode.OK))[0]!["id"]!.GetValue<string>();
        var location = await Call(HttpMethod.Post, "/api/vault-locations", new { name = "Místní", kind = "local", root = folder }, HttpStatusCode.OK);
        File.WriteAllBytes(Path.Combine(folder, "provoz.kdbx"), Read(Fixture));
        var vault = await Call(HttpMethod.Post, "/api/vaults/attach",
            new { spaceId, locationId = location["id"]!.GetValue<string>(), name = "Provoz", fileName = "provoz.kdbx", password = Password(Fixture) }, HttpStatusCode.OK);
        var router = (await Call(HttpMethod.Get, $"/api/vaults/{vault["id"]!.GetValue<string>()}/secrets", null, HttpStatusCode.OK)).AsArray()
            .Single(s => s!["title"]!.GetValue<string>() == "Router")!["secretId"]!.GetValue<string>();
        // The corpus payload names a fixed id: give the Router entry that Atlas id.
        await site.ExecuteAsync("UPDATE \"VaultSecret\" SET \"Id\" = @corpus WHERE \"Id\" = @id", ("corpus", Guid.Parse(CorpusSecretId)), ("id", Guid.Parse(router)));

        var found = await Call(HttpMethod.Get, $"/api/secrets/search?q=rou&spaceId={spaceId}", null, HttpStatusCode.OK);
        var hit = Assert.Single(found.AsArray())!;
        Assert.Equal(CorpusSecretId, hit["id"]!.GetValue<string>());
        Assert.Equal("Provoz", hit["vaultName"]!.GetValue<string>());
        Assert.Equal("manage", hit["level"]!.GetValue<string>());
        Assert.Contains("password", hit["fields"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Empty((await Call(HttpMethod.Get, $"/api/secrets/search?q=rou&spaceId={Guid.NewGuid()}", null, HttpStatusCode.OK)).AsArray());
        Assert.True((await Call(HttpMethod.Get, "/api/secrets/search?q=", null, HttpStatusCode.OK)).AsArray().Count >= 4);
        await Call(HttpMethod.Get, "/api/secrets/search?q=" + new string('x', 201), null, HttpStatusCode.BadRequest);
        var reference = await Call(HttpMethod.Get, $"/api/secrets/{CorpusSecretId}/reference", null, HttpStatusCode.OK);
        Assert.Equal("Router", reference["title"]!.GetValue<string>());
        Assert.Null(reference["value"]);
        await Call(HttpMethod.Get, $"/api/secrets/{Guid.NewGuid()}/reference", null, HttpStatusCode.NotFound);

        // Live collaboration: the WikiAdmin may Discover the secret, so the reference is admitted; an inline value never is.
        var page = await Call(HttpMethod.Post, "/api/pages", new { spaceId, parentId = (string?)null, title = "Hesla", contentJson = Rich, markdown = "" }, HttpStatusCode.OK);
        var pageId = page["id"]!.GetValue<string>();
        var session = await Call(HttpMethod.Post, $"/api/pages/{pageId}/collaboration", null, HttpStatusCode.OK);
        var documentId = session["documentId"]!.GetValue<string>();
        await using (var author = Connect(site, cookies))
        {
            await author.StartAsync();
            Assert.True((await JoinAsync(site, author, documentId)).IsSnapshotter);
            await author.InvokeAsync("PushSnapshot", documentId, 0L, Baseline, Projections(0, Baseline));
            Assert.Equal(1, await author.InvokeAsync<long>("SendUpdate", documentId, Guid.NewGuid().ToString("D"), SecretNode));
            var refused = await Assert.ThrowsAsync<HubException>(() => author.InvokeAsync<long>("SendUpdate", documentId, Guid.NewGuid().ToString("D"), InlineValue));
            Assert.Contains("[INVALID_CONTENT]", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("CANARY", refused.Message, StringComparison.Ordinal);
        }
        Assert.Equal(1L, await site.ScalarAsync("SELECT count(*) FROM \"CollaborationSecretReference\" WHERE \"PageId\" = @page AND \"SecretId\" = @secret",
            ("page", Guid.Parse(pageId)), ("secret", Guid.Parse(CorpusSecretId))));
        Assert.Equal(0L, await site.ScalarAsync("SELECT count(*) FROM \"CollaborationUpdate\" WHERE \"PageId\" = @page AND \"Payload\" = @payload",
            ("page", Guid.Parse(pageId)), ("payload", InlineValue)));

        // Export keeps the reference for its reader, and neither the export nor any response carries a value.
        var other = await Call(HttpMethod.Post, "/api/pages", new
        {
            spaceId, parentId = (string?)null, title = "Export",
            contentJson = SecretReferenceGuardTests.Rich(SecretReferenceGuardTests.Reference(Guid.Parse(CorpusSecretId))), markdown = "Heslo"
        }, HttpStatusCode.OK);
        var export = await admin.GetAsync($"/api/pages/{other["id"]!.GetValue<string>()}/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var archive = await ReadZipAsync(await export.Content.ReadAsByteArrayAsync());
        Assert.Contains(CorpusSecretId, archive);
        var canaries = VaultHttpTests.Canaries(Fixture).Append(Password(Fixture)).Append("CANARY").ToArray();
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, archive, StringComparison.Ordinal);
            Assert.All(bodies, body => Assert.DoesNotContain(canary, body, StringComparison.Ordinal));
            Assert.DoesNotContain(canary, string.Join("\n", site.Logs.Messages), StringComparison.Ordinal);
        }
        Directory.Delete(folder, true);
    }

    private static async Task<string> ReadZipAsync(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var text = new StringBuilder();
        foreach (var entry in zip.Entries)
        {
            await using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text.AppendLine(await reader.ReadToEndAsync());
        }
        return text.ToString();
    }
}
