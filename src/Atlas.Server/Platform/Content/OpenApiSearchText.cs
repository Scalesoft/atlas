using System.Text;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// The searchable text of the OpenAPI components of a rich document, for the AI index. The page's Markdown carries only the
/// one-line summary (<see cref="EditorComponentText.OpenApi"/>), so operations, parameters and schemas would never be found.
/// Written as Markdown sections, one per operation and schema, after the page's own text: the chunker gives each its own passage
/// whose context line names the operation ("{title} › GET /path"). Full-text search indexes the raw source in SQL
/// (<c>SearchSqlText.ComponentText</c>).
/// </summary>
public static class OpenApiSearchText
{
    /// <summary>Upper bound of the text of one page; a larger specification is indexed up to it.</summary>
    public const int MaxCharacters = 300_000;

    private static readonly HashSet<string> HttpMethods = new(StringComparer.Ordinal)
    {
        "get", "put", "post", "delete", "options", "head", "patch", "trace",
    };

    /// <summary>The text of every OpenAPI component in <paramref name="contentJson"/>; empty when there is none.</summary>
    public static string Of(string? contentJson)
    {
        if (string.IsNullOrEmpty(contentJson) || !contentJson.Contains("\"openapi\"", StringComparison.Ordinal))
        {
            return "";
        }
        var text = new StringBuilder();
        foreach (var source in Sources(JsonNode.Parse(contentJson)))
        {
            Append(text, source);
            if (text.Length >= MaxCharacters)
            {
                return text.ToString(0, MaxCharacters);
            }
        }
        return text.ToString();
    }

    private static IEnumerable<string> Sources(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonValue type && type.TryGetValue<string>(out var name) && name == "openapi"
                    && obj["attrs"]?["data"]?["source"] is JsonValue source && source.TryGetValue<string>(out var text))
                {
                    yield return text;
                    yield break;
                }
                foreach (var (_, child) in obj)
                {
                    foreach (var found in Sources(child))
                    {
                        yield return found;
                    }
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var found in Sources(child))
                    {
                        yield return found;
                    }
                }
                break;
        }
    }

    private static void Append(StringBuilder text, string source)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            using (var reader = new StringReader(source))
            {
                stream.Load(reader);
            }
            if (stream.Documents.FirstOrDefault()?.RootNode is not YamlMappingNode map)
            {
                return;
            }
            root = map;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // The editor never loads a source it cannot parse; there is nothing structured to index.
            return;
        }

        var info = Child(root, "info") as YamlMappingNode;
        text.Append("\n\n## API ").Append(Line(Scalar(info, "title")));
        if (Scalar(info, "version") is { Length: > 0 } version)
        {
            text.Append(" v").Append(Line(version));
        }
        text.Append('\n');
        Paragraph(text, Scalar(info, "description"));

        if (Child(root, "paths") is YamlMappingNode paths)
        {
            foreach (var (pathKey, item) in paths.Children)
            {
                if (item is not YamlMappingNode operations)
                {
                    continue;
                }
                var shared = Child(operations, "parameters");
                foreach (var (methodKey, value) in operations.Children)
                {
                    var method = (methodKey as YamlScalarNode)?.Value?.ToLowerInvariant() ?? "";
                    if (HttpMethods.Contains(method) && value is YamlMappingNode operation)
                    {
                        Operation(text, method.ToUpperInvariant() + " " + Line(Name(pathKey)), operation, shared);
                    }
                }
            }
        }

        var components = Child(root, "components") as YamlMappingNode;
        if (components is not null && Child(components, "schemas") is YamlMappingNode schemas)
        {
            foreach (var (nameKey, value) in schemas.Children)
            {
                text.Append("\n### Schéma ").Append(Line(Name(nameKey))).Append('\n');
                if (value is YamlMappingNode schema)
                {
                    Paragraph(text, Scalar(schema, "title"));
                    Paragraph(text, Scalar(schema, "description"));
                    Properties(text, schema);
                }
            }
        }
    }

    private static void Operation(StringBuilder text, string heading, YamlMappingNode operation, YamlNode? shared)
    {
        text.Append("\n### ").Append(heading).Append('\n');
        Paragraph(text, Scalar(operation, "summary"));
        var facts = new List<string>();
        if (Scalar(operation, "operationId") is { Length: > 0 } id)
        {
            facts.Add("operationId: " + id);
        }
        if (Child(operation, "tags") is YamlSequenceNode tags)
        {
            facts.Add("tagy: " + string.Join(", ", tags.Children.OfType<YamlScalarNode>().Select(x => x.Value)));
        }
        Paragraph(text, string.Join(" · ", facts));
        Paragraph(text, Scalar(operation, "description"));
        var parameters = new[] { shared, Child(operation, "parameters") }.OfType<YamlSequenceNode>().SelectMany(x => x.Children)
            .OfType<YamlMappingNode>().ToArray();
        foreach (var parameter in parameters)
        {
            var reference = Scalar(parameter, "$ref");
            var line = reference.Length > 0
                ? "- parametr " + reference[(reference.LastIndexOf('/') + 1)..]
                : "- parametr " + Scalar(parameter, "name") + " (" + Scalar(parameter, "in") + ")" + Suffix(Scalar(parameter, "description"));
            text.Append(Line(line)).Append('\n');
        }
        if (Child(operation, "requestBody") is YamlMappingNode body)
        {
            Paragraph(text, "Tělo požadavku: " + Scalar(body, "description") + " " + References(body));
        }
        if (Child(operation, "responses") is YamlMappingNode responses)
        {
            foreach (var (codeKey, value) in responses.Children)
            {
                var response = value as YamlMappingNode;
                text.Append(Line("- odpověď " + Name(codeKey) + Suffix(Scalar(response, "description")) + " " + References(response))).Append('\n');
            }
        }
    }

    private static void Properties(StringBuilder text, YamlMappingNode schema)
    {
        if (Child(schema, "properties") is not YamlMappingNode properties)
        {
            return;
        }
        foreach (var (nameKey, value) in properties.Children)
        {
            var property = value as YamlMappingNode;
            var type = Scalar(property, "type") is { Length: > 0 } t ? " (" + t + ")" : References(property) is { Length: > 0 } r ? " (" + r + ")" : "";
            text.Append(Line("- " + Name(nameKey) + type + Suffix(Scalar(property, "description")))).Append('\n');
        }
    }

    /// <summary>The schema names a node refers to anywhere below it (<c>$ref: '#/components/schemas/X'</c> gives X).</summary>
    private static string References(YamlNode? node)
    {
        var names = new List<string>();
        Collect(node, names, 0);
        return string.Join(", ", names.Distinct(StringComparer.Ordinal));
    }

    private static void Collect(YamlNode? node, List<string> names, int depth)
    {
        // Aliases may repeat a subtree; a bound keeps a hostile document from running long.
        if (depth > 12 || names.Count > 20)
        {
            return;
        }
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var (key, value) in map.Children)
                {
                    if (Name(key) == "$ref" && value is YamlScalarNode reference && reference.Value is { } target)
                    {
                        names.Add(target[(target.LastIndexOf('/') + 1)..]);
                    }
                    else
                    {
                        Collect(value, names, depth + 1);
                    }
                }
                break;
            case YamlSequenceNode sequence:
                foreach (var item in sequence.Children)
                {
                    Collect(item, names, depth + 1);
                }
                break;
        }
    }

    private static void Paragraph(StringBuilder text, string value)
    {
        if (value.Trim() is { Length: > 0 } trimmed)
        {
            text.Append(trimmed).Append("\n\n");
        }
    }

    private static string Suffix(string description) => description.Trim() is { Length: > 0 } text ? " — " + Line(text) : "";

    /// <summary>One line: a heading or list item must not break into Markdown structure.</summary>
    private static string Line(string value) => string.Join(' ', value.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Name(YamlNode node) => (node as YamlScalarNode)?.Value ?? "";

    private static YamlNode? Child(YamlMappingNode? map, string key)
        => map is not null && map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string Scalar(YamlMappingNode? map, string key) => (Child(map, key) as YamlScalarNode)?.Value ?? "";
}
