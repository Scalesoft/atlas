using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas.Markdown.Sync;

/// <summary>
/// The document schema of an editor configuration, as <c>describeMarkdownSchema</c> of <c>@atlas/markdown</c> exports
/// it: nodes and marks with their attributes, defaults and value constraints, content expressions, allowed and excluded
/// marks, and the plugin that owns each of them. <see cref="Builtin"/> is the manifest of every built-in plugin, generated
/// in the editor repository (packages/markdown/schema/markdown-schema.json) and embedded in this package; a host with
/// another configuration exports its own and <see cref="Parse(string)"/>s it, or narrows the built-in one with
/// <see cref="WithPlugins"/>. Immutable and thread-safe.
/// </summary>
public sealed class MarkdownSchemaManifest
{
    internal const string BuiltinResource = "Atlas.Markdown.Sync.markdown-schema.json";
    private static readonly Lazy<MarkdownSchemaManifest> BuiltinManifest = new(LoadBuiltin);
    private readonly JsonElement _root;

    private MarkdownSchemaManifest(JsonElement root, IReadOnlySet<string>? keep)
    {
        _root = root;
        if (!root.TryGetProperty("format", out var format) || format.GetString() != "atlas-markdown-schema"
            || !root.TryGetProperty("version", out var version) || version.GetInt32() != 1)
            throw new InvalidDataException("Not a atlas-markdown-schema manifest of version 1.");
        Profile = root.GetProperty("profile").GetString()!;
        SchemaRevision = root.GetProperty("schemaRevision").GetInt32();
        TopNode = root.GetProperty("topNode").GetString()!;
        var allPlugins = root.GetProperty("plugins").EnumerateArray()
            .Select(plugin => new MarkdownSchemaPluginInfo(plugin.GetProperty("id").GetString()!, plugin.GetProperty("version").GetInt32(),
                plugin.GetProperty("migratesFrom").EnumerateArray().Select(from => from.GetInt32()).ToArray()))
            .ToArray();
        if (keep?.FirstOrDefault(id => allPlugins.All(plugin => plugin.Id != id)) is { } unknown)
            throw new ArgumentException($"The manifest has no plugin '{unknown}'.", nameof(keep));
        bool Kept(string? owner) => owner is null || keep is null || keep.Contains(owner);
        Plugins = allPlugins.Where(plugin => Kept(plugin.Id)).ToFrozenDictionary(plugin => plugin.Id, StringComparer.Ordinal);

        SchemaAttribute[] Attributes(JsonElement owner, string? ownerPlugin) => owner.GetProperty("attrs").EnumerateArray()
            .Select(attribute => (attribute, owner: attribute.TryGetProperty("owner", out var value) ? value.GetString() : ownerPlugin))
            .Where(item => Kept(item.owner))
            .Select(item => new SchemaAttribute(item.attribute.GetProperty("name").GetString()!, item.owner,
                item.attribute.TryGetProperty("default", out var fallback) ? fallback.Clone() : null,
                ValueConstraint.Parse(item.attribute.GetProperty("constraint")),
                item.attribute.GetProperty("enforcement").GetString() == "server",
                item.attribute.TryGetProperty("validated", out var validated) && validated.GetBoolean()))
            .ToArray();
        string? OwnerOf(JsonElement element) => element.TryGetProperty("owner", out var owner) ? owner.GetString() : null;

        var marks = root.GetProperty("marks").EnumerateArray().Where(mark => Kept(OwnerOf(mark))).ToArray();
        var markNames = marks.Select(mark => mark.GetProperty("name").GetString()!).ToFrozenSet(StringComparer.Ordinal);
        Marks = marks.Select((mark, rank) =>
            {
                var attributes = Attributes(mark, OwnerOf(mark));
                return new SchemaMark(mark.GetProperty("name").GetString()!, rank, OwnerOf(mark),
                    mark.GetProperty("excludes").EnumerateArray().Select(name => name.GetString()!).Where(markNames.Contains).ToFrozenSet(StringComparer.Ordinal),
                    attributes.ToFrozenDictionary(attribute => attribute.Name, StringComparer.Ordinal)) { Declared = attributes };
            })
            .ToFrozenDictionary(mark => mark.Name, StringComparer.Ordinal);

        var nodes = root.GetProperty("nodes").EnumerateArray().Where(node => Kept(OwnerOf(node))).ToArray();
        var names = nodes.Select(node => node.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
        // Groups of every node, members of the kept ones: a group the removed plugins emptied matches nothing.
        var groups = root.GetProperty("nodes").EnumerateArray()
            .SelectMany(node => node.GetProperty("groups").EnumerateArray().Select(group => (group: group.GetString()!, name: node.GetProperty("name").GetString()!)))
            .GroupBy(item => item.group, item => item.name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Where(names.Contains).ToArray(), StringComparer.Ordinal);
        Nodes = nodes.Select(node =>
            {
                var content = node.GetProperty("content").GetString()!;
                var attributes = Attributes(node, OwnerOf(node));
                return new SchemaNode(node.GetProperty("name").GetString()!, OwnerOf(node),
                    node.TryGetProperty("text", out var text) && text.GetBoolean(), node.GetProperty("inline").GetBoolean(), content,
                    content.Length == 0 ? null : ContentExpression.Compile(content, names, groups),
                    node.GetProperty("marks").EnumerateArray().Select(name => name.GetString()!).Where(markNames.Contains).ToFrozenSet(StringComparer.Ordinal),
                    attributes.ToFrozenDictionary(attribute => attribute.Name, StringComparer.Ordinal))
                {
                    Repeat = ContentExpression.Repeat(content, names, groups), Members = ContentExpression.Members(content, names, groups), Declared = attributes,
                };
            })
            .ToFrozenDictionary(node => node.Name, StringComparer.Ordinal);
        NodeOrder = nodes.Select(node => node.GetProperty("name").GetString()!).ToArray();
        if (!Nodes.ContainsKey(TopNode)) throw new InvalidDataException($"The top node '{TopNode}' is not a node of the manifest.");

        var limits = root.GetProperty("limits");
        MaxTableCells = limits.GetProperty("maxTableCells").GetInt32();
        MaxTableSpan = limits.GetProperty("maxTableSpan").GetInt32();
        ValidatorNames = Nodes.Values.SelectMany(node => node.Attributes.Values).Concat(Marks.Values.SelectMany(mark => mark.Attributes.Values))
            .Select(attribute => attribute.Constraint.Validator).OfType<string>().ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>The manifest of every built-in plugin of the editor this package was released with.</summary>
    public static MarkdownSchemaManifest Builtin => BuiltinManifest.Value;

    /// <summary>"rich" or "markdown-safe".</summary>
    public string Profile { get; }

    /// <summary>The built-in content schema revision (<see cref="MarkdownDocumentSchema.Version"/>).</summary>
    public int SchemaRevision { get; }

    public string TopNode { get; }

    /// <summary>The content plugins of this configuration: schema version and the older versions the editor migrates.</summary>
    public FrozenDictionary<string, MarkdownSchemaPluginInfo> Plugins { get; }

    /// <summary>Node type names.</summary>
    public IEnumerable<string> NodeNames => Nodes.Keys;

    /// <summary>Mark type names, in rank order.</summary>
    public IEnumerable<string> MarkNames => Marks.Values.OrderBy(mark => mark.Rank).Select(mark => mark.Name);

    /// <summary>The attribute names of a node type (empty for an unknown one).</summary>
    public IEnumerable<string> AttributesOf(string node) => Nodes.TryGetValue(node, out var type) ? type.Attributes.Keys : [];

    /// <summary>The default of an attribute of a node or mark type (what the editor's JSON holds when it was never set); null when it has
    /// none or the type or attribute is unknown.</summary>
    public JsonElement? AttributeDefault(string type, string attribute)
        => (Nodes.TryGetValue(type, out var node) ? node.Attributes : Marks.TryGetValue(type, out var mark) ? mark.Attributes : null)
            ?.GetValueOrDefault(attribute)?.Default;

    /// <summary>The names of the structured validators the constraints use.</summary>
    public FrozenSet<string> ValidatorNames { get; }

    internal FrozenDictionary<string, SchemaNode> Nodes { get; }

    /// <summary>Node type names in schema order (ProseMirror's order, in which content expressions offer a group's members).</summary>
    internal IReadOnlyList<string> NodeOrder { get; }

    internal FrozenDictionary<string, SchemaMark> Marks { get; }

    internal int MaxTableCells { get; }

    internal int MaxTableSpan { get; }

    /// <summary>Reads a manifest exported by <c>describeMarkdownSchema</c>.</summary>
    /// <exception cref="InvalidDataException">The JSON is not a valid manifest.</exception>
    public static MarkdownSchemaManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Create(document.RootElement.Clone(), null);
    }

    /// <inheritdoc cref="Parse(string)"/>
    public static MarkdownSchemaManifest Parse(ReadOnlySpan<byte> utf8Json) => Parse(Encoding.UTF8.GetString(utf8Json));

    /// <summary>
    /// The same manifest for a host that installs only some of its plugins: nodes, marks and attributes of the other
    /// plugins are removed (a document using them is invalid), and group content expressions no longer offer them.
    /// </summary>
    /// <exception cref="ArgumentException">A plugin id is not in the manifest.</exception>
    public MarkdownSchemaManifest WithPlugins(IEnumerable<string> pluginIds)
        => Create(_root, pluginIds.ToHashSet(StringComparer.Ordinal));

    private static MarkdownSchemaManifest Create(JsonElement root, IReadOnlySet<string>? keep)
    {
        try
        {
            return new MarkdownSchemaManifest(root, keep);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("The schema manifest is malformed: " + error.Message, error);
        }
    }

    private static MarkdownSchemaManifest LoadBuiltin()
    {
        using var stream = typeof(MarkdownSchemaManifest).Assembly.GetManifestResourceStream(BuiltinResource)
            ?? throw new InvalidOperationException($"Missing embedded resource {BuiltinResource}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }
}

/// <summary>A content plugin of a schema manifest: its schema version and the older versions the editor migrates on load.</summary>
public sealed record MarkdownSchemaPluginInfo(string Id, int Version, IReadOnlyList<int> MigratesFrom);

/// <param name="Default">The default value, or null when the attribute is required (no default).</param>
/// <param name="ServerRule">True where the editor stores the value unvalidated and only servers enforce the constraint.</param>
/// <param name="Validated">True where the attribute declares its own validator, which ProseMirror (1.25.12 and later) runs whenever it creates a node or mark with an explicit value: the editor builds no such node from an invalid value, and y-tiptap deletes the element from the shared document.</param>
internal sealed record SchemaAttribute(string Name, string? Owner, JsonElement? Default, ValueConstraint Constraint, bool ServerRule, bool Validated = false);

internal sealed record SchemaNode(string Name, string? Owner, bool IsText, bool IsInline, string ContentExpression, Regex? Content,
    FrozenSet<string> AllowedMarks, FrozenDictionary<string, SchemaAttribute> Attributes)
{
    /// <summary>For an expression "set*" or "set+" (a name, a group or an alternation of them): the set and its minimum.</summary>
    public (FrozenSet<string> Set, int Minimum)? Repeat { get; init; }

    /// <summary>Every node type the content expression names (empty for a leaf).</summary>
    public FrozenSet<string> Members { get; init; } = FrozenSet<string>.Empty;

    /// <summary>The attributes in declaration order, ProseMirror's attrs order that toJSON() keeps.</summary>
    public IReadOnlyList<SchemaAttribute> Declared { get; init; } = [];

    /// <summary>Whether the child types satisfy the content expression; a leaf takes none.</summary>
    public bool Accepts(IReadOnlyList<string> children)
    {
        if (Content is null) return children.Count == 0;
        if (Repeat is { } repeat) return children.Count >= repeat.Minimum && children.All(repeat.Set.Contains);
        return Content.IsMatch(string.Concat(children.Select(child => child + ";")));
    }
}

internal sealed record SchemaMark(string Name, int Rank, string? Owner, FrozenSet<string> Excludes, FrozenDictionary<string, SchemaAttribute> Attributes)
{
    /// <inheritdoc cref="SchemaNode.Declared"/>
    public IReadOnlyList<SchemaAttribute> Declared { get; init; } = [];
}

/// <summary>
/// ProseMirror content expressions ("paragraph block*", "(tableCell | tableHeader)*") as a regular expression over the child
/// types, each written as "name;". Same language as ProseMirror's ContentMatch automaton; the non-backtracking engine keeps the
/// match linear in the number of children.
/// </summary>
internal static partial class ContentExpression
{
    [GeneratedRegex(@"\w+|[^\s\w]", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    [GeneratedRegex(@"^\(?\s*(\w+(?:\s*\|\s*\w+)*)\s*\)?\s*([*+])$", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatPattern();

    /// <summary>"x*", "x+", "(x | y)*": the node types of the alternation and the minimum count, or null for any other expression.</summary>
    public static (FrozenSet<string> Set, int Minimum)? Repeat(string expression, IReadOnlySet<string> names, IReadOnlyDictionary<string, string[]> groups)
    {
        var match = RepeatPattern().Match(expression.Trim());
        if (!match.Success || (expression.TrimStart().StartsWith('(') != expression.TrimEnd()[..^1].TrimEnd().EndsWith(')'))) return null;
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in match.Groups[1].Value.Split('|', StringSplitOptions.TrimEntries))
        {
            if (names.Contains(name)) types.Add(name);
            else if (groups.TryGetValue(name, out var members)) types.UnionWith(members);
            else return null;
        }
        return (types.ToFrozenSet(StringComparer.Ordinal), match.Groups[2].Value == "+" ? 1 : 0);
    }

    /// <summary>The node types an expression names, directly or by group.</summary>
    public static FrozenSet<string> Members(string expression, IReadOnlySet<string> names, IReadOnlyDictionary<string, string[]> groups)
        => Tokens().Matches(expression).Select(token => token.Value)
            .SelectMany(token => names.Contains(token) ? [token] : groups.GetValueOrDefault(token) ?? [])
            .ToFrozenSet(StringComparer.Ordinal);

    public static Regex Compile(string expression, IReadOnlySet<string> names, IReadOnlyDictionary<string, string[]> groups)
    {
        var tokens = Tokens().Matches(expression).Select(x => x.Value).ToArray();
        var position = 0;
        var pattern = Choice();
        if (position != tokens.Length) throw new InvalidDataException($"Unexpected token in content expression '{expression}'.");
        return new Regex($"^(?:{pattern})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

        string Choice()
        {
            var alternatives = new List<string> { Sequence() };
            while (Accept("|")) alternatives.Add(Sequence());
            return alternatives.Count == 1 ? alternatives[0] : $"(?:{string.Join('|', alternatives)})";
        }

        string Sequence()
        {
            var parts = new StringBuilder();
            while (position < tokens.Length && tokens[position] is not (")" or "|")) parts.Append(Subscript());
            return parts.ToString();
        }

        string Subscript()
        {
            var atom = Atom();
            while (true)
            {
                if (Accept("*")) atom = $"(?:{atom})*";
                else if (Accept("+")) atom = $"(?:{atom})+";
                else if (Accept("?")) atom = $"(?:{atom})?";
                else if (Accept("{"))
                {
                    var min = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                    var max = min.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (Accept(",")) max = tokens[position] == "}" ? "" : Next();
                    Expect("}");
                    atom = $"(?:{atom}){{{min},{max}}}";
                }
                else return atom;
            }
        }

        string Atom()
        {
            if (Accept("("))
            {
                var inner = Choice();
                Expect(")");
                return $"(?:{inner})";
            }
            var name = Next();
            var types = names.Contains(name) ? [name] : groups.TryGetValue(name, out var members) ? members
                : throw new InvalidDataException($"Unknown node type or group '{name}' in content expression '{expression}'.");
            return types.Length == 0 ? "(?!)" : $"(?:{string.Join('|', types.Select(type => Regex.Escape(type) + ";"))})";
        }

        bool Accept(string token)
        {
            if (position < tokens.Length && tokens[position] == token)
            {
                position++;
                return true;
            }
            return false;
        }

        string Next() => position < tokens.Length ? tokens[position++] : throw new InvalidDataException($"Unexpected end of content expression '{expression}'.");

        void Expect(string token)
        {
            if (!Accept(token)) throw new InvalidDataException($"Expected '{token}' in content expression '{expression}'.");
        }
    }
}
