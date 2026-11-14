using Microsoft.AspNetCore.SignalR.Client;
using System.Security.Cryptography;
using System.Text.Json;

namespace Atlas.Markdown.Sync.Tests;

public sealed class DocumentSchemaTests
{
    private static readonly MarkdownDocumentSchema Rich = new(1, "rich", [new("charts", 1), new("youtrack", 2)]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Equal_schemas_ignore_plugin_order_and_hydrate(int revision)
    {
        await using var host = await HubTestHost.StartAsync();
        await using var first = host.Connection("alice");
        await using var second = host.Connection("bob");
        await first.StartAsync();
        await second.StartAsync();
        var schema = Rich with { Version = revision };
        await Seed(first, schema);

        var joined = await second.InvokeAsync<JoinResult>("JoinDocumentWithSchema", "schema:doc", schema with { Plugins = schema.Plugins.Reverse().ToArray() });
        Assert.NotNull(joined.Snapshot);
        Assert.Equal(revision, joined.Schema!.Version);
        Assert.Equal(Rich.Plugins, joined.Schema!.Plugins);
        Assert.Equal(2, host.Registry.TotalPeerCount);
    }

    [Theory]
    [InlineData("markdown-safe", false, 1)]
    [InlineData("rich", true, 1)]
    [InlineData("rich", false, 3)]
    public async Task Incompatible_schema_cannot_join_or_write(string profile, bool missingPlugin, int chartVersion)
    {
        await using var host = await HubTestHost.StartAsync();
        await using var first = host.Connection("alice");
        await using var rejected = host.Connection("bob");
        await first.StartAsync();
        await rejected.StartAsync();
        await Seed(first, Rich);
        var requested = new MarkdownDocumentSchema(1, profile, missingPlugin ? [] : [new("charts", chartVersion), new("youtrack", 2)]);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => rejected.InvokeAsync<JoinResult>("JoinDocumentWithSchema", "schema:doc", requested));
        Assert.Contains("[SCHEMA_MISMATCH]", error.Message);
        Assert.Equal(1, host.Registry.TotalPeerCount);
        await Assert.ThrowsAnyAsync<Exception>(() => rejected.InvokeAsync<long>("SendUpdate", "schema:doc", Guid.NewGuid().ToString("D"), new byte[] { 9 }));
        Assert.Equal(0, host.Store.AppendCallsOf("schema:doc"));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    public async Task Different_builtin_revision_cannot_join_or_write(int storedRevision, int requestedRevision)
    {
        await using var host = await HubTestHost.StartAsync();
        await using var first = host.Connection("alice");
        await using var rejected = host.Connection("bob");
        await first.StartAsync();
        await rejected.StartAsync();
        await Seed(first, Rich with { Version = storedRevision });

        var error = await Assert.ThrowsAnyAsync<Exception>(() => rejected.InvokeAsync<JoinResult>(
            "JoinDocumentWithSchema", "schema:doc", Rich with { Version = requestedRevision }));
        Assert.Contains("[SCHEMA_MISMATCH]", error.Message);
        Assert.Contains("built-in schema revision", error.Message);
        Assert.Equal(1, host.Registry.TotalPeerCount);
        await Assert.ThrowsAnyAsync<Exception>(() => rejected.InvokeAsync<long>(
            "SendUpdate", "schema:doc", Guid.NewGuid().ToString("D"), new byte[] { 9 }));
        Assert.Equal(0, host.Store.AppendCallsOf("schema:doc"));
    }

    [Fact]
    public async Task Restart_retains_schema_and_legacy_cannot_enter_versioned_document()
    {
        var store = new InMemoryDocumentStore();
        await using (var host = await HubTestHost.StartAsync(store: store))
        {
            await using var first = host.Connection("alice");
            await first.StartAsync();
            await Seed(first, Rich);
        }
        await using var restarted = await HubTestHost.StartAsync(store: store);
        await using var client = restarted.Connection("bob");
        await client.StartAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => client.InvokeAsync<JoinResult>("JoinDocument", "schema:doc"));
        await Assert.ThrowsAnyAsync<Exception>(() => client.InvokeAsync<JoinResult>("JoinDocumentWithSchema", "schema:doc", Rich with { Plugins = [] }));
        var joined = await client.InvokeAsync<JoinResult>("JoinDocumentWithSchema", "schema:doc", Rich);
        Assert.NotNull(joined.Snapshot);
    }

    [Fact]
    public async Task Legacy_document_cannot_silently_acquire_a_modern_profile()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var legacy = host.Connection("alice");
        await using var modern = host.Connection("bob");
        await legacy.StartAsync();
        await modern.StartAsync();
        await legacy.InvokeAsync<JoinResult>("JoinDocument", "schema:doc");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => modern.InvokeAsync<JoinResult>("JoinDocumentWithSchema", "schema:doc", Rich));
        Assert.Contains("explicit migration", error.Message);
        Assert.Equal(1, host.Registry.TotalPeerCount);
    }

    [Fact]
    public async Task A_reader_join_is_checked_against_the_bound_schema_but_never_binds()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var writer = host.Connection("alice");
        await using var reader = host.Connection("ro-viewer");
        await using var stranger = host.Connection("ro-other");
        await writer.StartAsync();
        await reader.StartAsync();
        await stranger.StartAsync();
        await Seed(writer, Rich);
        var bound = host.Store.BindCallsOf("schema:doc");

        Assert.False((await reader.InvokeAsync<JoinResult>("JoinDocumentWithSchema", "schema:doc", Rich)).CanWrite);
        await Assert.ThrowsAnyAsync<Exception>(() => stranger.InvokeAsync<JoinResult>("JoinDocumentWithSchema", "schema:doc", Rich with { Plugins = [] }));
        Assert.Equal(bound, host.Store.BindCallsOf("schema:doc"));
    }

    [Fact]
    public async Task A_reader_first_on_an_unbound_document_neither_binds_nor_writes_and_the_first_writer_binds()
    {
        await using var host = await HubTestHost.StartAsync();
        host.Store.Seed("schema:doc", "# Saved");
        await using var reader = host.Connection("ro-viewer");
        await using var writer = host.Connection("alice");
        await reader.StartAsync();
        await writer.StartAsync();

        // No live state to read yet: the reader is refused as read-only (the page stays static) and leaves nothing behind.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => reader.InvokeAsync<JoinResult>("JoinDocumentWithSchema", "schema:doc", Rich));
        Assert.Contains("[READ_ONLY]", error.Message);
        Assert.Equal((0, 0, 0, 0), (host.Store.BindCallsOf("schema:doc"), host.Store.AppendCallsOf("schema:doc"),
            host.Store.FlushCallsOf("schema:doc"), host.Registry.TotalPeerCount));

        await Seed(writer, Rich);
        Assert.Equal(1, host.Store.BindCallsOf("schema:doc"));
        var joined = await reader.InvokeAsync<JoinResult>("JoinDocumentWithSchema", "schema:doc", Rich);
        Assert.False(joined.CanWrite);
        Assert.NotNull(joined.Snapshot);
        Assert.Equal(1, host.Store.BindCallsOf("schema:doc"));
    }

    [Theory]
    [InlineData(0, "rich")]
    [InlineData(8, "rich")]
    [InlineData(1, "unknown")]
    public void Unsupported_schema_is_rejected(int version, string profile)
        => Assert.Throws<DocumentSchemaException>(() => DocumentSchemaCompatibility.Normalize(new(version, profile, [])));

    [Theory]
    [InlineData(1, "rich")]
    [InlineData(2, "rich")]
    [InlineData(3, "rich")]
    [InlineData(1, "markdown-safe")]
    public void Normalization_preserves_the_declared_builtin_revision(int revision, string profile)
    {
        var normalized = DocumentSchemaCompatibility.Normalize(new(revision, profile, [new("z", 1), new("a", 2)]));
        Assert.Equal(revision, normalized.Version);
        Assert.Equal(["a", "z"], normalized.Plugins.Select(plugin => plugin.Id));
    }

    [Fact]
    public async Task Store_without_durable_schema_support_preserves_legacy_and_rejects_modern_sessions()
    {
        IDocumentStore store = new FaultInjectingDocumentStore();
        Assert.Null(await store.BindSchemaAsync("legacy", null, CancellationToken.None));
        var error = await Assert.ThrowsAsync<DocumentSchemaException>(() => store.BindSchemaAsync("modern", Rich, CancellationToken.None));
        Assert.Contains("does not support durable schema", error.Message);
    }

    private static async Task Seed(HubConnection connection, MarkdownDocumentSchema schema)
    {
        await connection.InvokeAsync<JoinResult>("JoinDocumentWithSchema", "schema:doc", schema);
        var state = new byte[] { 0 };
        var hash = Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant();
        using var json = JsonDocument.Parse("{\"type\":\"doc\",\"content\":[]}");
        await connection.InvokeAsync("PushSnapshot", "schema:doc", 0L, state, new ProjectionSetDto(0, hash, "", json.RootElement.Clone(), ""));
    }
}
