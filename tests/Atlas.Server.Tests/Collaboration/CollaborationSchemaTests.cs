using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Validation;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

public sealed class CollaborationSchemaTests
{
    private static readonly IReadOnlyList<MarkdownDocumentSchema> Known = CollaborationSchema.KnownHostSchemas;

    [Fact]
    public void Each_release_adds_plugins_or_raises_the_revision_and_the_newest_matches_the_web_fixture()
    {
        for (var index = 1; index < Known.Count; index++)
        {
            var (previous, current) = (Known[index - 1], Known[index]);
            Assert.Equal(Known[0].Profile, current.Profile);
            // A release adds plugins, raises the editor's built-in revision (0.7.0: heading level 4, caution callout) or a plugin's
            // version (3.4: atlas-requirements 2); it never drops or lowers either, so every older binding can move forward.
            Assert.All(previous.Plugins, plugin => Assert.Contains(current.Plugins, next => next.Id == plugin.Id && next.Version >= plugin.Version));
            Assert.True(current.Version >= previous.Version);
            Assert.True(current.Plugins.Count > previous.Plugins.Count || current.Version > previous.Version || !current.Plugins.SequenceEqual(previous.Plugins));
        }
        var fixture = LoadWebFixture();
        Assert.Equal((fixture.Version, fixture.Profile), (Known[^1].Version, Known[^1].Profile));
        Assert.Equal(fixture.Plugins, Known[^1].Plugins);
    }

    [Fact]
    public void An_atlas_3_3_tab_cannot_join_a_document_with_atlas_requirements_2_but_its_pages_still_validate()
    {
        // 3.3 negotiated atlas-requirements 1: in a 3.4 document its y-tiptap would delete the overview and the new attributes.
        var atlas33 = Known.Last(schema => schema.Plugins.Contains(new MarkdownSchemaPlugin("atlas-requirements", 1)));
        CollaborationSchema.RequireAcceptable(atlas33);
        Assert.Throws<DocumentSchemaException>(() => DocumentSchemaCompatibility.EnsureCompatible(Known[^1], atlas33));
        // Pages saved by 3.3 keep their metadata: the editor migrates 1 to 2 when it opens them, the server accepts both.
        const string saved = """{"format":"atlas-rich","version":1,"plugins":[{"id":"atlas-requirements","version":1}],"document":{"type":"doc","content":[{"type":"requirement","attrs":{"id":"0b6a1f0e-2a51-4c55-9a2b-5d0f5a4e7c11"},"content":[{"type":"paragraph"}]}]}}""";
        Assert.Null(HostDocumentValidator.Instance.Validate(saved));
    }

    public static TheoryData<int> PreviousHostSchemas => new(Enumerable.Range(0, Known.Count - 1));

    [Theory]
    [MemberData(nameof(PreviousHostSchemas))]
    public void Every_previous_host_schema_upgrades_to_the_current_one_under_a_new_generation(int index)
    {
        var generation = Guid.NewGuid();
        // Stored order is irrelevant: the bound schema is normalized before it is compared.
        var stored = Known[index] with { Plugins = Known[index].Plugins.Reverse().ToArray() };
        var state = new PageCollaboration { Generation = generation, SchemaBound = true, SchemaJson = JsonSerializer.Serialize(stored) };
        CollaborationSchema.UpgradeKnownSchema(state);
        Assert.NotEqual(generation, state.Generation);
        var upgraded = CollaborationSchema.Deserialize(state.SchemaJson)!;
        Assert.Equal((Known[^1].Version, Known[^1].Profile), (upgraded.Version, upgraded.Profile));
        Assert.Equal(Known[^1].Plugins, upgraded.Plugins);
    }

    public static TheoryData<string?, bool> UnknownSchemas
    {
        get
        {
            var previous = Known[^2];
            return new()
            {
                { JsonSerializer.Serialize(Known[^1]), true },
                { JsonSerializer.Serialize(new MarkdownDocumentSchema(2, "rich", [new("youtrack", 2)])), true },
                { JsonSerializer.Serialize(previous with { Profile = "markdown-safe" }), true },
                { JsonSerializer.Serialize(previous with { Version = 1 }), true },
                { JsonSerializer.Serialize(previous with { Plugins = [.. previous.Plugins, new("custom", 1)] }), true },
                { JsonSerializer.Serialize(previous with { Plugins = previous.Plugins.Where(plugin => plugin.Id != "math").ToArray() }), true },
                { null, true },
                { JsonSerializer.Serialize(previous), false },
            };
        }
    }

    [Theory]
    [MemberData(nameof(UnknownSchemas))]
    public void Current_unknown_legacy_and_unbound_schemas_are_never_upgraded(string? schemaJson, bool bound)
    {
        var generation = Guid.NewGuid();
        var state = new PageCollaboration { Generation = generation, SchemaBound = bound, SchemaJson = schemaJson };
        CollaborationSchema.UpgradeKnownSchema(state);
        Assert.Equal(generation, state.Generation);
        Assert.Equal(schemaJson, state.SchemaJson);
    }

    // 1.2.0 incident (COVID-19 page): an OpenAPI block saved by an editor 0.6.0 session vanished when editor 0.7.2 opened
    // the old live state, and the next save persisted the loss. With everything saved, such states start from the revision.
    private const string WithApi = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Test"}]},{"type":"openapi","attrs":{"data":{"version":1,"filename":"a.yaml","source":"openapi: 3.1.0"}}}]}""";
    private const string WithoutApi = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Test"}]}]}""";
    private static PageRevision Revision(string document) => new() { Id = Guid.NewGuid(), ContentJson = $$"""{"format":"atlas-rich","version":1,"document":{{document}}}""" };
    private static PageCollaboration Live(MarkdownDocumentSchema schema, string projection, long sequence = 4, long saved = 4) => new()
    {
        Generation = Guid.NewGuid(), SchemaBound = true, SchemaJson = JsonSerializer.Serialize(schema), State = [1, 2, 3], StateSha256 = new string('0', 64),
        ProjectionJson = projection, Sequence = sequence, LastSavedSequence = saved,
    };

    [Fact]
    public void A_saved_state_of_an_older_editor_or_one_that_lost_saved_content_starts_from_the_revision()
    {
        Assert.True(CollaborationSchema.StartsFromRevision(Live(Known[^2], WithApi), Revision(WithApi)));
        Assert.True(CollaborationSchema.StartsFromRevision(Live(Known[^1], WithoutApi), Revision(WithApi)));
        Assert.False(CollaborationSchema.StartsFromRevision(Live(Known[^1], WithApi), Revision(WithApi)));
    }

    [Fact]
    public void Unsaved_changes_and_a_fresh_page_keep_their_live_state()
    {
        // Changes nobody has saved yet live only in the relay: they are never dropped, even from an older editor.
        Assert.False(CollaborationSchema.StartsFromRevision(Live(Known[^2], WithoutApi, sequence: 6, saved: 4), Revision(WithApi)));
        Assert.False(CollaborationSchema.StartsFromRevision(new PageCollaboration { Generation = Guid.NewGuid() }, Revision(WithApi)));
        // The first writer's seed snapshot, with the editor's default attributes: resetting it moved the second writer to
        // another generation than the first (1.2.1 regression).
        const string seeded = """{"type":"doc","content":[{"type":"paragraph","attrs":{"textAlign":null},"content":[{"type":"text","text":"Test"}]}]}""";
        Assert.False(CollaborationSchema.StartsFromRevision(Live(Known[^1], seeded, sequence: 0, saved: 0), Revision(WithoutApi)));
    }

    [Fact]
    public void A_revision_stored_without_default_attributes_still_matches_the_projection_that_writes_them()
    {
        // The projection writes every default; the save stores the same document without them (RichDocument.WithoutDefaultAttributes).
        const string projected = """{"type":"doc","content":[{"type":"paragraph","attrs":{"textAlign":null,"dependencies":false},"content":[{"type":"text","text":"Test"}]}]}""";
        Assert.False(CollaborationSchema.StartsFromRevision(Live(Known[^1], projected), Revision(WithoutApi)));
        Assert.True(CollaborationSchema.StartsFromRevision(Live(Known[^1], projected), Revision(WithApi)));
    }

    /// <summary>tests/fixtures/host-document-schema.json: the schema the web tests assert the editor binds.</summary>
    private static MarkdownDocumentSchema LoadWebFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "host-document-schema.json");
        return JsonSerializer.Deserialize<MarkdownDocumentSchema>(File.ReadAllText(path), JsonSerializerOptions.Web)!;
    }
}
