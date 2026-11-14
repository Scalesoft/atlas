using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Collaboration.Domain;

public static class CollaborationSchema
{
    /// <summary>
    /// Every schema an Atlas web release has bound, oldest first. The last entry is the schema the current web
    /// negotiates; each entry adds plugins to its predecessor or raises the revision or a plugin's version
    /// (whose older documents stay valid content). Appending an entry ships together with the web
    /// that registers the plugin: OpenCollaboration lazily moves a document bound to an older entry to a new
    /// generation, after which a client still negotiating the older schema gets a permanent SCHEMA_MISMATCH and
    /// must reload.
    /// </summary>
    public static IReadOnlyList<MarkdownDocumentSchema> KnownHostSchemas { get; } =
    [
        HostSchema([new("youtrack", 1)]),
        HostSchema([new("attachments", 1), new("heading-anchors", 1), new("youtrack", 1)]),
        HostSchema([new("attachments", 1), new("charts", 1), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 1), new("math", 1), new("toc", 1), new("youtrack", 1)]),
        HostSchema([new("attachments", 1), new("charts", 1), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 1), new("math", 1), new("openapi", 1), new("toc", 1), new("youtrack", 1)]),
        // WP4 V6: password references (the secrets plugin, schema v4).
        HostSchema([new("attachments", 1), new("charts", 1), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 1), new("math", 1), new("openapi", 1), new("secrets", 4), new("toc", 1), new("youtrack", 1)]),
        // Concealed content: text readers uncover under invisible ink (visual only, not a secret).
        HostSchema([new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 1), new("math", 1), new("openapi", 1), new("secrets", 4), new("toc", 1), new("youtrack", 1)]),
        // Page links: `[[` and the slash menu insert a page reference (the pages plugin).
        HostSchema([new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 1), new("math", 1), new("openapi", 1), new("pages", 1), new("secrets", 4), new("toc", 1),
            new("youtrack", 1)]),
        // Editor 0.7.0: built-in rich revision 3 (heading level 4, the caution callout). Same plugins; a 0.6.x tab gets
        // SCHEMA_MISMATCH and reloads instead of dropping caution callouts it cannot build.
        HostSchema([new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 1), new("math", 1), new("openapi", 1), new("pages", 1), new("secrets", 4), new("toc", 1),
            new("youtrack", 1)], revision: 3),
        // Atlas 3 Projector: requirement blocks and the deployment summary (Atlas's own plugin, src/Atlas.Web/src/projector).
        HostSchema([new("atlas-requirements", 1), new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 1), new("math", 1), new("openapi", 1), new("pages", 1), new("secrets", 4), new("toc", 1),
            new("youtrack", 1)], revision: 3),
        // Rich revision 4: inline code combines with the other marks (Atlas 2.x stored bold and italic code). A revision 3 tab
        // would delete such a paragraph, so it gets SCHEMA_MISMATCH and reloads.
        HostSchema([new("atlas-requirements", 1), new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 1), new("math", 1), new("openapi", 1), new("pages", 1), new("secrets", 4), new("toc", 1),
            new("youtrack", 1)], revision: 4),
        // Atlas 3.4: atlas-requirements schema 2 (the overview node; the requirement's stateLabel and system). A 3.3 tab would delete
        // them from the shared document, so it gets SCHEMA_MISMATCH and reloads; version 1 content is valid version 2 content.
        HostSchema([new("atlas-requirements", 2), new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 1), new("math", 1), new("openapi", 1), new("pages", 1), new("secrets", 4), new("toc", 1),
            new("youtrack", 1)], revision: 4),
        // Atlas 3.6: rich revision 5, the timed list (timedList, timedItem). A 3.5 tab would delete it from the shared document, so it
        // gets SCHEMA_MISMATCH and reloads; revision 4 content is valid revision 5 content.
        HostSchema([new("atlas-requirements", 2), new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 1), new("math", 1), new("openapi", 1), new("pages", 1), new("secrets", 4), new("toc", 1),
            new("youtrack", 1)], revision: 5),
        // Atlas 3.6: a requirement is a heading. atlas-requirements 3 (the heading's state, stateLabel, direction, system and from; the
        // paragraph's dependencies) and heading-anchors 2 (a dot in the id, aliases). A tab of the previous schema would strip the
        // attributes or refuse a dotted id, so it gets SCHEMA_MISMATCH and reloads; older content is valid content of both.
        HostSchema([new("atlas-requirements", 3), new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 2), new("math", 1), new("openapi", 1), new("pages", 1), new("secrets", 4), new("toc", 1),
            new("youtrack", 1)], revision: 5),
        // Atlas 3.6: rich revision 6, the disclosure's title is its first child `disclosureTitle` (text written in place). A tab of the
        // previous schema would delete the title nodes, so it gets SCHEMA_MISMATCH and reloads; revision 5 content is valid revision 6
        // content (the title attribute stays readable until the content repair moves it).
        HostSchema([new("atlas-requirements", 3), new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 2), new("math", 1), new("openapi", 1), new("pages", 1), new("secrets", 4), new("toc", 1),
            new("youtrack", 1)], revision: 6),
        // Atlas 3.6: rich revision 7, no disclosure block (every heading folds; the content repair makes a disclosure a heading and its
        // body). A tab of the previous schema could still insert one, so it gets SCHEMA_MISMATCH and reloads.
        HostSchema([new("atlas-requirements", 3), new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 2), new("math", 1), new("openapi", 1), new("pages", 1), new("secrets", 4), new("toc", 1),
            new("youtrack", 1)], revision: 7),
        // Atlas 3.6: atlas-requirements 4, the requirement's party is the heading's party and partyContactId (shape §6). A tab of the
        // previous schema would strip them, so it gets SCHEMA_MISMATCH and reloads; version 3 content is moved by the server (job and open).
        HostSchema([new("atlas-requirements", 4), new("attachments", 1), new("charts", 1), new("concealed", 2), new("contacts", 2), new("diagrams", 2),
            new("emoji", 1), new("heading-anchors", 2), new("math", 1), new("openapi", 1), new("pages", 1), new("secrets", 4), new("toc", 1),
            new("youtrack", 1)], revision: 7),
    ];

    /// <summary>Plugin ids any Atlas web release has negotiated.</summary>
    private static readonly FrozenSet<string> KnownPluginIds = KnownHostSchemas.SelectMany(x => x.Plugins).Select(x => x.Id).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// A schema a client negotiates is stored (<see cref="PageCollaboration.SchemaJson"/>), so it must be one Atlas could have sent: the
    /// rich profile, a known revision and plugins Atlas knows, each once, at a bounded version (an unknown version is then refused
    /// by DocumentSchemaCompatibility, never guessed). No schema (a client before negotiation) stays allowed.
    /// </summary>
    public static void RequireAcceptable(MarkdownDocumentSchema? schema)
    {
        if (schema is not null && (schema.Profile != "rich" || schema.Version < 1 || schema.Version > KnownHostSchemas.Max(x => x.Version)
            || schema.Plugins.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != schema.Plugins.Count()
            || schema.Plugins.Any(x => x.Id is null || !KnownPluginIds.Contains(x.Id) || x.Version is < 1 or > MaxPluginVersion)))
        {
            throw new InvalidDataException("The negotiated document schema is not one Atlas knows.");
        }
    }

    private const int MaxPluginVersion = 64;

    /// <summary>Schema versions of the plugins of the current host schema (the last of <see cref="KnownHostSchemas"/>), by plugin id.</summary>
    public static IReadOnlyDictionary<string, int> CurrentPluginVersions { get; } = KnownHostSchemas[^1].Plugins
        .ToFrozenDictionary(x => x.Id, x => x.Version, StringComparer.Ordinal);

    public static MarkdownDocumentSchema? Deserialize(string? json)
    {
        return json is null ? null : DocumentSchemaCompatibility.Normalize(JsonSerializer.Deserialize<MarkdownDocumentSchema>(json)!);
    }

    /// <summary>Known host releases only, never arbitrary client negotiation. True when the state moved to a new generation.</summary>
    public static bool UpgradeKnownSchema(PageCollaboration state)
    {
        if (!BoundToPreviousHostSchema(state))
        {
            return false;
        }
        // Migration017 cascades this parent-key update to EXISTING log rows.
        // Page/state locks and the surrounding transaction protect every opaque byte.
        state.Generation = Guid.NewGuid();
        // Newer host releases add node types or attribute values and leave the opaque Yjs data as it is, with one exception:
        // atlas-requirements 3 → 4 moves a requirement's party from its facts line into the heading (shape §6), which the server does
        // as a live write after the commit (LiveRequirementsUpgrade), as the editor's own migration does.
        state.SchemaJson = JsonSerializer.Serialize(KnownHostSchemas[^1]);
        state.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// True when the live relay state should be dropped and the next session seeded from the saved revision: every change
    /// is saved (so nothing is lost), and the state was written by an older editor release (bound to a previous host
    /// schema) or, once it holds updates, its last projection is not the revision it is based on. Opening such a state let
    /// editor 1.2.0 drop an OpenAPI block an editor 0.6.0 session had saved, and the next save persisted the loss.
    /// </summary>
    public static bool StartsFromRevision(PageCollaboration state, PageRevision revision)
    {
        if (state.StateSha256 is null || state.Sequence != state.LastSavedSequence)
        {
            return false;
        }
        if (BoundToPreviousHostSchema(state))
        {
            return true;
        }
        // A generation without updates holds only the relay's seed of this very revision, projected by the current editor
        // (with default attributes, so it rarely equals the stored JSON). Reseeding would rebuild the same state and move
        // the writers already in the session to a dead generation.
        // Sequence first: ProjectionJson is a lazy column, loaded from the database only when updates make it matter.
        if (state.Sequence == 0 || state.ProjectionJson is null)
        {
            return false;
        }
        // Without attributes at their default: the projection writes every default, a revision is stored without them.
        return !JsonNode.DeepEquals(RichDocument.DocumentWithoutDefaults(state.ProjectionJson), RichDocument.DocumentWithoutDefaults(revision.ContentJson));
    }

    /// <summary>Whether OpenCollaboration would hand out this state as it is: neither reset to the revision nor moved to a new generation
    /// by <see cref="UpgradeKnownSchema"/>.</summary>
    public static bool OpensAsIs(PageCollaboration state, PageRevision current) => !BoundToPreviousHostSchema(state) && !StartsFromRevision(state, current);

    private static bool BoundToPreviousHostSchema(PageCollaboration state) =>
        state.SchemaBound && Deserialize(state.SchemaJson) is { } schema && IsPreviousHostSchema(schema);

    private static bool IsPreviousHostSchema(MarkdownDocumentSchema schema) => KnownHostSchemas
        .SkipLast(1)
        .Any(known => known.Version == schema.Version && known.Profile == schema.Profile && known.Plugins.SequenceEqual(schema.Plugins));

    private static MarkdownDocumentSchema HostSchema(MarkdownSchemaPlugin[] plugins, int revision = 2)
    {
        return DocumentSchemaCompatibility.Normalize(new MarkdownDocumentSchema(revision, "rich", plugins));
    }
}
