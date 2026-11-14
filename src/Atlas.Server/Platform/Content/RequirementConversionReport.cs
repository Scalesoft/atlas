using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Content;

/// <summary>What one requirement says, read from the old block or from the new heading's section, so the two can be compared field by
/// field (model §10b, definition of loss). <see cref="Id"/> is the heading id (the old block's assigned one), <see cref="BlockId"/> the
/// old block id; <see cref="Dependencies"/> the sorted targets <c>pageId#headingId</c>, an unresolved key as <c>?key</c>; <see cref="From"/>
/// the catalog definition (before: the one the space's catalog pages define for the key), <see cref="Aliases"/> the former ids.</summary>
public sealed record RequirementFacts(string Id, string BlockId, string Title, string State, string StateLabel, string Direction, string System,
    IReadOnlyList<string> Dependencies, string Party, string PartyContactId, string Date, string Where, int Notes, int HistoryLines,
    string From = "", string Aliases = "");

public enum RequirementCaseKind
{
    /// <summary>A block with nested blocks: it became a plain heading, its children requirements under it.</summary>
    Nested,
    /// <summary>Notes after a nested block moved before the first nested block.</summary>
    NotesMoved,
    /// <summary>A dependency key no block holds: the line „Chybějící prerekvizita: klíč“.</summary>
    UnresolvedKey,
    /// <summary>The heading id is the old id (or a new one), not the key: the key is empty, outside the charset, or not unique.</summary>
    IdFallback,
    /// <summary>The plain heading above is h6: the requirement is h6 as well.</summary>
    LevelCap,
    EmptyTitle,
    /// <summary>A key more than one block of the page holds.</summary>
    DuplicateKey,
    /// <summary>Content after a requirement block before the next heading: it is now in that requirement's section.</summary>
    LooseContent,
    /// <summary>A requirement block the conversion does not reach (inside another container): it stays, and is a loss.</summary>
    NotConverted,
    /// <summary>The stored JSON does not parse: left as it is.</summary>
    Unreadable,
    /// <summary>The move of a version-3 page (shape §6): a facts line that said more than its party and deadline stays under the deadline
    /// line as a note.</summary>
    FactsLineKept,
}

/// <summary>A case the report names. <see cref="Detail"/> holds no page text: counts, node types, the reason of a fallback.</summary>
public sealed record RequirementCase(RequirementCaseKind Kind, string BlockId, string Detail);

/// <summary>A field present before and not after (a loss) or after and not before (a gain); <see cref="Accepted"/> names the reported
/// case it comes from, null when it is unexplained (the gate).</summary>
public sealed record RequirementDifference(string RequirementId, string BlockId, string Field, string Before, string After, string? Accepted);

/// <summary>The numbers of a list of requirements, for one page or summed over all of them.</summary>
public sealed record RequirementCounts(int Requirements, IReadOnlyDictionary<string, int> States, IReadOnlyDictionary<string, int> Kinds,
    int Dependencies, int Unresolved, int Parties, int PartiesWithContact, int Deadlines, int Where, int Notes, int HistoryLines)
{
    public static RequirementCounts Of(IEnumerable<RequirementFacts> facts)
    {
        var list = facts.ToList();
        return new RequirementCounts(list.Count,
            new SortedDictionary<string, int>(list.GroupBy(x => x.State).ToDictionary(x => x.Key, x => x.Count()), StringComparer.Ordinal),
            new SortedDictionary<string, int>(list.GroupBy(x => x.Direction).ToDictionary(x => x.Key, x => x.Count()), StringComparer.Ordinal),
            list.Sum(x => x.Dependencies.Count), list.Sum(x => x.Dependencies.Count(d => d.StartsWith('?'))), list.Count(x => x.Party.Length > 0),
            list.Count(x => x.PartyContactId.Length > 0), list.Count(x => x.Date.Length > 0), list.Count(x => x.Where.Length > 0),
            list.Sum(x => x.Notes), list.Sum(x => x.HistoryLines));
    }
}

/// <summary>The dry-run report of one revision: the requirements before and after, the cases, and the losses and gains between them.</summary>
public sealed record RequirementConversionReport(Guid PageId, IReadOnlyList<RequirementFacts> Before, IReadOnlyList<RequirementFacts> After,
    IReadOnlyList<RequirementCase> Cases, IReadOnlyList<RequirementDifference> Losses, IReadOnlyList<RequirementDifference> Gains)
{
    public RequirementCounts BeforeCounts => RequirementCounts.Of(Before);

    public RequirementCounts AfterCounts => RequirementCounts.Of(After);

    /// <summary>The gate: every loss and gain comes from a reported case.</summary>
    public bool Passes => Losses.Concat(Gains).All(x => x.Accepted is not null);

    public static RequirementConversionReport Compare(Guid pageId, IReadOnlyList<RequirementFacts> before, IReadOnlyList<RequirementFacts> after,
        IReadOnlyList<RequirementCase> cases)
    {
        var losses = new List<RequirementDifference>();
        var gains = new List<RequirementDifference>();
        bool Has(string block, RequirementCaseKind kind) => cases.Any(x => x.BlockId == block && x.Kind == kind);
        var afterById = after.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        foreach (var old in before)
        {
            if (!afterById.TryGetValue(old.Id, out var now))
            {
                losses.Add(new(old.Id, old.BlockId, "requirement", old.Title, "", Has(old.BlockId, RequirementCaseKind.Nested) ? "nested" : null));
                continue;
            }
            foreach (var (field, was, @is) in new[]
            {
                ("title", old.Title, now.Title), ("state", old.State, now.State), ("stateLabel", old.StateLabel, now.StateLabel),
                ("kind", old.Direction, now.Direction), ("system", old.System, now.System), ("party", old.Party, now.Party),
                ("partyContactId", old.PartyContactId, now.PartyContactId), ("deadline", old.Date, now.Date), ("where", old.Where, now.Where),
                ("from", old.From, now.From), ("aliases", old.Aliases, now.Aliases),
            })
            {
                if (was != @is)
                {
                    // A party the move could not carry (over the attribute's limit) stays in its line, kept as a note.
                    (was.Length == 0 ? gains : losses).Add(new(old.Id, old.BlockId, field, was, @is,
                        field is "party" or "partyContactId" && @is.Length == 0 && Has(old.Id, RequirementCaseKind.FactsLineKept) ? "facts line kept" : null));
                }
            }
            foreach (var missing in old.Dependencies.Except(now.Dependencies, StringComparer.Ordinal))
            {
                losses.Add(new(old.Id, old.BlockId, "dependency", missing, "", null));
            }
            foreach (var extra in now.Dependencies.Except(old.Dependencies, StringComparer.Ordinal))
            {
                gains.Add(new(old.Id, old.BlockId, "dependency", "", extra, null));
            }
            // An unresolved key reads the same after (its line), but it is a lost dependency until the owner accepts it (model §10b).
            foreach (var unresolved in old.Dependencies.Where(x => x.StartsWith('?')))
            {
                losses.Add(new(old.Id, old.BlockId, "dependency", unresolved, unresolved, "unresolved key"));
            }
            var loose = Has(old.BlockId, RequirementCaseKind.LooseContent) ? "loose content" : Has(old.Id, RequirementCaseKind.FactsLineKept) ? "facts line kept" : null;
            foreach (var (field, was, @is) in new[] { ("notes", old.Notes, now.Notes), ("history", old.HistoryLines, now.HistoryLines) })
            {
                if (was != @is)
                {
                    (@is < was ? losses : gains).Add(new(old.Id, old.BlockId, field, was.ToString(CultureInfo.InvariantCulture),
                        @is.ToString(CultureInfo.InvariantCulture), @is > was ? loose : null));
                }
            }
        }
        var beforeIds = before.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        gains.AddRange(after.Where(x => !beforeIds.Contains(x.Id)).Select(x => new RequirementDifference(x.Id, x.BlockId, "requirement", "", x.Title, null)));
        return new RequirementConversionReport(pageId, before, after, cases, losses, gains);
    }
}

/// <summary>
/// Reads the requirements of a document in the new shape (plan 2026-11-04 §1), independently of the converter: a heading with a
/// <c>state</c> is a requirement, its section the following top-level siblings up to the next heading; the dependency line, the facts
/// line, „Chybějící prerekvizita: …“ and „Kde uloženo: …“ are its fields, every other block a note or a list of history lines. The party
/// is the heading's <c>party</c> / <c>partyContactId</c> and the facts line the deadline alone (§6, version 4), or with <c>legacy</c> (a
/// version-3 page) the party and deadline of a facts line „od|pro … · do …“.
/// An old <c>requirement</c> block ends a section too, so a document holding both shapes reads only the new one here.
/// </summary>
public static partial class RequirementHeadingReader
{
    public const string Missing = "Chybějící prerekvizita: ";
    public const string Stored = "Kde uloženo: ";
    public const string Former = "Dříve požadavek: ";

    public static bool IsRequirementHeading(JsonNode? node)
        => RichDocument.TypeOf(node) == "heading" && node!["attrs"]?["state"] is JsonValue state && state.TryGetValue<string>(out _);

    public static IReadOnlyList<RequirementFacts> Read(Guid pageId, JsonNode document, bool legacy = false)
    {
        var facts = new List<RequirementFacts>();
        JsonNode? heading = null;
        var section = new List<JsonNode>();
        foreach (var node in (document["content"] as JsonArray ?? []).OfType<JsonNode>().Append(null))
        {
            if (node is null || RichDocument.TypeOf(node) is "heading" or "requirement")
            {
                if (heading is not null)
                {
                    facts.Add(Section(pageId, heading, section, legacy));
                }
                (heading, section) = (IsRequirementHeading(node) ? node : null, []);
                continue;
            }
            section.Add(node);
        }
        return facts;
    }

    private static RequirementFacts Section(Guid pageId, JsonNode heading, List<JsonNode> section, bool legacy)
    {
        var dependencies = new List<string>();
        if (section is [var first, ..] && RichDocument.TypeOf(first) == "paragraph" && first["attrs"]?["dependencies"] is JsonValue flag
            && flag.TryGetValue<bool>(out var isLine) && isLine)
        {
            foreach (var atom in (first["content"] as JsonArray ?? []).Where(x => RichDocument.TypeOf(x) == "pageReference"))
            {
                var url = RequirementText.Value(atom, "url");
                var at = url.IndexOf("?heading=", StringComparison.Ordinal);
                dependencies.Add(RequirementText.Value(atom, "id") + "#" + (at < 0 ? "" : Uri.UnescapeDataString(url[(at + 9)..])));
            }
            section = section[1..];
        }
        // Fixed places (shape §1): the facts line is the first node after the dependency line, else a note.
        var (party, contact, date, where) = legacy ? ("", "", "", "") : (RequirementText.Value(heading, "party"), RequirementText.Value(heading, "partyContactId"), "", "");
        var slot = section.FirstOrDefault();
        var body = new List<JsonNode>();
        foreach (var node in section)
        {
            var text = RichDocument.TypeOf(node) == "paragraph" ? Text(node) : null;
            if (text is not null && ReferenceEquals(node, slot) && !legacy && Deadline().Match(text) is { Index: 0, Success: true } line && line.Length == text.Length)
            {
                date = Date(line);
            }
            else if (text is not null && ReferenceEquals(node, slot) && legacy
                && (text.StartsWith("od ", StringComparison.Ordinal) || text.StartsWith("pro ", StringComparison.Ordinal) || Deadline().Match(text) is { Index: 0, Success: true }))
            {
                if (!text.StartsWith("do ", StringComparison.Ordinal))
                {
                    var start = text.IndexOf(' ', StringComparison.Ordinal) + 1;
                    var end = text.IndexOf(" · ", start, StringComparison.Ordinal);
                    party = (end < 0 ? text[start..] : text[start..end]).Trim(' ', ' ');
                }
                contact = (node["content"] as JsonArray ?? []).Where(x => RichDocument.TypeOf(x) == "contactInline")
                    .Select(x => x!["attrs"]?["data"]?["contacts"]?[0]?["objectId"]).OfType<JsonValue>().Select(x => x.GetValue<string>()).FirstOrDefault() ?? "";
                date = Deadline().Match(text) is { Success: true } day ? Date(day) : "";
            }
            else if (text is not null && text.StartsWith(Missing, StringComparison.Ordinal))
            {
                dependencies.Add("?" + text[Missing.Length..]);
            }
            else if (text is not null && where.Length == 0 && text.StartsWith(Stored, StringComparison.Ordinal))
            {
                where = text[Stored.Length..];
            }
            else
            {
                body.Add(node);
            }
        }
        var (notes, history) = Body(body);
        dependencies.Sort(StringComparer.Ordinal);
        var aliases = RequirementText.Value(heading, "aliases").Split(',');
        return new RequirementFacts(RequirementText.Value(heading, "headingId"), aliases[0], Title(heading), RequirementText.Value(heading, "state"),
            RequirementText.Value(heading, "stateLabel"), RequirementText.Value(heading, "direction"), RequirementText.Value(heading, "system"),
            dependencies, party, contact, date, where, notes, history, RequirementText.Value(heading, "from"), RequirementText.Value(heading, "aliases"));
    }

    private static string Date(Match day)
        => $"{day.Groups[3].Value}-{int.Parse(day.Groups[2].Value, CultureInfo.InvariantCulture):00}-{int.Parse(day.Groups[1].Value, CultureInfo.InvariantCulture):00}";

    /// <summary>The heading's text as <see cref="RequirementText.Title"/> reads a block's title paragraph.</summary>
    public static string Title(JsonNode heading) => RequirementText.Title(new JsonObject { ["content"] = new JsonArray(heading.DeepClone()) });

    /// <summary>Notes (top-level blocks that are not lists) and history lines (list, task and timed items, nested ones too).</summary>
    public static (int Notes, int History) Body(IEnumerable<JsonNode?> nodes)
    {
        var (notes, history) = (0, 0);
        foreach (var node in nodes)
        {
            var items = Items(node);
            (notes, history) = items > 0 ? (notes, history + items) : (notes + 1, history);
        }
        return (notes, history);
    }

    private static int Items(JsonNode? node) => (RichDocument.TypeOf(node) is "listItem" or "taskItem" or "timedItem" ? 1 : 0)
        + (node?["content"] as JsonArray ?? []).Sum(Items);

    /// <summary>The plain text of a node: its text, a contact by its name, a line break as a space.</summary>
    public static string Text(JsonNode? node) => RichDocument.TypeOf(node) switch
    {
        "text" => node!["text"]?.GetValue<string>() ?? "",
        "hardBreak" => " ",
        "contactInline" => node!["attrs"]?["data"]?["contacts"]?[0]?["name"]?.GetValue<string>() ?? "",
        _ => string.Concat((node?["content"] as JsonArray ?? []).Select(Text)),
    };

    /// <summary>The deadline of a facts line („od X · do 15. 11. 2026“, „do 15. 11. 2026“) in the day grammar of history-stamps.json.</summary>
    [GeneratedRegex(@"(?:^|(?<= · ))do (\d{1,2})\. (\d{1,2})\. (\d{4})")]
    private static partial Regex Deadline();
}
