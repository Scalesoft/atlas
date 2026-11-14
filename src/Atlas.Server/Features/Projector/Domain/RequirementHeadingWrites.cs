using System.Text.Json.Nodes;
using Atlas.Server.Features.Projector.Contracts;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Projector.Domain;

/// <summary>
/// The block commands on a requirement heading (shape 2026-11-04 §1): what <see cref="RequirementTree"/> does to a requirement block, done to
/// the heading's attributes (the party too, §6), its dependency line, its facts line and its notes. The text the server writes is the canonical shape
/// (<see cref="DependencyLine"/>, <see cref="FactsLine"/>, „Kde uloženo: …“), so a structured write never leaves irregular text. Positions
/// work on the page's top-level nodes: a requirement goes one level under the plain heading it is placed under and never into another.
/// </summary>
internal static class RequirementHeadingWrites
{
    private const int MaxAliases = 1000;

    /// <summary>Whether new requirements on this page are headings: it holds one already, or it holds no requirement block and the schema
    /// knows the heading's state (<see cref="RequirementHeadings.Writable"/>).</summary>
    public static bool UsesHeadings(JsonObject rich)
    {
        var document = rich["document"];
        return (document?["content"] as JsonArray ?? []).Any(RequirementHeadings.IsRequirement)
            || RequirementHeadings.Writable && document?.ToJsonString().Contains("\"type\":\"requirement\"", StringComparison.Ordinal) != true;
    }

    /// <summary>The requirement heading of <paramref name="id"/>: its headingId first, then a former id in its <c>aliases</c>.</summary>
    public static int? Find(JsonArray page, string id)
    {
        for (var i = 0; i < page.Count; i++)
        {
            if (RequirementHeadings.IsRequirement(page[i]) && RequirementHeadings.HeadingId(page[i]) == id)
            {
                return i;
            }
        }
        for (var i = 0; i < page.Count; i++)
        {
            if (RequirementHeadings.IsRequirement(page[i]) && Aliases(page[i]).Contains(id))
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>Where a requirement inserted at <paramref name="position"/> goes and its level: after a requirement, at its level; at the
    /// end of a plain heading's section, one level under it; at the end of the page, at the level of the last requirement heading there,
    /// else one under the last plain heading, else 2. Into a requirement is refused: requirements do not nest.</summary>
    public static (int Index, int Level) Target(JsonArray page, RequirementPosition position)
    {
        if (new[] { position.AfterBlockId, position.IntoBlockId, position.HeadingId }.Count(x => x is not null) > 1)
        {
            throw RequirementTree.InvalidPosition("Zadejte nejvýše jedno místo: za požadavek, nebo do oddílu.");
        }
        if (position.IntoBlockId is not null)
        {
            throw RequirementTree.InvalidPosition("Požadavky se do sebe nevnořují; vložte za požadavek (afterBlockId) nebo do oddílu nadpisu (headingId).");
        }
        if (position.AfterBlockId is { } after)
        {
            var heading = Find(page, after) ?? throw new ApiException(404, "Požadavek na stránce není.", "requirement_not_found");
            return (RequirementHeadings.RangeAt(page, heading).End, RequirementHeadings.Level(page[heading]));
        }
        if (position.HeadingId is { } headingId)
        {
            var at = Enumerable.Range(0, page.Count).Where(i => RequirementHeadings.IsHeading(page[i]) && RequirementHeadings.HeadingId(page[i]) == headingId)
                .Cast<int?>().FirstOrDefault() ?? throw new ApiException(404, "Nadpis na stránce není.", "heading_not_found");
            var level = RequirementHeadings.Level(page[at]);
            if (RequirementHeadings.IsRequirement(page[at]))
            {
                return (RequirementHeadings.RangeAt(page, at).End, level);
            }
            var end = at + 1;
            while (end < page.Count && !(RequirementHeadings.IsHeading(page[end]) && RequirementHeadings.Level(page[end]) <= level))
            {
                end++;
            }
            return (end, Math.Min(6, level + 1));
        }
        var last = page.LastOrDefault(RequirementHeadings.IsHeading);
        return (page.Count, last is null ? 2 : RequirementHeadings.IsRequirement(last) ? RequirementHeadings.Level(last) : Math.Min(6, RequirementHeadings.Level(last) + 1));
    }

    /// <summary>Takes the heading's section out of the page, in order.</summary>
    public static List<JsonNode> Detach(JsonArray page, int heading)
    {
        var end = RequirementHeadings.RangeAt(page, heading).End;
        var nodes = new List<JsonNode>();
        for (var i = end - 1; i >= heading; i--)
        {
            nodes.Insert(0, page[i]!);
            page.RemoveAt(i);
        }
        return nodes;
    }

    public static void Move(JsonArray page, int heading, RequirementPosition position)
    {
        var id = RequirementHeadings.HeadingId(page[heading]);
        if (position.AfterBlockId == id || position.HeadingId == id)
        {
            throw RequirementTree.InvalidPosition("Požadavek nejde přesunout do sebe sama.");
        }
        // The place is checked before anything moves; it is found again once the section is out.
        Target(page, position);
        var nodes = Detach(page, heading);
        var (index, level) = Target(page, position);
        nodes[0]["attrs"]!["level"] = level;
        Insert(page, index, nodes);
    }

    /// <summary>Copies (block-shaped, from <see cref="RequirementTree"/>'s copy) placed at <paramref name="position"/> as requirement headings;
    /// a plain heading among them (a whole module) goes as it is and the requirements after it one level under it.</summary>
    public static void Place(JsonObject rich, IReadOnlyList<JsonObject> copies, RequirementPosition position, Guid pageId)
    {
        var page = (JsonArray)rich["document"]!["content"]!;
        var (index, level) = Target(page, position);
        foreach (var copy in copies)
        {
            if (RequirementHeadings.IsHeading(copy))
            {
                page.Insert(index++, copy);
                level = Math.Min(6, RequirementHeadings.Level(copy) + 1);
                continue;
            }
            var nodes = Section(page, copy, level, pageId);
            Insert(page, index, nodes);
            index += nodes.Count;
        }
    }

    /// <summary>A block-shaped requirement (a catalog definition, a copy) as a heading's section at <paramref name="level"/>: its key as the
    /// headingId (the slug of its title, or a new id, when the key is none or taken), its attributes on the heading (its party too), the
    /// dependency line of its requires, the facts line of its date, „Kde uloženo“ of its where, then its notes. The copy's id becomes the headingId.</summary>
    // shortcut: a nested requirement block in the notes stays a block; convert it when a catalog with nested blocks is inserted into a heading page.
    public static List<JsonNode> Section(JsonArray page, JsonObject copy, int level, Guid pageId)
    {
        var attrs = (JsonObject)copy["attrs"]!;
        var taken = page.Where(RequirementHeadings.IsHeading).Select(RequirementHeadings.HeadingId).ToHashSet(StringComparer.Ordinal);
        var key = RequirementAttributes.Text(attrs, "key");
        var id = RequirementHeadings.IsValidId(key) && !taken.Contains(key) ? key : NewId(RequirementTree.Title(copy), taken);
        attrs["id"] = id;
        attrs["key"] = id;
        var content = copy["content"] as JsonArray ?? [];
        var heading = new JsonObject
        {
            ["type"] = "heading",
            ["attrs"] = new JsonObject
            {
                ["level"] = level, ["headingId"] = id, ["state"] = RequirementAttributes.Text(attrs, "state"), ["stateLabel"] = RequirementAttributes.Text(attrs, "stateLabel"),
                ["direction"] = RequirementAttributes.Text(attrs, "direction"), ["system"] = RequirementAttributes.Text(attrs, "system"),
                ["from"] = RequirementAttributes.Text(attrs, "from"),
            },
        };
        SetParty((JsonObject)heading["attrs"]!, RequirementAttributes.Text(attrs, "party"), RequirementAttributes.Text(attrs, "partyContactId"));
        if (content.Count > 0 && content[0]?["content"] is JsonArray { Count: > 0 } title)
        {
            heading["content"] = title.DeepClone();
        }
        var nodes = new List<JsonNode> { heading };
        if (DependencyLine(page, Resolve(page, RequirementAttributes.ParseRequires(RequirementAttributes.Text(attrs, "requires")), pageId, id, id), pageId, null) is { } line)
        {
            nodes.Add(line);
        }
        if (FactsLine(RequirementAttributes.Text(attrs, "date")) is { } facts)
        {
            nodes.Add(facts);
        }
        if (RequirementAttributes.Text(attrs, "where").Trim() is { Length: > 0 } where)
        {
            nodes.Add(Where(where));
        }
        nodes.AddRange(content.Skip(1).Select(x => x!.DeepClone()));
        return nodes;
    }

    /// <summary><see cref="RequirementTree.Update"/> of a requirement heading: the title is the heading's text, a new key its headingId (the
    /// former one joins its aliases, the page's links to it follow it), requires the dependency line, party and contact the heading's
    /// attributes, date the facts line, where the „Kde uloženo“ paragraph, notes everything else of the section.</summary>
    public static IReadOnlyList<string> Update(JsonObject rich, int at, RequirementChange change, DateOnly today, Guid pageId)
    {
        var document = (JsonObject)rich["document"]!;
        var page = (JsonArray)document["content"]!;
        var range = RequirementHeadings.RangeAt(page, at);
        var view = RequirementHeadings.View(page, range, pageId, RequirementHeadings.LocalIds(page));
        var attrs = (JsonObject)view["attrs"]!;
        var heading = (JsonObject)page[at]!;
        var oldId = RequirementHeadings.HeadingId(heading);
        var before = RequirementAttributes.Text(attrs, "date");
        var completedBefore = RequirementAttributes.Text(attrs, "completed");
        var others = page.Where((node, index) => index != at && RequirementHeadings.IsHeading(node)).Select(RequirementHeadings.HeadingId)
            .Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (change.Title is { } title)
        {
            // A requirement heading is its title: an empty one would leave a requirement nobody can see or name (as the editor's Enter).
            if (title.Trim().Length == 0)
            {
                throw RequirementAttributes.InvalidArgument("title", "title: požadavek je nadpis, jeho název nesmí být prázdný.");
            }
            heading["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = title });
        }
        var newId = oldId;
        if (change.Key is { } key)
        {
            // A cleared key takes the slug of the title (spec §2.1); without a title the id stays.
            newId = key.Trim().Length > 0 ? key.Trim()
                : RequirementAttributes.Unique(RequirementAttributes.Slug(HeadingText(heading)), others) is { Length: > 0 } slug ? slug : oldId;
        }
        attrs["key"] = newId;
        RequirementTree.Apply(attrs, change, today);
        if (change.Requires is not null)
        {
            attrs["requires"] = string.Join(",", Resolve(page, RequirementAttributes.ParseRequires(RequirementAttributes.Text(attrs, "requires")), pageId, oldId, newId)
                .Select(x => x.Page == pageId ? x.HeadingId : RequirementHeadings.Reference(x.Page, x.HeadingId)));
        }
        RequirementAttributes.Validate(attrs, others, heading: true);
        var headingAttrs = heading["attrs"] as JsonObject ?? (JsonObject)(heading["attrs"] = new JsonObject());
        foreach (var name in new[] { "state", "stateLabel", "direction", "system" })
        {
            headingAttrs[name] = RequirementAttributes.Text(attrs, name);
        }
        SetParty(headingAttrs, RequirementAttributes.Text(attrs, "party"), RequirementAttributes.Text(attrs, "partyContactId"));

        var nodes = Detach(page, at);
        var dependencies = range.Dependencies is { } d ? nodes[d - at] : null;
        var facts = range.Facts is { } f ? nodes[f - at] : null;
        var notes = nodes.Skip(1).Where(node => node != dependencies && node != facts).ToList();
        if (change.NotesMarkdown is { } markdown)
        {
            RequirementText.ReplaceNotes(rich, view, markdown);
            notes = Take(view["content"]!.AsArray()).Skip(1).ToList();
        }
        if (change.Where is { } where)
        {
            var found = notes.FindIndex(node => node is JsonObject paragraph && paragraph["type"]?.GetValue<string>() == "paragraph"
                && RequirementHeadings.Text(paragraph).StartsWith(RequirementHeadings.WhereLabel.TrimEnd(), StringComparison.Ordinal));
            if (found >= 0)
            {
                notes.RemoveAt(found);
            }
            if (where.Trim().Length > 0)
            {
                notes.Insert(Math.Max(found, 0), Where(where.Trim()));
            }
        }
        if (change.Requires is not null)
        {
            dependencies = DependencyLine(page, RequirementAttributes.ParseRequires(RequirementAttributes.Text(attrs, "requires"))
                .Select(x => RequirementHeadings.CrossPage(x) ?? (pageId, x)).ToArray(), pageId, dependencies);
        }
        if (RequirementAttributes.Text(attrs, "date") is var after && RequirementAttributes.Text(attrs, "completed") is var completed
            && (after != before || completed != completedBefore))
        {
            facts = FactsLine(after, completed);
        }
        Insert(page, at, [heading, .. new[] { dependencies, facts }.OfType<JsonNode>(), .. Notes(notes)]);

        IReadOnlyList<string> warnings = [];
        if (newId != oldId)
        {
            var dependents = RequirementHeadings.Ranges(page).Where(x => x.Dependencies is { } line
                    && RequirementHeadings.Links(page[line]).Contains((pageId, oldId)))
                .Select(x => $"„{HeadingText(page[x.Heading]!)}“").ToArray();
            headingAttrs["headingId"] = newId;
            var aliases = Aliases(heading).Append(oldId).Where(x => x.Length > 0 && x != newId).Distinct(StringComparer.Ordinal).ToList();
            while (string.Join(",", aliases).Length > MaxAliases)
            {
                aliases.RemoveAt(0);
            }
            headingAttrs["aliases"] = string.Join(",", aliases);
            Relink(document, pageId, oldId, newId);
            if (dependents.Length > 0)
            {
                warnings = [$"Závislosti požadavků {string.Join(", ", dependents)} teď vedou na nové ID „{newId}“."];
            }
        }
        RichMarkdownMerge.DeclarePlugins(rich, document);
        return warnings;
    }

    /// <summary>What the server writes: a requirement heading without a valid id, or with one an earlier heading has, gets one (the slug of
    /// its title, else a new one), so every requirement is addressable.</summary>
    public static void Normalize(JsonArray page)
    {
        var taken = page.Where(RequirementHeadings.IsHeading).Select(RequirementHeadings.HeadingId).Where(RequirementHeadings.IsValidId).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in page.Where(RequirementHeadings.IsHeading).Cast<JsonObject>())
        {
            var id = RequirementHeadings.HeadingId(heading);
            if ((!RequirementHeadings.IsValidId(id) || !seen.Add(id)) && RequirementHeadings.IsRequirement(heading))
            {
                id = NewId(HeadingText(heading), taken);
                heading["attrs"]!["headingId"] = id;
                taken.Add(id);
                seen.Add(id);
            }
        }
    }

    /// <summary>A copy of the page for a new deployment: each requirement heading planned, without its own state word, its aliases, its party,
    /// its facts line, its „Kde uloženo“ paragraph, the history lines of its notes and the reason a „Blokováno“ line names.</summary>
    public static void ResetForCopy(JsonArray page)
    {
        foreach (var range in RequirementHeadings.Ranges(page).Reverse())
        {
            var dependencies = range.Dependencies is { } d ? page[d] : null;
            var facts = range.Facts is { } f ? page[f] : null;
            var nodes = Detach(page, range.Heading);
            var heading = (JsonObject)nodes[0];
            heading["attrs"]!["state"] = "planned";
            heading["attrs"]!["stateLabel"] = "";
            heading["attrs"]!.AsObject().Remove("aliases");
            SetParty(heading["attrs"]!.AsObject(), "", "");
            var notes = new JsonObject { ["content"] = new JsonArray([RequirementTree.Paragraph(""), .. nodes.Skip(1).Where(x => x != dependencies && x != facts)]) };
            RequirementTree.StripRecords(notes);
            var kept = Take(notes["content"]!.AsArray()).Skip(1).Where(node => !(node["type"]?.GetValue<string>() == "paragraph"
                && RequirementHeadings.Text(node).StartsWith(RequirementHeadings.WhereLabel.TrimEnd(), StringComparison.Ordinal)));
            Insert(page, range.Heading, [heading, .. new[] { dependencies }.OfType<JsonNode>(), .. Notes(kept)]);
        }
    }

    /// <summary>The dependency line of <paramref name="targets"/>: section links separated by „, “, each named by its heading's text on this
    /// page, by the name its link had in <paramref name="old"/>, else by its id; null for none.</summary>
    public static JsonObject? DependencyLine(JsonArray page, IReadOnlyList<(Guid Page, string HeadingId)> targets, Guid pageId, JsonNode? old)
    {
        if (targets.Count == 0)
        {
            return null;
        }
        var named = (old?["content"] as JsonArray ?? []).Where(x => x?["type"]?.GetValue<string>() == "pageReference")
            .Select(x => (Target: RequirementHeadings.Links(new JsonObject { ["content"] = new JsonArray(x!.DeepClone()) }).FirstOrDefault(), Title: RequirementText.Value(x, "title")))
            .Where(x => x.Target.HeadingId is not null).GroupBy(x => x.Target).ToDictionary(x => x.Key, x => x.First().Title);
        var content = new JsonArray();
        foreach (var (target, index) in targets.Distinct().Select((x, i) => (x, i)))
        {
            if (index > 0)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = ", " });
            }
            var local = target.Page == pageId ? page.FirstOrDefault(x => RequirementHeadings.IsHeading(x) && RequirementHeadings.HeadingId(x) == target.HeadingId) : null;
            var title = local is not null && HeadingText(local) is { Length: > 0 } text ? text
                : named.GetValueOrDefault(target) is { Length: > 0 } name ? name : target.HeadingId;
            content.Add(new JsonObject
            {
                ["type"] = "pageReference",
                ["attrs"] = new JsonObject { ["id"] = target.Page.ToString("D"), ["title"] = title, ["url"] = $"#/pages/{target.Page:D}?heading={target.HeadingId}" },
            });
        }
        return new JsonObject { ["type"] = "paragraph", ["attrs"] = new JsonObject { ["dependencies"] = true }, ["content"] = content };
    }

    /// <summary>The facts line: „do d. M. yyyy“ of <paramref name="date"/>, „hotovo d. M. yyyy“ of <paramref name="completed"/> (3.5's day of
    /// checking off), joined by „ · “; null without either.</summary>
    public static JsonObject? FactsLine(string date, string completed = "")
    {
        var text = string.Join(RequirementHeadings.Separator, new[] { date.Length == 0 ? "" : RequirementHeadings.DeadlineText(date),
            completed.Length == 0 ? "" : RequirementHeadings.DoneText(completed) }.Where(x => x.Length > 0));
        return text.Length == 0 ? null : new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
    }

    /// <summary>The heading's party (§6): <c>party</c> and, for a contact, <c>partyContactId</c>; both gone without a party.</summary>
    private static void SetParty(JsonObject attrs, string party, string contactId)
    {
        attrs.Remove("party");
        attrs.Remove("partyContactId");
        if (party.Trim() is { Length: > 0 } name)
        {
            attrs["party"] = name;
            if (contactId.Trim().Length > 0)
            {
                attrs["partyContactId"] = contactId.Trim();
            }
        }
    }

    /// <summary>The targets of a requires list: <c>pageId#headingId</c>, a block of another page <c>key@pageId</c> as that page's heading of
    /// the key (a converted page keeps a valid key as its headingId), and on this page a headingId, a former id or key of a heading (its
    /// aliases), else the key as it is (a requirement the page lacks yet). <paramref name="oldId"/> stands for <paramref name="newId"/>.</summary>
    // shortcut: key@pageId is read as that page's headingId of the key; resolve it through the conversion map when a converted page renamed one.
    private static IReadOnlyList<(Guid Page, string HeadingId)> Resolve(JsonArray page, IEnumerable<string> requires, Guid pageId, string oldId, string newId)
        => requires.Select(reference =>
        {
            if (RequirementHeadings.CrossPage(reference) is { } heading)
            {
                return heading;
            }
            if (reference.Contains('@') && RequirementAttributes.CrossPage(reference) is { } block)
            {
                return (block.PageId, block.Key);
            }
            var local = Find(page, reference) is { } at ? RequirementHeadings.HeadingId(page[at]) : reference;
            return (pageId, local == oldId ? newId : local);
        }).Distinct().ToArray();

    /// <summary>Every link of the document to heading <paramref name="oldId"/> of this page leads to <paramref name="newId"/>.</summary>
    private static void Relink(JsonNode node, Guid pageId, string oldId, string newId)
    {
        if (node["type"]?.GetValue<string>() == "pageReference" && node["attrs"] is JsonObject attrs
            && RequirementHeadings.Links(new JsonObject { ["content"] = new JsonArray(node.DeepClone()) }) is [var target] && target == (pageId, oldId))
        {
            attrs["url"] = $"#/pages/{pageId:D}?heading={newId}";
        }
        foreach (var child in (node["content"] as JsonArray ?? []).OfType<JsonNode>())
        {
            Relink(child, pageId, oldId, newId);
        }
    }

    /// <summary>Notes as they are written back under the lines (fixed places, shape §1): a dependency flag on a note is cleared, as the editor
    /// does, so a note does not become the dependency line when the lines above it go.</summary>
    private static IEnumerable<JsonNode> Notes(IEnumerable<JsonNode> notes)
        => notes.Select(note =>
        {
            (note["attrs"] as JsonObject)?.Remove("dependencies");
            return note;
        });

    private static JsonObject Where(string where)
        => new() { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = RequirementHeadings.WhereLabel + where }) };

    /// <summary>The heading's text as a title paragraph, as the readers read a title.</summary>
    private static JsonObject Title(JsonNode heading)
        => heading["content"] is JsonArray { Count: > 0 } inline ? new JsonObject { ["type"] = "paragraph", ["content"] = inline.DeepClone() } : new JsonObject { ["type"] = "paragraph" };

    /// <summary>The heading's text as a requirement's title reads (RequirementText.Title).</summary>
    private static string HeadingText(JsonNode heading) => RequirementText.Title(new JsonObject { ["content"] = new JsonArray(Title(heading)) });

    private static string NewId(string title, IReadOnlySet<string> taken)
    {
        var slug = RequirementAttributes.Unique(RequirementAttributes.Slug(title), taken);
        return RequirementHeadings.IsValidId(slug) ? slug : "pozadavek-" + Guid.NewGuid().ToString("N")[..8];
    }

    private static IReadOnlyList<string> Aliases(JsonNode? heading)
        => RequirementAttributes.Text(heading?["attrs"], "aliases").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void Insert(JsonArray page, int index, IEnumerable<JsonNode> nodes)
    {
        foreach (var node in nodes)
        {
            page.Insert(index++, node);
        }
    }

    /// <summary>The nodes of <paramref name="array"/>, taken out of it so they can go elsewhere.</summary>
    private static List<JsonNode> Take(JsonArray array)
    {
        var nodes = array.OfType<JsonNode>().ToList();
        array.Clear();
        return nodes;
    }
}
