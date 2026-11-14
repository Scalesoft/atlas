using Atlas.Server.Features.ContentImport.Contracts;
using Atlas.Server.Features.PageTags.Domain;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Atlas.Server.Features.ContentImport.Domain;

/// <summary>
/// The metadata header of a page. <paramref name="HasTitle"/> tells whether the title came from the
/// header rather than the fallback; <paramref name="LineOffset"/> counts the source lines before
/// <paramref name="Body"/>.
/// </summary>
public sealed record WikiPageHeader(string Title, string Body, IReadOnlyList<WikiImportFinding> Findings, IReadOnlyList<string> Tags,
    IReadOnlyList<string> Aliases, int LineOffset, bool HasTitle);

/// <summary>
/// Wiki.js writes the same key/value header into every dumped page: as Markdown front matter for
/// Markdown pages and as the leading HTML comment for pages written in its visual editor; Obsidian and
/// other Markdown wikis use the same front matter with YAML lists. Both forms are read here so a
/// page's title, tags and aliases behave the same whichever editor authored it.
/// </summary>
public static class WikiPageMetadata
{
    private const int MaximumTags = 20;

    public static WikiPageHeader Read(string text, string fallbackTitle)
    {
        var (open, close) = text.StartsWith("---\n", StringComparison.Ordinal) ? ("---\n", "\n---\n")
            : text.StartsWith("<!--\n", StringComparison.Ordinal) ? ("<!--\n", "\n-->") : ("", "");
        if (open.Length == 0)
        {
            return new WikiPageHeader(fallbackTitle, text, [], [], [], 0, false);
        }
        var end = text.IndexOf(close, open.Length, StringComparison.Ordinal);
        if (end < 0 || end > 32768)
        {
            throw new ApiException(400, "Neplatná nebo příliš velká metadata stránky.");
        }
        var metadata = text[open.Length..end];
        var findings = new ImportFindings();
        string? title = null;
        var tags = new List<string>();
        var aliases = new List<string>();
        // Wiki.js writes unquoted scalar lines; reject aliases and fall back to simple lines for invalid YAML.
        YamlMappingNode? mapping = null;
        try
        {
            var parser = new Parser(new StringReader(metadata));
            var events = 0;
            while (parser.MoveNext())
            {
                if (++events > 256 || parser.Current is AnchorAlias || parser.Current is NodeEvent node && !node.Anchor.IsEmpty)
                {
                    throw new ApiException(400, "Metadata nesmí používat YAML odkazy nebo složitou strukturu.");
                }
            }
            var stream = new YamlStream();
            stream.Load(new StringReader(metadata));
            mapping = stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;
        }
        catch (YamlException)
        {
            findings.Add("front-matter-invalid", WikiImportFinding.Degraded, 1, open.Trim());
        }
        if (mapping is not null)
        {
            foreach (var (key, value) in mapping.Children)
            {
                switch ((key as YamlScalarNode)?.Value?.ToLowerInvariant())
                {
                    case "title":
                        title = (value as YamlScalarNode)?.Value;
                        break;
                    case "tags" or "tag" or "keywords":
                        tags.AddRange(Values(value));
                        break;
                    case "aliases" or "alias":
                        aliases.AddRange(Values(value));
                        break;
                }
            }
        }
        else
        {
            var lines = metadata.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                {
                    title = line[6..].Trim().Trim('\"', '\'');
                }
                else if (line.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
                {
                    var inline = line[5..].Trim();
                    if (inline.Length > 0)
                    {
                        tags.AddRange(Split(inline));
                    }
                    while (index + 1 < lines.Length && lines[index + 1].TrimStart().StartsWith("- ", StringComparison.Ordinal))
                    {
                        tags.Add(lines[++index].TrimStart()[2..].Trim());
                    }
                }
            }
        }
        var valid = new List<string>();
        foreach (var tag in tags)
        {
            try
            {
                valid.Add(TagNames.Normalize(tag.Trim().TrimStart('#'), 48, false));
            }
            catch (ApiException)
            {
                findings.Add("tag-invalid", WikiImportFinding.Lost, null, tag);
            }
        }
        // The same names, order and limit a person setting the tags gets.
        var prepared = TagNames.Prepare(valid);
        foreach (var (name, _) in prepared.Skip(MaximumTags))
        {
            findings.Add("tag-invalid", WikiImportFinding.Lost, null, name);
        }
        var normalized = prepared.Take(MaximumTags).Select(x => x.Name).ToArray();
        var body = text[(end + close.Length)..].TrimStart('\n');
        var offset = text[..(text.Length - body.Length)].Count(c => c == '\n');
        var hasTitle = !string.IsNullOrWhiteSpace(title);
        return new WikiPageHeader(hasTitle ? title!.Trim() : fallbackTitle, body, findings.ToArray(), normalized,
            aliases.Select(x => x.Trim()).Where(x => x.Length is > 0 and <= 200).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray(), offset, hasTitle);
    }

    private static IEnumerable<string> Values(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode scalar when scalar.Value is { Length: > 0 } value => Split(value),
            YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? "").Where(x => x.Length > 0),
            _ => [],
        };
    }

    // Wiki.js separates tags with commas; a YAML flow list written as a plain line is accepted too.
    private static IEnumerable<string> Split(string value)
    {
        return value.Trim().TrimStart('[').TrimEnd(']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim('\"', '\''));
    }
}
