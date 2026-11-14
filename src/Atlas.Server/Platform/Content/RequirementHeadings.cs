using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// The one reading of a requirement heading for every C# reader; the SQL
/// function atlas_projector_requirements (migrations 2026110820, 2026110840) mirrors these rules and tests/fixtures/content/requirement-headings.json keeps
/// the two equal. A top-level heading whose <c>state</c> is a string is a requirement; its section runs to the next heading of any level.
/// Fixed places: the dependency line is the node directly under the heading when it is a paragraph with <c>dependencies: true</c> (its
/// <c>pageReference</c> atoms with <c>?heading=</c> are the dependencies); the facts line is the next node (after the dependency line, if
/// any) when it is a paragraph whose whole text, trimmed of spaces and no-break spaces, is „do d. M. yyyy“ (the deadline, a day in the
/// calendar), „hotovo d. M. yyyy“ (the day it was checked off) or both joined by „ · “. The party is the heading's attributes <c>party</c> and <c>partyContactId</c> (§6, schema version 4). Every other node is a
/// note, a <c>dependencies</c> paragraph and a version-3 „od … · do …“ line (<see cref="LegacyFactsOf"/>, moved by <see cref="MoveParty"/>) too.
/// </summary>
public static partial class RequirementHeadings
{
    public const string Separator = " · ";
    public const string WhereLabel = "Kde uloženo: ";

    /// <summary>The <c>atlas-requirements</c> version whose party is the heading's attributes (§6); a page declaring less is moved.</summary>
    public const int PartyVersion = 4;

    /// <summary>The longest party the heading's attribute holds (the schema's limit).</summary>
    public const int MaxParty = 200;

    /// <summary>What a facts segment is trimmed of: spaces and no-break spaces.</summary>
    private static readonly char[] Blank = [' ', '\u00A0'];

    /// <summary>A requirement's section in a top-level content array: its heading, the index after its last node, and the places of
    /// its dependency line and its facts line, when it has them.</summary>
    public sealed record Range(int Heading, int End, int? Dependencies, int? Facts);

    /// <summary>A version-3 facts line's party (its text), the party's contact ("" for plain text) and the deadline as yyyy-MM-dd ("" for none).</summary>
    public sealed record Facts(string Party, string ContactId, string Date);

    /// <summary>Whether the server may write the new shape: the editor schema it validates with knows the heading's <c>state</c>. Until
    /// the schema has it (workstream W2a), a new requirement on a page without requirement headings stays a block.</summary>
    public static bool Writable { get; } = Validation.HostDocumentValidator.Instance.Schema.AttributesOf("heading").Contains("state");

    public static bool IsHeading(JsonNode? node) => Type(node) == "heading";

    public static bool IsRequirement(JsonNode? node) => IsHeading(node) && node!["attrs"]?["state"] is JsonValue state && state.TryGetValue<string>(out _);

    public static bool IsValidId(string id) => Id().IsMatch(id);

    public static string Reference(Guid page, string headingId) => $"{page:D}#{headingId}";

    /// <summary><c>pageId#headingId</c> as (pageId, headingId), else null.</summary>
    public static (Guid Page, string HeadingId)? CrossPage(string reference)
    {
        var at = reference.IndexOf('#');
        return at == 36 && Guid.TryParseExact(reference[..at], "D", out var page) && IsValidId(reference[(at + 1)..]) ? (page, reference[(at + 1)..]) : null;
    }

    public static int Level(JsonNode? heading) => heading?["attrs"]?["level"] is JsonValue value && value.TryGetValue<int>(out var level) ? level : 1;

    public static string HeadingId(JsonNode? heading) => RequirementText.Attr(heading?["attrs"], "headingId");

    /// <summary>Every requirement heading's section of <paramref name="content"/> (a document's top-level nodes), in order.</summary>
    public static IReadOnlyList<Range> Ranges(JsonArray content)
    {
        var ranges = new List<Range>();
        for (var i = 0; i < content.Count; i++)
        {
            if (IsRequirement(content[i]))
            {
                ranges.Add(RangeAt(content, i));
            }
        }
        return ranges;
    }

    public static Range RangeAt(JsonArray content, int heading)
    {
        var end = heading + 1;
        while (end < content.Count && !IsHeading(content[end]))
        {
            end++;
        }
        int? dependencies = heading + 1 < end && Type(content[heading + 1]) == "paragraph"
            && content[heading + 1]!["attrs"]?["dependencies"] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on ? heading + 1 : null;
        var next = (dependencies ?? heading) + 1;
        int? facts = next < end && Type(content[next]) == "paragraph" && DeadlineOf(content[next]) is not null ? next : null;
        return new Range(heading, end, dependencies, facts);
    }

    /// <summary>The text the facts rule reads (marks do not matter): text, a line break as a space, a contact as its name, a YouTrack issue
    /// as its key, a page link as its title; other inline nodes are nothing.</summary>
    public static string Text(JsonNode? paragraph) => string.Concat((paragraph?["content"] as JsonArray ?? []).Select(x => Type(x) switch
    {
        "text" => RequirementText.Attr(x, "text"),
        "hardBreak" => " ",
        "contactInline" => ContactName(x),
        "youtrackIssue" => RequirementText.Value(x, "key").Trim(' '),
        "pageReference" => RequirementText.Value(x, "title"),
        _ => "",
    }));

    /// <summary>The deadline (yyyy-MM-dd, "" for none) of a paragraph that is a facts line, else null.</summary>
    public static string? DeadlineOf(JsonNode? paragraph) => FactsOf(paragraph)?.Deadline;

    /// <summary>The facts line's deadline and completion day (yyyy-MM-dd, "" for none): a paragraph whose whole text is „do d. M. yyyy“,
    /// „hotovo d. M. yyyy“ (3.5's day of checking off) or both joined by „ · “; any other paragraph is no facts line (null).</summary>
    public static (string Deadline, string Completed)? FactsOf(JsonNode? paragraph)
    {
        var segments = Text(paragraph).Split(Separator);
        var deadlines = segments.Select(Deadline).Where(x => x is not null).ToArray();
        var done = segments.Select(Done).Where(x => x is not null).ToArray();
        return deadlines.Length > 1 || done.Length > 1 || deadlines.Length + done.Length != segments.Length ? null : (deadlines.FirstOrDefault() ?? "", done.FirstOrDefault() ?? "");
    }

    /// <summary>The heading's party, trimmed of spaces and no-break spaces.</summary>
    public static string PartyOf(JsonNode? heading) => RequirementText.Attr(heading?["attrs"], "party").Trim(Blank);

    /// <summary>The heading's party contact: its <c>partyContactId</c> when it has a party, else "".</summary>
    public static string PartyContactOf(JsonNode? heading) => PartyOf(heading).Length > 0 ? RequirementText.Attr(heading?["attrs"], "partyContactId") : "";

    /// <summary>The facts of a paragraph that is a version-3 facts line („od … · do …“, schema version 3), else null.</summary>
    public static Facts? LegacyFactsOf(JsonNode? paragraph)
    {
        var segments = Text(paragraph).Split(Separator);
        if (Party(segments[0]) is null && Deadline(segments[0]) is null)
        {
            return null;
        }
        var at = Array.FindIndex(segments, x => Party(x) is not null);
        var party = at < 0 ? null : Party(segments[at]);
        var date = segments.Select(Deadline).FirstOrDefault(x => x is not null) ?? "";
        // The contact only when the party segment is that chip („od [Jan]“), never a chip elsewhere in the line („· kontakt [Petr]“).
        var (contact, text) = ("", "");
        foreach (var node in paragraph?["content"] as JsonArray ?? [])
        {
            if (Type(node) == "contactInline" && contact.Length == 0 && text.Split(Separator).Length - 1 == at && ContactName(node).Trim(Blank) == party)
            {
                contact = ContactId(node);
            }
            text += Text(new JsonObject { ["content"] = new JsonArray(node!.DeepClone()) });
        }
        return new Facts(party ?? "", contact, date);
    }

    /// <summary>The party of one segment: the rest after „od “ or „pro “, trimmed of spaces and no-break spaces; null for any other segment.</summary>
    public static string? Party(string segment)
    {
        var text = segment.Trim(Blank);
        return text.StartsWith("od ", StringComparison.Ordinal) ? text[3..].Trim(Blank)
            : text.StartsWith("pro ", StringComparison.Ordinal) ? text[4..].Trim(Blank) : null;
    }

    /// <summary>„do 15. 11. 2026“ as 2026-11-15 when the day is in the calendar; null for any other segment.</summary>
    public static string? Deadline(string segment) => Day(segment, "do");

    /// <summary>„hotovo 5. 11. 2026“ as 2026-11-05 when the day is in the calendar; null for any other segment.</summary>
    public static string? Done(string segment) => Day(segment, "hotovo");

    private static string? Day(string segment, string word)
    {
        var text = segment.Trim(Blank);
        var match = DayPattern().Match(text.StartsWith(word, StringComparison.Ordinal) ? "do" + text[word.Length..] : "");
        return match.Success && int.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var year) && year >= 1
            && int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var month) && month is >= 1 and <= 12
            && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var day) && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    /// <summary>„do 15. 11. 2026“ of a yyyy-MM-dd date.</summary>
    public static string DeadlineText(string date) => DayText("do", date);

    /// <summary>„hotovo d. M. yyyy“ of a yyyy-MM-dd date.</summary>
    public static string DoneText(string date) => DayText("hotovo", date);

    private static string DayText(string word, string date)
    {
        var day = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{word} {day.Day}. {day.Month}. {day.Year}");
    }

    /// <summary>The dependency line's targets in order, each once: its <c>pageReference</c> atoms whose url names a valid <c>?heading=</c>
    /// and whose id is a page id.</summary>
    public static IReadOnlyList<(Guid Page, string HeadingId)> Links(JsonNode? paragraph)
        => (paragraph?["content"] as JsonArray ?? []).Where(x => Type(x) == "pageReference")
            .Select(x => (Page: Guid.TryParseExact(RequirementText.Value(x, "id"), "D", out var page) ? page : Guid.Empty,
                Heading: HeadingParameter().Match(RequirementText.Value(x, "url")) is { Success: true } m ? m.Groups[1].Value : ""))
            .Where(x => x.Page != Guid.Empty && IsValidId(x.Heading)).Distinct().ToArray();

    /// <summary>The dependencies as the index keeps them: the headingId for one on the same page, <c>pageId#headingId</c> for another. Without
    /// <paramref name="pageId"/> (a reader that does not know the page) a target is on the same page when the page has a requirement heading
    /// of that id (<paramref name="local"/>).</summary>
    public static IReadOnlyList<string> Requires(JsonNode? paragraph, Guid? pageId, IReadOnlySet<string> local)
        => Links(paragraph).Select(x => (pageId is { } page ? x.Page == page : local.Contains(x.HeadingId)) ? x.HeadingId : Reference(x.Page, x.HeadingId))
            .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The ids of the requirement headings of <paramref name="content"/>.</summary>
    public static IReadOnlySet<string> LocalIds(JsonArray content)
        => content.Where(IsRequirement).Select(HeadingId).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A requirement heading's section read as the old requirement node (§4 of the shape), so every reader of blocks reads both shapes: attrs
    /// <c>id</c> and <c>key</c> = headingId, state, stateLabel, direction, system, from, the facts line's date, party and partyContactId, the
    /// dependency line's requires, <c>where</c> empty; content = the heading's text as the title paragraph, then copies of the notes (the
    /// section without its dependency and facts lines). A copy: writing to it changes nothing.
    /// </summary>
    // shortcut: copies the notes on every read; read the range in place when pages with thousands of notes make the reads slow.
    public static JsonObject View(JsonArray content, Range range, Guid? pageId, IReadOnlySet<string> local)
    {
        var heading = content[range.Heading]!;
        var attrs = heading["attrs"];
        var (date, completed) = range.Facts is { } at ? FactsOf(content[at]) ?? ("", "") : ("", "");
        var view = new JsonObject
        {
            ["type"] = "requirement",
            ["attrs"] = new JsonObject
            {
                ["id"] = HeadingId(heading), ["key"] = HeadingId(heading), ["direction"] = RequirementText.Attr(attrs, "direction"),
                ["state"] = RequirementText.Attr(attrs, "state"), ["stateLabel"] = RequirementText.Attr(attrs, "stateLabel"),
                ["system"] = RequirementText.Attr(attrs, "system"), ["from"] = RequirementText.Attr(attrs, "from"), ["date"] = date, ["completed"] = completed,
                ["party"] = PartyOf(heading), ["partyContactId"] = PartyContactOf(heading), ["where"] = "",
                ["requires"] = string.Join(",", range.Dependencies is { } line ? Requires(content[line], pageId, local) : []),
                ["level"] = Level(heading),
            },
        };
        var title = new JsonObject { ["type"] = "paragraph" };
        if (heading["content"] is JsonArray { Count: > 0 } inline)
        {
            title["content"] = inline.DeepClone();
        }
        var notes = new JsonArray(title);
        for (var i = range.Heading + 1; i < range.End; i++)
        {
            if (i != range.Dependencies && i != range.Facts)
            {
                notes.Add(content[i]!.DeepClone());
            }
        }
        view["content"] = notes;
        return view;
    }

    /// <summary>The document's top-level nodes with each requirement heading's section in the place of its heading as its <see cref="View"/>:
    /// what a reader of blocks walks instead of the top-level content. Other nodes are the document's own.</summary>
    public static IReadOnlyList<JsonNode> Project(JsonNode? document, Guid? pageId = null)
    {
        if (document?["content"] is not JsonArray content)
        {
            return [];
        }
        var local = LocalIds(content);
        var nodes = new List<JsonNode>(content.Count);
        for (var i = 0; i < content.Count; i++)
        {
            if (IsRequirement(content[i]))
            {
                var range = RangeAt(content, i);
                nodes.Add(View(content, range, pageId, local));
                i = range.End - 1;
            }
            else if (content[i] is { } node)
            {
                nodes.Add(node);
            }
        }
        return nodes;
    }

    /// <summary>The id a page written before it exists links its own requirement headings through (the web's deployment from the catalog,
    /// catalogSelection.ts NEW_PAGE_ID); <see cref="Rehome"/> turns it into the real id when the page is created.</summary>
    public static readonly Guid NewPage = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    /// <summary>A page's content copied into a new page (a deployment copy, a page from a template): its links to headings of
    /// <paramref name="source"/> that the copy holds lead to the same headings of <paramref name="copy"/> (shape §4: the links between the
    /// copied headings are rewritten, links to anything else stay). The text as it was when there is none.</summary>
    public static string Rehome(string contentJson, Guid source, Guid copy)
    {
        if (!contentJson.Contains(source.ToString("D"), StringComparison.OrdinalIgnoreCase) || !contentJson.Contains("?heading=", StringComparison.Ordinal))
        {
            return contentJson;
        }
        var rich = JsonNode.Parse(contentJson, documentOptions: Atlas.Markdown.Sync.MarkdownDocumentValidator.JsonOptions)!;
        var held = (rich["document"]?["content"] as JsonArray ?? []).Where(IsHeading).Select(HeadingId).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        void Walk(JsonNode? node)
        {
            if (Type(node) == "pageReference" && Links(new JsonObject { ["content"] = new JsonArray(node!.DeepClone()) }) is [var target]
                && target.Page == source && held.Contains(target.HeadingId))
            {
                node["attrs"]!["id"] = copy.ToString("D");
                node["attrs"]!["url"] = $"#/pages/{copy:D}?heading={target.HeadingId}";
                changed = true;
            }
            foreach (var child in node?["content"] as JsonArray ?? [])
            {
                Walk(child);
            }
        }
        Walk(rich["document"]);
        return changed ? rich.ToJsonString(RichDocument.JsonWrite) : contentJson;
    }

    /// <summary>
    /// The move of a version-3 page (§6): each requirement's „od|pro [kontakt] · do d. M. yyyy“ line at the facts line's place becomes the
    /// heading's <c>party</c> / <c>partyContactId</c> and the deadline line „do d. M. yyyy“ (none without a deadline). A line that holds
    /// anything else (another segment, a second contact, a link, an issue, a line break) stays under the deadline line as a note, so nothing
    /// is lost. A line that is a deadline line already stays, and so does a heading that has a party already (moved). Whether anything moved.
    /// </summary>
    /// <param name="kept">Collects the headings whose line stayed as a note.</param>
    public static bool MoveParty(JsonArray content, List<JsonNode>? kept = null)
    {
        var moved = false;
        for (var i = content.Count - 1; i >= 0; i--)
        {
            if (!IsRequirement(content[i]) || PartyOf(content[i]).Length > 0)
            {
                continue;
            }
            var place = i + 1 < content.Count && Type(content[i + 1]) == "paragraph" && content[i + 1]!["attrs"]?["dependencies"] is JsonValue flag
                && flag.TryGetValue<bool>(out var on) && on ? i + 2 : i + 1;
            if (place >= content.Count || Type(content[place]) != "paragraph" || DeadlineOf(content[place]) is not null
                || LegacyFactsOf(content[place]) is not { } facts)
            {
                continue;
            }
            var line = content[place]!;
            // A party over the attribute's limit (shape §6) stays in its line, kept as a note below the deadline.
            var fits = facts.Party.Length <= MaxParty;
            if (facts.Party.Length > 0 && fits)
            {
                var attrs = content[i]!["attrs"] as JsonObject ?? (JsonObject)(content[i]!["attrs"] = new JsonObject());
                attrs["party"] = facts.Party;
                if (facts.ContactId.Length > 0)
                {
                    attrs["partyContactId"] = facts.ContactId;
                }
            }
            content.RemoveAt(place);
            if (!fits || !Clean(line))
            {
                content.Insert(place, line);
                kept?.Add(content[i]!);
            }
            if (facts.Date.Length > 0)
            {
                content.Insert(place, new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = DeadlineText(facts.Date) }) });
            }
            moved = true;
        }
        return moved;
    }

    /// <summary>Whether a version-3 facts line says only what the move keeps: text and at most one contact, each segment one party or one
    /// deadline, at most one of each.</summary>
    private static bool Clean(JsonNode line)
    {
        var nodes = line["content"] as JsonArray ?? [];
        var segments = Text(line).Split(Separator);
        return nodes.All(x => Type(x) is "text" or "contactInline") && nodes.Count(x => Type(x) == "contactInline") <= 1
            && segments.All(x => Party(x) is not null || Deadline(x) is not null)
            && segments.Count(x => Party(x) is not null) <= 1 && segments.Count(x => Deadline(x) is not null) <= 1;
    }

    /// <summary>Whether a block node is a <see cref="View"/> (a heading's section, not a stored node).</summary>
    public static bool IsView(JsonNode? block) => block?["attrs"]?["level"] is JsonValue;

    private static string ContactName(JsonNode? node) => node?["attrs"]?["data"]?["contacts"]?[0]?["name"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : "";

    private static string ContactId(JsonNode? node)
        => node?["attrs"]?["data"]?["contacts"]?[0]?["objectId"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : "";

    private static string Type(JsonNode? node) => node is JsonObject ? RequirementText.Attr(node, "type") : "";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Id();

    // The day grammar of tests/fixtures/content/history-stamps.json with a space or a no-break space between its parts.
    [GeneratedRegex("^do[  ]+([0-9]{1,2})\\.[  ]*([0-9]{1,2})\\.[  ]*([0-9]{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex DayPattern();

    [GeneratedRegex("[?&]heading=([^&#]+)", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingParameter();
}
