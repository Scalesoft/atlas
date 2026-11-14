using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using static Atlas.Markdown.Sync.JsValues;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Where a rich document breaks the editor schema: a JSON path ("document.content[12].attrs.width"), a stable
/// <see cref="MarkdownDocumentViolationCodes">code</see> and an English reason.
/// </summary>
public sealed record MarkdownDocumentViolation(string Path, string Code, string Message)
{
    public override string ToString() => $"{Path}: {Message} ({Code})";
}

/// <summary>The stable codes of <see cref="MarkdownDocumentViolation.Code"/>.</summary>
public static class MarkdownDocumentViolationCodes
{
    /// <summary>Not JSON, or nested deeper than <see cref="MarkdownDocumentValidator.JsonMaxDepth"/>.</summary>
    public const string InvalidJson = "invalid_json";
    /// <summary>The envelope is not {format: "atlas-rich", version: 1, plugins?, document}.</summary>
    public const string InvalidEnvelope = "invalid_envelope";
    /// <summary>The plugins field or one of its requirements is malformed or repeated.</summary>
    public const string InvalidPlugins = "invalid_plugins";
    /// <summary>A requirement names a plugin the host did not install.</summary>
    public const string UnknownPlugin = "unknown_plugin";
    /// <summary>A requirement names a plugin version the editor neither has nor migrates.</summary>
    public const string UnsupportedPluginVersion = "unsupported_plugin_version";
    /// <summary>A node, mark or attribute of a plugin the document does not require in its plugins field.</summary>
    public const string MissingPlugin = "missing_plugin";
    /// <summary>A node or mark that is not an object or has no type name.</summary>
    public const string InvalidNode = "invalid_node";
    public const string UnknownNode = "unknown_node";
    public const string UnknownMark = "unknown_mark";
    /// <summary>A field a node, mark or plugin requirement does not have.</summary>
    public const string UnknownField = "unknown_field";
    /// <summary>A property name repeated in one object.</summary>
    public const string DuplicateField = "duplicate_field";
    public const string UnknownAttribute = "unknown_attribute";
    /// <summary>An attribute value breaks its constraint (or attrs is not an object).</summary>
    public const string InvalidAttribute = "invalid_attribute";
    /// <summary>An absent attribute without a valid default.</summary>
    public const string MissingAttribute = "missing_attribute";
    /// <summary>A text node without non-empty text.</summary>
    public const string InvalidText = "invalid_text";
    /// <summary>Content that is not an array or does not match the node's content expression.</summary>
    public const string InvalidContent = "invalid_content";
    /// <summary>Marks that are not an array.</summary>
    public const string InvalidMarks = "invalid_marks";
    /// <summary>A mark the parent node does not allow in its content.</summary>
    public const string MarkNotAllowed = "mark_not_allowed";
    /// <summary>A mark repeated on one node, or two marks that exclude each other.</summary>
    public const string MarkConflict = "mark_conflict";
    /// <summary>Nodes nested deeper than <see cref="MarkdownDocumentValidator.MaxDepth"/>.</summary>
    public const string TooDeep = "too_deep";
    /// <summary>A table above the cell or span bound.</summary>
    public const string TableTooLarge = "table_too_large";
    /// <summary>Overlapping or missing cells, rowspans past the end, inconsistent column widths.</summary>
    public const string InvalidTable = "invalid_table";
}

/// <summary>
/// Validates a stored rich document (the <c>atlas-rich</c> envelope with its <c>plugins</c> requirements) against a
/// <see cref="MarkdownSchemaManifest"/>, so a host stores only documents the editor opens. It mirrors the editor's load path
/// (@atlas/markdown engine/valueValidation.ts): the envelope and plugin requirements, known node and mark types, allowed
/// fields and attributes, plugin ownership, ProseMirror's Node.check (content expressions, allowed marks, mark exclusion,
/// attribute validators) and engine/builtinDocumentValidation.ts (table bounds and geometry). Attribute values follow the
/// manifest's constraints, including the deliberately stricter server rules (<c>enforcement: "server"</c>): the JSON type of
/// attributes the editor stores unvalidated, heading levels 1–6 and no javascript:, vbscript: or data: links.
/// The work is linear in the document: JSON depth and node nesting are bounded, content expressions and patterns run on the
/// non-backtracking engine and tables are bounded before their geometry is mapped. Invalid input never throws.
/// Thread-safe; create one per manifest and reuse it.
/// </summary>
public sealed class MarkdownDocumentValidator
{
    /// <summary>The format id of a stored rich document envelope; everything that writes one writes this.</summary>
    public const string Format = "atlas-rich";

    /// <summary>Legacy id of documents stored before 5.0; every reader accepts it as <see cref="Format"/>.</summary>
    public const string LegacyFormat = "scalesoft-rich";

    /// <summary>Whether <paramref name="format"/> names a rich document envelope (<see cref="Format"/> or <see cref="LegacyFormat"/>).</summary>
    public static bool IsFormat(string? format) => format is Format or LegacyFormat;

    /// <summary>Nesting of nodes (the editor has no limit; 64 nodes are 31 nested list levels). It bounds the walk's recursion.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// The JSON nesting of a document within <see cref="MaxDepth"/> nodes: two levels per node (its object and content array)
    /// and at most five more below the deepest one (marks[].attrs, or component data such as data.contacts[].internalUser), with
    /// room for a wrapper.
    /// </summary>
    public const int JsonMaxDepth = 2 * MaxDepth + 8;

    /// <summary>Parser options a host should use for stored rich content.</summary>
    public static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = JsonMaxDepth };

    private static readonly Lazy<MarkdownDocumentValidator> BuiltinValidator = new(() => new MarkdownDocumentValidator(MarkdownSchemaManifest.Builtin));
    private readonly FrozenDictionary<string, MarkdownValueValidator> _validators;

    /// <param name="schema">The manifest of the host's editor configuration (<see cref="MarkdownSchemaManifest.Builtin"/>, optionally narrowed).</param>
    /// <param name="validators">Implementations of structured validators that third-party plugins name; built-in names may not be replaced.</param>
    /// <exception cref="ArgumentException">The manifest names a structured validator nobody implements.</exception>
    public MarkdownDocumentValidator(MarkdownSchemaManifest schema, IReadOnlyDictionary<string, MarkdownValueValidator>? validators = null)
    {
        Schema = schema;
        var all = new Dictionary<string, MarkdownValueValidator>(StructuredValidators.Builtin, StringComparer.Ordinal);
        foreach (var (name, validator) in validators ?? new Dictionary<string, MarkdownValueValidator>())
            if (!all.TryAdd(name, validator)) throw new ArgumentException($"The structured validator '{name}' is built in.", nameof(validators));
        if (schema.ValidatorNames.FirstOrDefault(name => !all.ContainsKey(name)) is { } missing)
            throw new ArgumentException($"The schema names the structured validator '{missing}'; pass its implementation.", nameof(validators));
        _validators = all.ToFrozenDictionary(StringComparer.Ordinal);
        // Defaults that pass a constraint reading no sibling attribute need no check per node.
        _validDefaults = schema.Nodes.Values.SelectMany(node => node.Attributes.Values).Concat(schema.Marks.Values.SelectMany(mark => mark.Attributes.Values))
            .Where(attribute => attribute.Constraint.Context.Length == 0 && attribute.Default is { } fallback && CheckAttribute(attribute, fallback, default) is null)
            .ToFrozenSet<SchemaAttribute>(ReferenceEqualityComparer.Instance);
    }

    private readonly FrozenSet<SchemaAttribute> _validDefaults;

    /// <summary>The validator of every built-in plugin (<see cref="MarkdownSchemaManifest.Builtin"/>).</summary>
    public static MarkdownDocumentValidator Builtin => BuiltinValidator.Value;

    public MarkdownSchemaManifest Schema { get; }

    /// <summary>The first violation of a stored envelope, or null when the editor opens it. Unparsable JSON is a violation at "$".</summary>
    public MarkdownDocumentViolation? Validate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, JsonOptions);
            return Validate(document.RootElement);
        }
        catch (JsonException)
        {
            return InvalidJson();
        }
    }

    /// <inheritdoc cref="Validate(string)"/>
    public MarkdownDocumentViolation? Validate(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json, JsonOptions);
            return Validate(document.RootElement);
        }
        catch (JsonException)
        {
            return InvalidJson();
        }
    }

    /// <inheritdoc cref="Validate(string)"/>
    public MarkdownDocumentViolation? Validate(JsonElement envelope)
    {
        try
        {
            new Walker(this, shallow: false, merged: false).Envelope(envelope);
            return null;
        }
        catch (ViolationException violation)
        {
            return violation.Violation;
        }
    }

    private static MarkdownDocumentViolation InvalidJson()
        => new("$", MarkdownDocumentViolationCodes.InvalidJson, $"is not JSON or is nested deeper than {MaxDepth} node levels");

    /// <summary>
    /// One node of a document whose plugin requirements are <paramref name="required"/>, at <paramref name="depth"/> (the top
    /// node is 1). Children written as a type name, or carrying <see cref="ShallowMarker"/>, were validated on their own: only
    /// their type, owner and marks are read (the collaboration guard validates the elements an update touched this way).
    /// </summary>
    /// <param name="childNames">The node's children as type names, when the node JSON has no content (all validated on their own).</param>
    /// <param name="merged">
    /// The node may be what Yjs made of concurrent collaborative edits that were each valid: the editor shows such a document
    /// (y-prosemirror builds nodes without checking their content) and repairs it, so refusing it would stop the session of
    /// the peer whose update arrives second. Found by YjsGuardFuzzTests in real editor sessions: marks that exclude each other
    /// meet (one peer formats text as code while another's bold covers it, or Yjs cleaned a format up differently per
    /// history), quotes and lists are emptied (two peers delete one child each), tables lose cells or all rows (concurrent row
    /// and column edits), and a cell's colwidth meets another peer's colspan. Moving or restoring (undo) such an element
    /// creates it anew with the same content. So: children only have to be types the content expression names, a table only
    /// keeps its size maxima, marks may exclude each other, and a rule that reads sibling attributes checks the value alone.
    /// Names, attribute values, marks allowed by the parent, plugin ownership and depth still hold: an element's type,
    /// parent and depth never change in Yjs, so no merge breaks them.
    /// </param>
    internal MarkdownDocumentViolation? ValidateNode(JsonElement node, int depth, IReadOnlySet<string> required, bool top, IReadOnlyList<string>? childNames = null,
        bool merged = false)
    {
        try
        {
            var walker = new Walker(this, shallow: true, merged) { ChildNames = childNames };
            walker.Require(required);
            if (top && (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("type", out var type) || type.GetString() != Schema.TopNode))
                throw new ViolationException("document", MarkdownDocumentViolationCodes.InvalidEnvelope, $"must be a node of type {Schema.TopNode}");
            walker.Node(node, "node", depth);
            return null;
        }
        catch (ViolationException violation)
        {
            return violation.Violation;
        }
    }

    /// <summary>
    /// A field naming a child the walk does not descend into (internal to the collaboration guard, which also writes such a
    /// child as a bare type-name string).
    /// </summary>
    internal const string ShallowMarker = "$shallow";

    internal string? CheckAttribute(SchemaAttribute attribute, JsonElement value, JsonElement attrs) => attribute.Constraint.Check(value, attrs, _validators);

    private sealed class ViolationException(JsonPath path, string code, string message) : Exception(message)
    {
        public MarkdownDocumentViolation Violation { get; } = new(path.ToString(), code, message);
    }

    /// <summary>A JSON path built only when a violation names it (the walk allocates a link per node, no strings).</summary>
    private sealed class JsonPath(JsonPath? parent, string? name, int index)
    {
        public static implicit operator JsonPath(string text) => new(null, text, -1);

        public JsonPath Field(string field) => new(this, field, -1);

        public JsonPath Item(int position) => new(this, null, position);

        public override string ToString() => parent is null ? name! : name is null ? $"{parent}[{index}]" : $"{parent}.{name}";
    }

    private sealed class Walker(MarkdownDocumentValidator validator, bool shallow, bool merged)
    {
        private static readonly FrozenSet<string> EnvelopeFields = new[] { "format", "version", "plugins", "document" }.ToFrozenSet(StringComparer.Ordinal);
        private static readonly FrozenSet<string> NodeFields = new[] { "type", "attrs", "content", "marks" }.ToFrozenSet(StringComparer.Ordinal);
        private static readonly FrozenSet<string> TextFields = new[] { "type", "text", "marks" }.ToFrozenSet(StringComparer.Ordinal);
        private static readonly FrozenSet<string> MarkFields = new[] { "type", "attrs" }.ToFrozenSet(StringComparer.Ordinal);

        private readonly MarkdownSchemaManifest _schema = validator.Schema;
        private readonly HashSet<string> _required = new(StringComparer.Ordinal);

        /// <summary>The first node's children as bare type names (see <see cref="ValidateNode"/>).</summary>
        public IReadOnlyList<string>? ChildNames { get; set; }

        public void Require(IEnumerable<string> plugins) => _required.UnionWith(plugins);

        public void Envelope(JsonElement envelope)
        {
            var fields = Fields(envelope, "$", MarkdownDocumentViolationCodes.InvalidEnvelope, "must be a document object {format, version, plugins, document}");
            foreach (var name in fields.Keys.Where(name => !EnvelopeFields.Contains(name)))
                throw new ViolationException(name, MarkdownDocumentViolationCodes.InvalidEnvelope, "is not a document field; allowed are format, version, plugins and document");
            if (!fields.TryGetValue("format", out var format) || !TryString(format, out var text) || !MarkdownDocumentValidator.IsFormat(text))
                throw new ViolationException("format", MarkdownDocumentViolationCodes.InvalidEnvelope, $"must be \"{MarkdownDocumentValidator.Format}\"");
            if (!fields.TryGetValue("version", out var version) || !IsNumberEqual(version, 1))
                throw new ViolationException("version", MarkdownDocumentViolationCodes.InvalidEnvelope, "must be 1");
            if (fields.TryGetValue("plugins", out var plugins)) Plugins(plugins);
            if (!fields.TryGetValue("document", out var document))
                throw new ViolationException("document", MarkdownDocumentViolationCodes.InvalidEnvelope, "is missing");
            if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("type", out var type) || !TryString(type, out var top) || top != _schema.TopNode)
                throw new ViolationException("document", MarkdownDocumentViolationCodes.InvalidEnvelope, $"must be a node of type {_schema.TopNode}");
            Node(document, "document", 1);
        }

        private void Plugins(JsonElement plugins)
        {
            if (plugins.ValueKind != JsonValueKind.Array)
                throw new ViolationException("plugins", MarkdownDocumentViolationCodes.InvalidPlugins, "must be an array of plugin requirements [{id, version}]");
            var index = 0;
            foreach (var requirement in plugins.EnumerateArray())
            {
                var path = $"plugins[{index++}]";
                var fields = Fields(requirement, path, MarkdownDocumentViolationCodes.InvalidPlugins, "must be an object {id, version}");
                if (fields.Keys.FirstOrDefault(name => name is not ("id" or "version")) is { } extra)
                    throw new ViolationException($"{path}.{extra}", MarkdownDocumentViolationCodes.InvalidPlugins, "is not a requirement field; a requirement has only id and version");
                if (!fields.TryGetValue("id", out var id) || !TryString(id, out var name))
                    throw new ViolationException($"{path}.id", MarkdownDocumentViolationCodes.InvalidPlugins, "must be a plugin id");
                if (!fields.TryGetValue("version", out var version) || !IsSafeInteger(version, out var number) || number < 1)
                    throw new ViolationException($"{path}.version", MarkdownDocumentViolationCodes.InvalidPlugins, "must be a positive integer");
                if (!_required.Add(name))
                    throw new ViolationException($"{path}.id", MarkdownDocumentViolationCodes.InvalidPlugins, $"plugin {name} is required more than once");
                if (!_schema.Plugins.TryGetValue(name, out var installed))
                    throw new ViolationException($"{path}.id", MarkdownDocumentViolationCodes.UnknownPlugin,
                        $"plugin {name} is not installed; installed are {string.Join(", ", _schema.Plugins.Keys.Order(StringComparer.Ordinal))}");
                if (number != installed.Version && !installed.MigratesFrom.Contains((int)number))
                    throw new ViolationException($"{path}.version", MarkdownDocumentViolationCodes.UnsupportedPluginVersion,
                        $"plugin {name} has version {installed.Version} and does not migrate version {number}");
            }
        }

        /// <summary>One node and its subtree; returns its type and mark types for the parent's content and mark checks.</summary>
        public (string Type, string[] Marks) Node(JsonElement node, JsonPath path, int depth)
        {
            if (depth > MaxDepth) throw new ViolationException(path, MarkdownDocumentViolationCodes.TooDeep, $"is nested deeper than {MaxDepth} levels");
            // A child the guard validated on its own: its type, owner and marks are what the parent needs.
            if (shallow && node.ValueKind == JsonValueKind.Object && node.TryGetProperty(ShallowMarker, out _))
            {
                var name = node.GetProperty("type").GetString()!;
                if (!_schema.Nodes.TryGetValue(name, out var known))
                    throw new ViolationException($"{path}.type", MarkdownDocumentViolationCodes.UnknownNode, $"\"{name}\" is not a node type");
                RequireOwner(known.Owner, path, $"node {name}");
                return (name, node.TryGetProperty("marks", out var marked) ? Marks(marked, path.Field("marks")) : []);
            }
            var fields = Fields(node, path, MarkdownDocumentViolationCodes.InvalidNode, "must be a node object {type, attrs, content, marks}");
            if (!fields.TryGetValue("type", out var typeValue) || !TryString(typeValue, out var typeName))
                throw new ViolationException($"{path}.type", MarkdownDocumentViolationCodes.InvalidNode, "must be a node type name");
            if (!_schema.Nodes.TryGetValue(typeName, out var type))
                throw new ViolationException($"{path}.type", MarkdownDocumentViolationCodes.UnknownNode,
                    $"\"{typeName}\" is not a node type; known are {string.Join(", ", _schema.Nodes.Keys.Order(StringComparer.Ordinal))}");
            if (type.Owner is not null) RequireOwner(type.Owner, path, $"node {typeName}");
            var allowed = type.IsText ? TextFields : NodeFields;
            if (fields.Keys.FirstOrDefault(name => !allowed.Contains(name)) is { } extra)
                throw new ViolationException($"{path}.{extra}", MarkdownDocumentViolationCodes.UnknownField, $"is not a field of node {typeName}; allowed are {string.Join(", ", allowed)}");
            fields.TryGetValue("attrs", out var attrs);
            Attributes(attrs, type.Attributes, path.Field("attrs"), typeName);
            if (type.IsText && (!fields.TryGetValue("text", out var text) || !TryString(text, out var value) || value.Length == 0))
                throw new ViolationException($"{path}.text", MarkdownDocumentViolationCodes.InvalidText, "must be non-empty text");
            var marks = fields.TryGetValue("marks", out var markList) ? Marks(markList, path.Field("marks")) : [];
            List<string>? children = null;
            List<JsonElement>? rows = typeName == "table" ? [] : null;
            if (fields.TryGetValue("content", out var content))
            {
                if (content.ValueKind != JsonValueKind.Array)
                    throw new ViolationException($"{path}.content", MarkdownDocumentViolationCodes.InvalidContent, "must be an array of nodes");
                var index = 0;
                foreach (var child in content.EnumerateArray())
                {
                    // The guard writes a child it validates on its own as just its type name.
                    if (shallow && child.ValueKind == JsonValueKind.String)
                    {
                        var name = child.GetString()!;
                        if (!_schema.Nodes.TryGetValue(name, out var known))
                            throw new ViolationException($"{path}.content[{index}]", MarkdownDocumentViolationCodes.UnknownNode, $"\"{name}\" is not a node type");
                        RequireOwner(known.Owner, $"{path}.content[{index}]", $"node {name}");
                        (children ??= []).Add(name);
                        index++;
                        continue;
                    }
                    var childPath = path.Field("content").Item(index++);
                    var (childType, childMarks) = Node(child, childPath, depth + 1);
                    if (childMarks.FirstOrDefault(mark => !type.AllowedMarks.Contains(mark)) is { } refused)
                        throw new ViolationException($"{childPath}.marks", MarkdownDocumentViolationCodes.MarkNotAllowed, $"node {typeName} does not allow the mark {refused} in its content");
                    (children ??= []).Add(childType);
                    rows?.Add(child);
                }
            }
            else if (ChildNames is { } names)
            {
                ChildNames = null;
                for (var index = 0; index < names.Count; index++)
                {
                    if (!_schema.Nodes.TryGetValue(names[index], out var known))
                        throw new ViolationException($"{path}.content[{index}]", MarkdownDocumentViolationCodes.UnknownNode, $"\"{names[index]}\" is not a node type");
                    RequireOwner(known.Owner, $"{path}.content[{index}]", $"node {names[index]}");
                    (children ??= []).Add(names[index]);
                }
            }
            if (merged ? !(children ?? []).All(type.Members.Contains) : !type.Accepts(children ?? []))
            {
                var found = children is null ? "nothing" : string.Join(", ", children);
                throw new ViolationException($"{path}.content", MarkdownDocumentViolationCodes.InvalidContent, type.ContentExpression.Length == 0
                    ? $"node {typeName} cannot have content"
                    : $"the content of {typeName} must match \"{type.ContentExpression}\", found: {found}");
            }
            if (rows is not null) TableGeometry.Require(rows, path, _schema.MaxTableCells, _schema.MaxTableSpan, geometry: !merged);
            return (typeName, marks);
        }

        private string[] Marks(JsonElement marks, JsonPath path)
        {
            if (marks.ValueKind != JsonValueKind.Array) throw new ViolationException(path, MarkdownDocumentViolationCodes.InvalidMarks, "must be an array of marks");
            var result = new List<SchemaMark>();
            var index = 0;
            foreach (var mark in marks.EnumerateArray())
            {
                var markPath = path.Item(index++);
                var fields = Fields(mark, markPath, MarkdownDocumentViolationCodes.InvalidNode, "must be a mark object {type, attrs}");
                if (!fields.TryGetValue("type", out var typeValue) || !TryString(typeValue, out var typeName) || !_schema.Marks.TryGetValue(typeName, out var type))
                    throw new ViolationException($"{markPath}.type", MarkdownDocumentViolationCodes.UnknownMark, $"is not a mark; known are {string.Join(", ", _schema.MarkNames)}");
                if (fields.Keys.FirstOrDefault(name => !MarkFields.Contains(name)) is { } extra)
                    throw new ViolationException($"{markPath}.{extra}", MarkdownDocumentViolationCodes.UnknownField, "is not a mark field; allowed are type and attrs");
                if (type.Owner is not null) RequireOwner(type.Owner, markPath, $"mark {typeName}");
                fields.TryGetValue("attrs", out var attrs);
                Attributes(attrs, type.Attributes, markPath.Field("attrs"), typeName);
                // ProseMirror sorts a node's marks by rank on load (Mark.setFrom), but Node.check refuses a set holding a mark
                // twice or two marks that exclude each other (Mark.addToSet/sameSet).
                foreach (var earlier in merged ? [] : result)
                {
                    if (earlier.Excludes.Contains(typeName) || type.Excludes.Contains(earlier.Name))
                        throw new ViolationException(markPath, MarkdownDocumentViolationCodes.MarkConflict, earlier.Name == typeName
                            ? $"the mark {typeName} is repeated" : $"the mark {typeName} cannot be combined with {earlier.Name}");
                }
                result.Add(type);
            }
            return result.Select(x => x.Name).ToArray();
        }

        private void Attributes(JsonElement attrs, IReadOnlyDictionary<string, SchemaAttribute> declared, JsonPath path, string typeName)
        {
            HashSet<string>? present = null;
            if (attrs.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                var fields = Fields(attrs, path, MarkdownDocumentViolationCodes.InvalidAttribute, "must be an attributes object");
                foreach (var (name, value) in fields)
                {
                    if (!declared.TryGetValue(name, out var attribute))
                        throw new ViolationException($"{path}.{name}", MarkdownDocumentViolationCodes.UnknownAttribute, declared.Count == 0
                            ? $"is not an attribute of {typeName}; {typeName} has none"
                            : $"is not an attribute of {typeName}; allowed are {string.Join(", ", declared.Keys)}");
                    if (attribute.Owner is not null) RequireOwner(attribute.Owner, path.Field(name), $"attribute {name}");
                    // A merge can pair a colwidth with another peer's colspan: such a rule then checks the value alone.
                    var context = merged && attribute.Constraint.Context.Length > 0 ? default : attrs;
                    if (validator.CheckAttribute(attribute, value, context) is { } reason)
                        throw new ViolationException($"{path}.{name}", MarkdownDocumentViolationCodes.InvalidAttribute, reason);
                    (present ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
                }
            }
            // ProseMirror computes absent attributes from their defaults and validates those too; no default means required.
            foreach (var attribute in declared.Values)
            {
                if (present?.Contains(attribute.Name) == true || validator._validDefaults.Contains(attribute)) continue;
                if (attribute.Default is not { } fallback || validator.CheckAttribute(attribute, fallback, attrs) is not null)
                    throw new ViolationException(path.Field(attribute.Name), MarkdownDocumentViolationCodes.MissingAttribute, "is required");
            }
        }

        private void RequireOwner(string? owner, JsonPath path, string what)
        {
            if (owner is not null && !_required.Contains(owner))
                throw new ViolationException(path, MarkdownDocumentViolationCodes.MissingPlugin,
                    $"{what} belongs to plugin {owner}; the document must require it in plugins ({{\"id\":\"{owner}\",\"version\":{_schema.Plugins[owner].Version}}})");
        }

        /// <summary>The properties of an object; a repeated name is refused (JavaScript would silently keep the last one).</summary>
        private static Dictionary<string, JsonElement> Fields(JsonElement value, JsonPath path, string code, string expected)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new ViolationException(path, code, expected);
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!fields.TryAdd(property.Name, property.Value))
                    throw new ViolationException($"{path}.{property.Name}", MarkdownDocumentViolationCodes.DuplicateField, "is repeated");
            return fields;
        }
    }

    /// <summary>
    /// The table checks of engine/builtinDocumentValidation.ts: validateTableDimensions (at most 1000 cells, spans included) before
    /// prosemirror-tables' TableMap, whose problems (overlapping or missing cells, rowspans past the end, inconsistent column
    /// widths) make the editor refuse the document.
    /// </summary>
    private static class TableGeometry
    {
        private sealed record Cell(int Colspan, int Rowspan, double[]? Colwidth);

        /// <param name="geometry">Also require a consistent geometry, not only the size bounds.</param>
        public static void Require(List<JsonElement> rowNodes, JsonPath path, int maxCells, int maxSpan, bool geometry)
        {
            var rows = rowNodes.Select(row => row.TryGetProperty("content", out var cells) && cells.ValueKind == JsonValueKind.Array
                ? cells.EnumerateArray().Select(Read).ToArray() : []).ToArray();
            Dimensions(rows, path, maxCells, maxSpan, empty: !geometry);
            if (geometry && Problem(rows) is { } problem)
                throw new ViolationException(path, MarkdownDocumentViolationCodes.InvalidTable, $"has an invalid table geometry ({problem}): cells overlap, are missing or disagree on column widths");
        }

        private static Cell Read(JsonElement cell)
        {
            var attrs = cell.TryGetProperty("attrs", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
            int Span(string name) => attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(name, out var span) && IsNumber(span, out var number) ? (int)number : 1;
            double[]? widths = attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty("colwidth", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(x => IsNumber(x, out var width) ? width : 0).ToArray() : null;
            return new Cell(Span("colspan"), Span("rowspan"), widths);
        }

        /// <param name="empty">Admit a table without rows or cells, as concurrent row and column deletes leave one.</param>
        private static void Dimensions(Cell[][] rows, JsonPath path, int maxCells, int maxSpan, bool empty)
        {
            bool ValidSize(long height, long width) => (empty || (height > 0 && width > 0)) && height <= maxSpan && width <= maxSpan && height * width <= maxCells;
            var tooLarge = $"exceeds the table size (at most {maxCells} cells including merged ones and {maxSpan} rows or columns)";
            if (!ValidSize(rows.Length, 1)) throw new ViolationException(path, MarkdownDocumentViolationCodes.TableTooLarge, tooLarge);
            var widths = new Dictionary<int, long>();
            long widest = 0, height = rows.Length;
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                foreach (var cell in rows[rowIndex])
                {
                    height = System.Math.Max(height, rowIndex + cell.Rowspan);
                    if (!ValidSize(height, System.Math.Max(widest, cell.Colspan))) throw new ViolationException(path, MarkdownDocumentViolationCodes.TableTooLarge, tooLarge);
                    for (var row = rowIndex; row < rowIndex + cell.Rowspan; row++)
                    {
                        widths[row] = widths.GetValueOrDefault(row) + cell.Colspan;
                        widest = System.Math.Max(widest, widths[row]);
                        if (!ValidSize(height, widest)) throw new ViolationException(path, MarkdownDocumentViolationCodes.TableTooLarge, tooLarge);
                    }
                }
            }
            if (!ValidSize(height, widest)) throw new ViolationException(path, MarkdownDocumentViolationCodes.TableTooLarge, tooLarge);
        }

        /// <summary>prosemirror-tables computeMap/findBadColWidths, with cell ordinals in place of document positions.</summary>
        private static string? Problem(Cell[][] rows)
        {
            var height = rows.Length;
            var width = Width(rows);
            if (width == 0 || height == 0) return "zero_sized";
            var map = new int[width * height];
            var cells = new List<Cell> { null! };
            var colWidths = new double?[width];
            var counts = new int[width];
            string? problem = null;
            var mapPos = 0;
            for (var row = 0; row < height; row++)
            {
                for (var i = 0; ; i++)
                {
                    while (mapPos < map.Length && map[mapPos] != 0) mapPos++;
                    if (i == rows[row].Length) break;
                    var cell = rows[row][i];
                    var id = cells.Count;
                    cells.Add(cell);
                    for (var h = 0; h < cell.Rowspan; h++)
                    {
                        if (h + row >= height)
                        {
                            problem ??= "overlong_rowspan";
                            break;
                        }
                        var start = mapPos + h * width;
                        for (var w = 0; w < cell.Colspan; w++)
                        {
                            var at = start + w;
                            if (at < map.Length && map[at] == 0) map[at] = id;
                            else problem ??= "collision";
                            var colW = cell.Colwidth is { } list && w < list.Length ? list[w] : 0;
                            if (colW != 0)
                            {
                                var column = at % width;
                                var previous = colWidths[column];
                                if (previous is null || (previous != colW && counts[column] == 1))
                                {
                                    colWidths[column] = colW;
                                    counts[column] = 1;
                                }
                                else if (previous == colW)
                                {
                                    counts[column]++;
                                }
                            }
                        }
                    }
                    mapPos += cell.Colspan;
                }
                var expected = (row + 1) * width;
                var missing = 0;
                while (mapPos < expected) if (map[mapPos++] == 0) missing++;
                if (missing > 0) problem ??= "missing";
            }
            if (problem is not null) return problem;
            if (!Enumerable.Range(0, width).Any(column => colWidths[column] is not null && counts[column] < height)) return null;
            var seen = new HashSet<int>();
            for (var i = 0; i < map.Length; i++)
            {
                if (!seen.Add(map[i])) continue;
                var cell = cells[map[i]];
                for (var j = 0; j < cell.Colspan; j++)
                {
                    var expectedWidth = colWidths[(i + j) % width];
                    if (expectedWidth is not null && (cell.Colwidth is null || j >= cell.Colwidth.Length || cell.Colwidth[j] != expectedWidth))
                        return "colwidth mismatch";
                }
            }
            return null;
        }

        private static int Width(Cell[][] rows)
        {
            var width = -1;
            var hasRowSpan = false;
            for (var row = 0; row < rows.Length; row++)
            {
                var rowWidth = 0;
                if (hasRowSpan)
                {
                    for (var j = 0; j < row; j++)
                        rowWidth += rows[j].Where(cell => j + cell.Rowspan > row).Sum(cell => cell.Colspan);
                }
                foreach (var cell in rows[row])
                {
                    rowWidth += cell.Colspan;
                    hasRowSpan |= cell.Rowspan > 1;
                }
                width = width == -1 ? rowWidth : System.Math.Max(width, rowWidth);
            }
            return System.Math.Max(width, 0);
        }
    }
}
