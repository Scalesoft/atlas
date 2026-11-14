using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Tests.ContentImport;
using Atlas.Server.Features.ExternalSharing;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Tests.Collaboration;
using Xunit;

namespace Atlas.Server.Tests;

/// <summary>The document contract of password references (WP4 V6): only the plugin's attributes, never a value, on every
/// ingestion path; stripping for readers; public sharing removes every reference.</summary>
public sealed class SecretReferenceContentTests
{
    internal const string SecretId = "2538f90a-733e-4f69-bafe-5f71086739be";
    private const string Secret = """{"type":"secretReference","attrs":{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","value":"CANARY"},"content":[{"type":"text","text":"CANARY"}],"fallback":"CANARY"}""";
    private const string InlineSecret = """{"type":"secretInlineReference","attrs":{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","value":"CANARY"},"content":[{"type":"text","text":"CANARY"}],"fallback":"CANARY"}""";
    internal const string Reference = """{"type":"secretReference","attrs":{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","field":"password","fields":["username","password"],"showCopy":true,"showDetails":false,"autoHideSeconds":60}}""";
    private const string InlineReference = """{"type":"secretInlineReference","attrs":{"secretId":"8d2f7a1c-5b3e-4c6d-9e0f-1a2b3c4d5e6f","fields":null,"autoHideSeconds":null},"marks":[{"type":"bold"}]}""";

    public static TheoryData<string> BrokenReferences => new()
    {
        Envelope("{\"type\":\"doc\",\"content\":[" + Secret + "]}"),
        Envelope("{\"type\":\"doc\",\"content\":[{\"type\":\"blockquote\",\"content\":[" + Secret + "]}]}"),
        Envelope("{\"type\":\"doc\",\"attrs\":{\"nested\":[" + Secret + "]}}"),
        Envelope("{\"type\":\"doc\",\"content\":[" + InlineSecret + "]}"),
        Envelope("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[" + InlineSecret + "]}]}"),
        // An inline value under any name, a label, text content, a missing or noncanonical id, bad presentation values.
        Node("""{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","value":"CANARY"}"""),
        Node("""{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","password":"CANARY"}"""),
        Node("""{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","label":"CANARY"}"""),
        Node("""{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","secretId":"8d2f7a1c-5b3e-4c6d-9e0f-1a2b3c4d5e6f"}"""),
        Node("""{"field":"password"}"""),
        Node("""{"secretId":"2538F90A-733E-4F69-BAFE-5F71086739BE"}"""),
        Node("""{"secretId":"{2538f90a-733e-4f69-bafe-5f71086739be}"}"""),
        Node("""{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","field":"CANARY"}"""),
        Node("""{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","fields":[]}"""),
        Node("""{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","fields":["password","password"]}"""),
        Node("""{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","showCopy":"yes"}"""),
        Node("""{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","autoHideSeconds":4}"""),
        Node("""{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be","autoHideSeconds":60.5}"""),
        Envelope("""{"type":"doc","content":[{"type":"secretReference","attrs":{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be"},"content":[{"type":"text","text":"CANARY"}]}]}"""),
        Envelope("""{"type":"doc","content":[{"type":"secretReference","attrs":{"secretId":"2538f90a-733e-4f69-bafe-5f71086739be"},"text":"CANARY"}]}"""),
        Envelope("""{"type":"doc","content":[{"type":"secretReference"}]}"""),
        Envelope("{\"type\":\"doc\",\"content\":[{\"type\":\"secretReference\",\"type\":\"paragraph\"}]}"),
        Envelope("{\"type\":\"doc\",\"content\":[{\"type\":\"secretInlineReference\",\"type\":\"text\"}]}"),
        Envelope("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"type\":\"secretReference\",\"text\":\"CANARY\"}]}"),
    };

    [Theory]
    [MemberData(nameof(BrokenReferences))]
    public void Ingestion_refuses_references_outside_the_contract_without_echoing_input(string json)
    {
        var error = Assert.Throws<ApiException>(() => Rules.ValidateDocument(json, "CANARY"));
        Assert.Equal(400, error.Status);
        Assert.Equal("invalid_secret_reference", error.Code);
        Assert.DoesNotContain("CANARY", error.Message);
        Assert.DoesNotContain("2538f90a", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"CANARY\":")]
    [InlineData("null")]
    [InlineData("[]")]
    public void Malformed_documents_fail_without_echoing_input(string json)
    {
        var error = Assert.Throws<ApiException>(() => Rules.ValidateDocument(json, ""));
        Assert.Equal(400, error.Status);
        Assert.DoesNotContain("CANARY", error.Message);
    }

    [Fact]
    public void Valid_references_are_collected_at_every_depth_and_absent_attributes_are_defaults()
    {
        var json = Envelope("{\"type\":\"doc\",\"content\":[" + Reference + ",{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"VPN \"}," + InlineReference
            + "]},{\"type\":\"secretReference\",\"attrs\":{\"secretId\":\"" + SecretId + "\"}}]}", "[{\"id\":\"secrets\",\"version\":4}]");
        var ids = Rules.ValidateDocument(json, "");
        Assert.Equal([Guid.Parse(SecretId), Guid.Parse("8d2f7a1c-5b3e-4c6d-9e0f-1a2b3c4d5e6f")], ids.Order());
    }

    [Fact]
    public void Ordinary_content_and_plugin_declarations_remain_supported()
    {
        Assert.Empty(Rules.ValidateDocument(Envelope("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"secretReference secrets"}]}]}""", "[{\"id\":\"toc\",\"version\":1}]"), "secrets"));
        Assert.Empty(Rules.ValidateDocument(Envelope("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\"}]}"), ""));
        var imported = ImportPages.Convert("`secretReference` and secrets");
        Assert.Empty(Rules.ValidateDocument(imported.ContentJson, imported.Markdown));
    }

    [Theory]
    [InlineData("s\\u0065cretReference")]
    [InlineData("se\\u0063retReference")]
    [InlineData("sec\\u0072etInlineReference")]
    [InlineData("secre\\u0074Reference")]
    public void A_reference_whose_name_escapes_a_letter_is_still_found(string escapedType)
    {
        var json = Envelope("{\"type\":\"doc\",\"content\":[{\"type\":\"" + escapedType + "\",\"attrs\":{\"secretId\":\"" + SecretId + "\"}}]}");
        Assert.True(SecretReferenceContent.MayContainReferences(json));
        Assert.Equal([Guid.Parse(SecretId)], SecretReferenceContent.Collect(json));
    }

    [Fact]
    public void Escaped_diacritics_and_quotes_alone_do_not_force_a_parse()
    {
        // As stored pages hold Czech text: every non-ASCII letter and HTML-sensitive character as a \u escape.
        var json = Envelope("{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"P\\u0159\\u00EDli\\u0161 \\u0022\\u017Elu\\u0165ou\\u010Dk\\u00FD\\u0022 k\\u016F\\u0148 \\u003C\\u0026\\u003E\"}]}]}", "[]");
        Assert.False(SecretReferenceContent.MayContainReferences(json));
        Assert.Empty(SecretReferenceContent.Collect(json));
    }

    [Theory]
    [InlineData("secretId", "\"2538f90a-733e-4f69-bafe-5f71086739be\"", true)]
    [InlineData("fields", "[\"title\",\"path\",\"notes\"]", true)]
    [InlineData("autoHideSeconds", "3600", true)]
    [InlineData("autoHideSeconds", "null", true)]
    [InlineData("value", "\"CANARY\"", false)]
    [InlineData("ciphertext", "\"CANARY\"", false)]
    [InlineData("secretId", "7", false)]
    [InlineData("showDetails", "1", false)]
    public void Collaboration_attribute_writes_follow_the_same_rules(string key, string value, bool valid)
    {
        using var json = JsonDocument.Parse(value);
        Assert.Equal(valid, SecretReferenceContent.TryValidateAttributes([KeyValuePair.Create(key, json.RootElement)], false, out _));
        // A new node must name its secret.
        Assert.Equal(valid && key == "secretId", SecretReferenceContent.TryValidateAttributes([KeyValuePair.Create(key, json.RootElement)], true, out _));
    }

    [Fact]
    public void Readers_lose_exactly_the_references_they_may_not_discover()
    {
        var visible = Guid.Parse(SecretId);
        var json = Envelope("{\"type\":\"doc\",\"content\":[" + Reference + ",{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"VPN \"}," + InlineReference + "]}]}",
            "[{\"id\":\"secrets\",\"version\":4},{\"id\":\"custom\",\"version\":1}]");
        var partial = SecretReferenceContent.Strip(json, id => id == visible, out var removed);
        Assert.Equal(1, removed);
        Assert.Contains(SecretId, partial);
        Assert.DoesNotContain("8d2f7a1c", partial);
        Assert.Equal([visible], SecretReferenceContent.Collect(partial));
        Assert.Contains("\"secrets\"", partial);

        var none = SecretReferenceContent.Strip(json, _ => false, out removed);
        Assert.Equal(2, removed);
        Assert.Empty(SecretReferenceContent.Collect(none));
        using var document = JsonDocument.Parse(none);
        // A block reference leaves an empty paragraph (containers keep valid content); the plugin requirement goes too.
        Assert.Equal("paragraph", document.RootElement.GetProperty("document").GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("custom", Assert.Single(document.RootElement.GetProperty("plugins").EnumerateArray()).GetProperty("id").GetString());
        Assert.Same(json, SecretReferenceContent.Strip(json, _ => true, out removed));
        Assert.Equal(0, removed);
    }

    [Theory]
    [InlineData("secretReference")]
    [InlineData("secretInlineReference")]
    public void Public_sharing_removes_entire_secret_objects_at_every_depth_and_requirement_from_copy(string nodeType)
    {
        var secret = nodeType == "secretReference" ? Secret : InlineSecret;
        var source = Envelope("{\"type\":\"doc\",\"content\":[" + secret + ",{\"type\":\"paragraph\",\"content\":[" + secret + ",{\"type\":\"text\",\"text\":\"public\"}]}],\"attrs\":{\"private\":" + secret + "}}", "[{\"id\":\"secrets\",\"version\":1,\"CANARY\":true},{\"id\":\"custom\",\"version\":7}]");
        var shared = SharedDocument.ForPublic(source, new Dictionary<Guid, string>());
        Assert.DoesNotContain("CANARY", shared);
        Assert.DoesNotContain(nodeType, shared);
        Assert.DoesNotContain("2538f90a", shared);
        using var json = JsonDocument.Parse(shared);
        Assert.Equal("custom", Assert.Single(json.RootElement.GetProperty("plugins").EnumerateArray()).GetProperty("id").GetString());
        var paragraph = Assert.Single(json.RootElement.GetProperty("document").GetProperty("content").EnumerateArray());
        Assert.Equal("public", Assert.Single(paragraph.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
        Assert.Empty(json.RootElement.GetProperty("document").GetProperty("attrs").EnumerateObject());
    }

    [Theory]
    [InlineData("{\"CANARY\":")]
    [InlineData("null")]
    [InlineData("{\"type\":\"secretReference\",\"fallback\":\"CANARY\"}")]
    [InlineData("{\"type\":\"secretInlineReference\",\"fallback\":\"CANARY\"}")]
    [InlineData("{\"type\":\"doc\",\"plugins\":[[{\"id\":\"secrets\",\"fallback\":\"CANARY\"}]]}")]
    [InlineData("{\"type\":\"doc\",\"plugins\":{\"nested\":{\"id\":\"secrets\",\"fallback\":\"CANARY\"}}}")]
    [InlineData("{\"type\":\"doc\",\"type\":\"secretReference\",\"fallback\":\"CANARY\"}")]
    [InlineData("{\"type\":\"doc\",\"type\":\"secretInlineReference\",\"fallback\":\"CANARY\"}")]
    public void Public_sharing_fails_closed_for_malformed_or_reserved_root(string json)
    {
        var error = Assert.Throws<InvalidDataException>(() => SharedDocument.ForPublic(json, new Dictionary<Guid, string>()));
        Assert.DoesNotContain("CANARY", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Sharing_preserves_ordinary_content_structurally()
    {
        var source = Envelope("""{"type":"doc","content":[{"type":"text","text":"secretReference secrets"}],"future":{"opaque":true}}""", "[{\"id\":\"custom\",\"version\":8,\"description\":\"secrets\"}]");
        using var original = JsonDocument.Parse(source);
        using var shared = JsonDocument.Parse(SharedDocument.ForPublic(source, new Dictionary<Guid, string>()));
        Assert.True(JsonElement.DeepEquals(original.RootElement, shared.RootElement));
    }

    [Fact]
    public void The_current_host_schema_binds_the_secrets_plugin_v4()
    {
        Assert.Contains(new MarkdownSchemaPlugin("secrets", 4), CollaborationSchema.KnownHostSchemas[^1].Plugins);
        // Once a host schema binds the secrets plugin, every later schema keeps it (a document never loses its references).
        var first = CollaborationSchema.KnownHostSchemas.ToList().FindIndex(schema => schema.Plugins.Any(plugin => plugin.Id == "secrets"));
        Assert.True(first >= 0);
        Assert.All(CollaborationSchema.KnownHostSchemas.Skip(first), schema => Assert.Contains(schema.Plugins, plugin => plugin.Id == "secrets"));
    }

    [Theory]
    [InlineData("secretReference")]
    [InlineData("secretInlineReference")]
    public async Task Snapshot_projection_outside_the_contract_is_refused_before_persistence(string nodeType)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var store = new PostgresCollaborationStore(services.GetRequiredService<IServiceScopeFactory>());
        var secret = nodeType == "secretReference" ? Secret : InlineSecret;
        using var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[" + secret + "]}");
        byte[] state = [7, 8];
        var snapshot = new DocSnapshot(state, 1, new(1, Convert.ToHexStringLower(SHA256.HashData(state)), "CANARY", json.RootElement.Clone(), "CANARY"));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.FlushAsync(DocumentId(), snapshot, default));
        Assert.DoesNotContain("CANARY", error.Message);
    }

    internal static string Envelope(string document, string plugins = "[{\"id\":\"secrets\",\"version\":4}]") => "{\"format\":\"atlas-rich\",\"version\":1,\"document\":" + document + ",\"plugins\":" + plugins + "}";

    private static string Node(string attrs) => Envelope("{\"type\":\"doc\",\"content\":[{\"type\":\"secretReference\",\"attrs\":" + attrs + "}]}");

    [PostgreSqlFact]
    public async Task Rejected_page_and_collaboration_writes_leave_persisted_state_unchanged()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var json = Envelope("{\"type\":\"doc\",\"content\":[" + Secret + "]}");
        await Assert.ThrowsAsync<ApiException>(() => f.Pages.CreateAsync(new(f.Page.SpaceId, null, "Secret", json, "CANARY"), f.Writer, default));
        await Assert.ThrowsAsync<ApiException>(() => f.Pages.SaveAsync(f.Page.Id, new("Secret", json, "CANARY", f.Page.Version), f.Writer, default));
        // A valid reference to a secret the writer may not discover (none exists) is refused as well.
        var reference = Envelope("{\"type\":\"doc\",\"content\":[" + Reference + "]}");
        var unusable = await Assert.ThrowsAsync<ApiException>(() => f.Pages.SaveAsync(f.Page.Id, new("Secret", reference, "", f.Page.Version), f.Writer, default));
        Assert.Equal(("secret_reference_unavailable", 400), (unusable.Code, unusable.Status));
        Assert.Equal(1, await f.RevisionCount());
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Store.BindSchemaAsync(opened.DocumentId, null, default);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [1]);
        var snapshot = CollaborationFixture.Snapshot(1);
        using var projection = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[" + Secret + "]}");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.FlushAsync(opened.DocumentId,
            snapshot with { Projections = snapshot.Projections with { EditorJson = projection.RootElement.Clone() } }, default));
        // A projection may not name a reference the collaboration guard never admitted.
        using var smuggled = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[" + Reference + "]}");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.FlushAsync(opened.DocumentId,
            snapshot with { Projections = snapshot.Projections with { EditorJson = smuggled.RootElement.Clone() } }, default));
        var persisted = await f.Store.LoadAsync(opened.DocumentId);
        Assert.Null(persisted!.Snapshot);
        Assert.Equal(1, persisted.Seq);
        Assert.Single(persisted.Updates);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);
        Assert.Equal(new byte[] { 7, 8 }, (await f.Store.LoadAsync(opened.DocumentId))!.Snapshot);
    }

    private static string DocumentId() => new WikiDocumentId(Guid.NewGuid(), Guid.NewGuid()).ToString();
}
