using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Collaboration.Domain;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// Writes Markdown edited by a client (MCP, plan 2026-10-05 §11 K2, D8) back into the revision it was projected from
/// (<see cref="RichMarkdownProjection"/>), on the principle of the editor's page source session: the new blocks are aligned
/// (LCS) with the projected blocks they replace, an unchanged block keeps its stored JSON (attributes, heading id, table
/// alignment) and every placeholder <c>⟦c:N⟧</c> brings back the very node it stands for. Unchanged means structurally equal as
/// Markdown sees it (<see cref="RichDocument.Comparable"/>, key order aside), the very test by which the projection admitted the
/// block as text; an edited block keeps what Markdown does not carry where it is clear where it goes (<see cref="KeepGapPresentation"/>,
/// <see cref="KeepLinks"/>), and an edit that would still drop stored presentation is 409 <c>formatting_lost</c> unless the caller
/// resets formatting. Pure: no database, no clock; the
/// caller (C2) reads the revision and projection in one transaction and saves the result with the expected version in
/// another, so a concurrent change is a version conflict there.
/// <para>
/// Refusals are <see cref="ApiException"/>s: 400 <c>components_changed</c> (placeholders not exactly those of the replaced
/// range: missing, duplicated, reordered, foreign, or a block placeholder moved into text; <see cref="ReplaceAll"/> accepts
/// reordered ones, and missing ones it is told to remove, else 409 <c>components_removed</c>), 400 <c>secret_directive_forbidden</c> (a password directive anywhere in the input or in the text
/// it parses to), 400 <c>invalid_markdown</c> (a section that does not start with its heading at its level, a bad new heading),
/// 400 <c>invalid_argument</c> (a temporary heading id of another version), 404 <c>section_not_found</c> and 413 <c>invalid_markdown</c>
/// (input or result over the document limits). Messages never
/// echo stored content.
/// </para>
/// <para>
/// What Markdown could smuggle in is neutralized with a warning: the parser keeps raw HTML as text, an image other than an
/// attachment of the page as a link and a link outside <c>http</c>, <c>https</c>, <c>mailto</c>, Atlas pages and attachment
/// downloads as text; a link to an Atlas page the actor may not read (or that does not exist) becomes text unless the revision
/// already held it; <c>[[Název]]</c> becomes a page reference only for exactly one readable page; formulas, Mermaid diagrams, contacts and charts are
/// read as the editor reads them (<see cref="MarkdownToRich.PagePlugins"/>), so an unchanged one aligns with its stored node. A new node never comes from
/// Markdown otherwise. An attachment image whose Markdown did not change (address, alternative text, title) gets its stored
/// node back, size and presentation included, wherever it stands; one with a new alternative text or title keeps the size and
/// presentation of a stored image of the same address; a new one is the editor's default node, and a stored look it replaced
/// is a <c>formatting_lost</c>. Whether
/// the attachment belongs to the page, is active and is an image is the save's check (<c>AttachmentReferences</c>).
/// </para>
/// </summary>
public static partial class RichMarkdownMerge
{
    private const int MaxHeadingText = 500;

    private static readonly string DefaultLinkLook = LinkLook((JsonObject)RichNodes.LinkMark("https://example.com")["attrs"]!);

    // Names a refusal or warning lists before "a dalších N".
    private const int ListedNames = 20;

    // Above this many cells of the alignment table the middle of a range is taken as rewritten (memory bound).
    private const long MaxAlignmentCells = 4_000_000;

    private static readonly Dictionary<string, string> PluginOfNode = new(StringComparer.Ordinal)
    {
        ["heading"] = "heading-anchors",
        ["pageReference"] = "pages",
        ["contactInline"] = "contacts",
        ["contactCard"] = "contacts",
        ["chart"] = "charts",
        ["attachment"] = "attachments",
        ["openapi"] = "openapi",
        ["youtrackIssue"] = "youtrack",
        ["emoji"] = "emoji",
        ["secretReference"] = "secrets",
        ["secretInlineReference"] = "secrets",
        ["concealedContent"] = "concealed",
        ["diagram"] = "diagrams",
        ["mathBlock"] = "math",
        ["mathInline"] = "math",
        ["tableOfContents"] = "toc",
        ["requirement"] = "atlas-requirements",
        ["overview"] = "atlas-requirements",
        ["requirementSummary"] = "atlas-requirements",
    };

    private static readonly Dictionary<string, string> BlockNames = new(StringComparer.Ordinal)
    {
        ["paragraph"] = "odstavec", ["heading"] = "nadpis", ["table"] = "tabulka", ["bulletList"] = "seznam", ["orderedList"] = "číslovaný seznam",
        ["taskList"] = "seznam úkolů", ["timedList"] = "časová odrážka", ["blockquote"] = "citace", ["callout"] = "informační box", ["image"] = "obrázek",
    };

    private static readonly Dictionary<string, string> PresentationNames = new(StringComparer.Ordinal)
    {
        ["textAlign"] = "zarovnání", ["colwidth"] = "šířka sloupců", ["backgroundColor"] = "barva buněk", ["height"] = "výška řádků", ["type"] = "styl číslování",
        ["image"] = "vzhled obrázku", ["link"] = "cíl a třída odkazů", ["aliases"] = "dřívější ID nadpisu",
    };

    [GeneratedRegex(":secret(?![A-Za-z0-9_])", RegexOptions.IgnoreCase)]
    private static partial Regex SecretDirective();

    [GeneratedRegex("^#/pages/(?<id>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})")]
    private static partial Regex PageHref();

    /// <summary>
    /// Replaces the section of <paramref name="headingId"/> (its heading and everything up to the next heading of the same
    /// or a higher level) with <paramref name="markdown"/>, which starts with the section's heading at the same level (its
    /// text may change, its id stays) and holds exactly the section's placeholders in their order.
    /// </summary>
    public static MergeResult ReplaceSection(RichDocument current, ContentProjection projection, string headingId, string markdown, MergeContext context,
        bool resetFormatting = false)
    {
        RequireSize(markdown);
        var (start, end, section) = SectionRange(current, projection, headingId);
        var parsed = Parse(markdown, context);
        var first = projection.Blocks.First(x => x.DocumentIndex == start);
        var opening = parsed.Blocks.Count == 0 ? null : parsed.Blocks[0];
        var opens = first.Prepared is null
            ? opening is not null && BlockSlot(opening) == first.SlotIndex
            : RichDocument.TypeOf(opening) == "heading" && Level(opening!) == section.Level;
        if (!opens)
        {
            throw new ApiException(400, "Sekce musí začínat svým nadpisem úrovně {level} ({marker} …).", "invalid_markdown",
                args: new Dictionary<string, object?> { ["level"] = section.Level, ["marker"] = new string('#', section.Level) });
        }
        return Merge(current, projection, start, end, parsed, context, resetFormatting: resetFormatting);
    }

    /// <summary>
    /// Inserts a new section (heading <paramref name="heading"/> at <paramref name="level"/> and the body
    /// <paramref name="markdown"/>) at the start or end of the page or after the whole section of
    /// <paramref name="afterHeadingId"/> (<see cref="SectionPosition.AfterSection"/>). A new section has no placeholders.
    /// </summary>
    public static MergeResult InsertSection(RichDocument current, ContentProjection projection, string? afterHeadingId, SectionPosition position,
        int level, string heading, string markdown, MergeContext context)
    {
        if ((position == SectionPosition.AfterSection) != (afterHeadingId is not null))
        {
            throw new ArgumentException("A section is inserted after a heading exactly when its position says so.", nameof(afterHeadingId));
        }
        var title = heading.Trim();
        if (level is < 1 or > 6 || title.Length == 0 || title.Length > MaxHeadingText || title.Contains('\n') || title.Contains('\r'))
        {
            throw new ApiException(400, "Nová sekce potřebuje nadpis na jednom řádku (nejvýše 500 znaků) a úroveň 1–6.", "invalid_markdown");
        }
        var text = new string('#', level) + " " + title + "\n\n" + markdown;
        RequireSize(text);
        var parsed = Parse(text, context);
        if (RichDocument.TypeOf(parsed.Blocks.FirstOrDefault()) != "heading")
        {
            throw new ApiException(400, "Nadpis nové sekce není platný nadpis Markdownu.", "invalid_markdown");
        }
        var index = position switch
        {
            SectionPosition.Start => 0,
            SectionPosition.End => current.Blocks.Count,
            _ => SectionRange(current, projection, afterHeadingId!).End,
        };
        if (position != SectionPosition.Start)
        {
            // Spacing paragraphs at the end of the page or section stay after the new section.
            while (index > 0 && IsEmptyParagraph(current.Blocks[index - 1]))
            {
                index--;
            }
        }
        return Merge(current, projection, index, index, parsed, context);
    }

    /// <summary>
    /// Replaces the whole page. Each placeholder brings its node back, also at another place. A placeholder left out removes its
    /// component only when <paramref name="removeComponents"/> lists it (named in a warning); otherwise 409
    /// <c>components_removed</c> and nothing is written. A listed number that is no placeholder of the page, or whose placeholder
    /// the Markdown still holds, is 400 <c>invalid_argument</c>. A foreign or repeated placeholder, or a block placeholder moved
    /// into text, is still <c>components_changed</c>.
    /// </summary>
    public static MergeResult ReplaceAll(RichDocument current, ContentProjection projection, string markdown, MergeContext context,
        IReadOnlyCollection<int>? removeComponents = null, bool resetFormatting = false)
    {
        RequireSize(markdown);
        return Merge(current, projection, 0, current.Blocks.Count, Parse(markdown, context), context, removeComponents ?? [], resetFormatting);
    }

    /// <summary>
    /// Appends <paramref name="markdown"/> after the last block of the page (spacing paragraphs at the end stay after it): new
    /// text only, without placeholders, every heading a new id. MCP's draft from a template: the template's copy with the text
    /// after it. The rest of the page keeps its stored JSON.
    /// </summary>
    public static MergeResult Append(RichDocument current, ContentProjection projection, string markdown, MergeContext context)
    {
        RequireSize(markdown);
        var parsed = Parse(markdown, context);
        var index = current.Blocks.Count;
        while (index > 0 && IsEmptyParagraph(current.Blocks[index - 1]))
        {
            index--;
        }
        return Merge(current, projection, index, index, parsed, context);
    }

    /// <summary>A new page from Markdown: no placeholders, every heading a new id.</summary>
    public static MergeResult CreateDocument(string markdown, MergeContext context)
    {
        RequireSize(markdown);
        var parsed = Parse(markdown, context);
        var merge = new Conversion(context, new Dictionary<int, SlotBinding>(), new HashSet<string>(StringComparer.Ordinal));
        merge.RequireSlots(parsed.Blocks, [], "Nová stránka nesmí obsahovat značky komponent ⟦c:N⟧.");
        var blocks = parsed.Blocks.Select(merge.Convert).ToList();
        var envelope = new JsonObject { ["format"] = Atlas.Markdown.Sync.MarkdownDocumentValidator.Format, ["version"] = 1, ["plugins"] = new JsonArray() };
        return Finish(envelope, blocks, parsed.Warnings.Concat(merge.Warnings), context);
    }

    // ---- the merge ----

    /// <param name="removeComponents">Not null for the whole page: the placeholders it may leave out (<see cref="ReplaceAll"/>).</param>
    /// <param name="resetFormatting">Edited blocks may lose presentation that has nowhere unambiguous to go (else 409 <c>formatting_lost</c>).</param>
    private static MergeResult Merge(RichDocument current, ContentProjection projection, int start, int end, MarkdownToRichResult parsed, MergeContext context,
        IReadOnlyCollection<int>? removeComponents = null, bool resetFormatting = false)
    {
        var wholePage = removeComponents is not null;
        var bindings = projection.Bindings.ToDictionary(x => x.Index);
        var merge = new Conversion(context, bindings, LinkTargets(current.Document));
        var originals = projection.Blocks.Where(x => x.DocumentIndex >= start && x.DocumentIndex < end).ToList();
        var expected = originals.SelectMany(x => x.Prepared is null ? [x.SlotIndex] : x.InlineSlots).ToList();
        var removed = merge.RequireSlots(parsed.Blocks, expected, wholePage
                ? "Značky komponent ⟦c:N⟧ musí být ze stránky, každá nejvýše jednou a blok samostatně na řádku."
                : start == end ? "Nový text nesmí obsahovat značky komponent ⟦c:N⟧." : "Značky komponent ⟦c:N⟧ musí zůstat beze změny, v původním pořadí a každá jednou.",
            wholePage);
        if (wholePage)
        {
            RequireRemovals(projection, removed, removeComponents!);
        }
        if (removed.Count > 0)
        {
            merge.Warn("Odebrané komponenty: " + Described(projection, removed) + ".");
        }

        var sections = SectionsByBlock(current, projection);
        var losses = new SortedDictionary<int, SortedSet<string>>();
        var blocks = new List<JsonObject>();
        blocks.AddRange(current.Blocks.Take(start).Select(Clone));
        // Empty paragraphs have no Markdown form: each stays after the projected block it followed.
        var leading = current.Blocks.Skip(start).Take((originals.Count == 0 ? end : originals[0].DocumentIndex) - start).Select(Clone);
        blocks.AddRange(leading);

        var keys = originals.Select(x => x.Prepared is null ? "s:" + x.SlotIndex : "t:" + Key(x.Prepared)).ToList();
        var newKeys = parsed.Blocks.Select(x => merge.IsBlockSlot(x!) is { } slot ? "s:" + slot : "t:" + Key((JsonObject)x!)).ToList();
        var pairs = Align(keys, newKeys);
        var aligned = pairs.Select(x => x.Left).ToHashSet();
        var moved = Moved(keys, newKeys, aligned, pairs.Select(x => x.Right).ToHashSet());
        var movedOriginals = moved.Values.ToHashSet();
        // Images of the replaced blocks, for new blocks that keep their Markdown; an aligned or moved block keeps its own anyway.
        merge.KeepImages(originals.Where((_, index) => !aligned.Contains(index) && !movedOriginals.Contains(index))
            .Select(x => (x.DocumentIndex, current.Blocks[x.DocumentIndex])), parsed.Blocks);
        // Stored blocks no new block of their gap continues, and the types of new blocks that continue no stored one: together a
        // block moved and edited (or changed into another kind), whose look would otherwise vanish silently.
        var orphans = new List<(int Index, string Type, bool Replaced)>();
        var fresh = new HashSet<string>(StringComparer.Ordinal);
        // Ids a heading took by level and order beside an inserted or removed heading: which one continues it is a guess (D-M4).
        var guessed = new List<string>();
        // Stored blocks of gaps where a new image got the editor's default node: a stored look left there was replaced.
        var imageGaps = new HashSet<int>();
        var previousOriginal = 0;
        var previousNew = 0;
        foreach (var (originalIndex, newIndex) in pairs.Append((originals.Count, parsed.Blocks.Count)))
        {
            // The gap before this pair: the new blocks replace the original ones; a changed heading keeps the id of an
            // original heading of the same level in the gap, in order (D8).
            var gapOriginals = originals.Skip(previousOriginal).Take(originalIndex - previousOriginal)
                .Where((_, offset) => !movedOriginals.Contains(previousOriginal + offset)).ToList();
            var candidates = gapOriginals.Where(x => x.Prepared is not null && RichDocument.TypeOf(current.Blocks[x.DocumentIndex]) == "heading").ToList();
            // The candidates of each level in order: a heading takes the first one of its level after the last one taken.
            var byLevel = candidates.Select((x, position) => (Level: Level(current.Blocks[x.DocumentIndex]), Position: position))
                .GroupBy(x => x.Level).ToDictionary(x => x.Key, x => new Queue<int>(x.Select(y => y.Position)));
            var candidate = 0;
            var gapBlocks = new List<JsonObject>();
            var texts = new List<int>();
            var headingSources = new List<(int Stored, int New)>();
            var gapIds = new List<string>();
            var writtenIds = new List<(int Position, string Id)>();
            var newHeadings = 0;
            var defaulted = merge.DefaultedImages;
            for (var index = previousNew; index < newIndex; index++)
            {
                var block = parsed.Blocks[index];
                if (moved.TryGetValue(index, out var source))
                {
                    gapBlocks.Add(Clone(current.Blocks[originals[source].DocumentIndex]));
                    continue;
                }
                var converted = merge.Convert(block);
                if (merge.IsBlockSlot(block!) is null)
                {
                    texts.Add(gapBlocks.Count);
                }
                if (RichDocument.TypeOf(converted) == "heading" && merge.IsBlockSlot(block!) is null)
                {
                    string? id = null;
                    // {#id} in Markdown (dialect §2): the heading has that id, whatever the level and order say.
                    var written = converted["attrs"]?["headingId"] is JsonValue own && own.TryGetValue<string>(out var text) ? text : null;
                    newHeadings++;
                    if (byLevel.TryGetValue(Level(converted), out var sameLevel))
                    {
                        while (sameLevel.TryPeek(out var skipped) && skipped < candidate)
                        {
                            sameLevel.Dequeue();
                        }
                    }
                    if (sameLevel is not null && sameLevel.TryDequeue(out var taken))
                    {
                        var paired = candidates[taken];
                        candidate = taken + 1;
                        id = sections.TryGetValue(paired.DocumentIndex, out var section) && !section.HeadingIdIsTemporary ? section.HeadingId : null;
                        headingSources.Add((paired.DocumentIndex, gapBlocks.Count));
                        if (id is not null && written is null)
                        {
                            gapIds.Add(id);
                        }
                    }
                    converted["attrs"]!["headingId"] = written ?? id ?? context.NewHeadingId();
                    if (written is not null)
                    {
                        writtenIds.Add((gapBlocks.Count, written));
                    }
                }
                gapBlocks.Add(converted);
            }
            KeepGapPresentation(current, gapOriginals.Where(x => x.Prepared is not null).Select(x => x.DocumentIndex).ToList(), gapBlocks, texts,
                headingSources, losses, orphans, fresh);
            foreach (var (position, written) in writtenIds)
            {
                KeepRequirement(current, gapBlocks[position], written);
            }
            if (newHeadings != candidates.Count)
            {
                guessed.AddRange(gapIds);
            }
            if (merge.DefaultedImages > defaulted)
            {
                imageGaps.UnionWith(gapOriginals.Select(x => x.DocumentIndex));
            }
            blocks.AddRange(gapBlocks);
            for (var position = previousOriginal; position < originalIndex; position++)
            {
                blocks.AddRange(Trailing(current, originals, position, end));
            }
            if (originalIndex < originals.Count)
            {
                var original = originals[originalIndex];
                blocks.Add(Clone(current.Blocks[original.DocumentIndex]));
                blocks.AddRange(Trailing(current, originals, originalIndex, end));
            }
            previousOriginal = originalIndex + 1;
            previousNew = newIndex + 1;
        }
        blocks.AddRange(current.Blocks.Skip(end).Select(Clone));
        foreach (var (index, _, _) in orphans.Where(x => x.Replaced || fresh.Contains(x.Type)))
        {
            Record(losses, index, Dropped(current.Blocks[index]));
        }
        foreach (var (index, image) in merge.LostImages().Where(x => imageGaps.Contains(x.Index)))
        {
            Record(losses, index, [(image, "image")]);
        }
        if (losses.Count > 0 && !resetFormatting)
        {
            throw FormattingLost(current, projection, sections, losses);
        }

        WarnHeadingIds(current, blocks, guessed, merge);
        var envelope = (JsonObject)current.Envelope.DeepClone();
        envelope.Remove("document");
        // A page that ends in a table (or list) without the editor's spacing paragraph keeps ending so: a round trip must not add one.
        return Finish(envelope, blocks, parsed.Warnings.Concat(merge.Warnings), context, endParagraph: current.Blocks.Count == 0 || RichDocument.TypeOf(current.Blocks[^1]) == "paragraph");
    }

    /// <summary>
    /// A heading written with <c>{#id}</c> (dialect §2): without <c>state=</c> it keeps the requirement attributes of the stored heading
    /// with that id (so <c>atlas_update_section</c> cannot make a requirement plain by leaving them out), and it has that heading's former
    /// ids, or none. A heading written without a block is plain.
    /// </summary>
    private static void KeepRequirement(RichDocument current, JsonObject heading, string id)
    {
        var stored = current.Blocks.FirstOrDefault(x => RichDocument.TypeOf(x) == "heading"
            && x["attrs"]?["headingId"] is JsonValue value && value.TryGetValue<string>(out var text) && text == id)?["attrs"] as JsonObject;
        var attributes = Attributes(heading);
        IEnumerable<string> keys = attributes.ContainsKey("state") ? ["aliases"] : RequirementHeadingMarkdown.Attributes.Append("aliases");
        foreach (var key in keys)
        {
            if (stored?[key] is { } kept)
            {
                attributes[key] = kept.DeepClone();
            }
            else if (key == "aliases")
            {
                attributes.Remove(key);
            }
        }
    }

    /// <summary>
    /// Heading ids are addresses (<c>?heading=</c>, D8): an id of the stored page the result no longer holds, and one a heading took
    /// only by level and order, are named in a warning so the client can check its links (D-M4).
    /// </summary>
    private static void WarnHeadingIds(RichDocument current, List<JsonObject> blocks, List<string> guessed, Conversion merge)
    {
        static HashSet<string> Ids(IEnumerable<JsonNode> nodes)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            WalkAll(nodes, x =>
            {
                if (RichDocument.TypeOf(x) == "heading" && x["attrs"]?["headingId"] is JsonValue value && value.TryGetValue<string>(out var id)
                    && HeadingIds.IsValid(id) && !HeadingIds.IsTemporary(id))
                {
                    ids.Add(id);
                }
            });
            return ids;
        }
        var kept = Ids(blocks);
        var gone = Ids(current.Blocks).Where(x => !kept.Contains(x)).ToList();
        if (gone.Count > 0)
        {
            merge.Warn("Nadpisy s ID " + Listed(gone) + " už na stránce nejsou; odkazy ?heading= na ně nepovedou.");
        }
        if (guessed.Count > 0)
        {
            merge.Warn("ID nadpisů " + Listed(guessed) + " převzaly podle úrovně a pořadí upravené nadpisy; ověřte, že odkazy ?heading= vedou na správné místo.");
        }
    }

    /// <summary>
    /// What Markdown does not carry (<see cref="RichMarkdownProjection.Presentation"/>) for the blocks of one rewritten gap: a heading
    /// from the stored heading whose id it keeps, every other block from the stored block it is paired with (<see cref="PairGap"/>).
    /// What has nowhere unambiguous to go is recorded in <paramref name="losses"/> by the stored top-level block that held it. A stored
    /// block whose type no new block of the gap has goes to <paramref name="orphans"/> (replaced when the gap has a new block of a
    /// type it did not hold), and the type of every new block that continues no stored one to <paramref name="fresh"/>: the merge
    /// decides at the end whether the stored block was deleted or moved away edited.
    /// </summary>
    private static void KeepGapPresentation(RichDocument current, List<int> stored, List<JsonObject> gap, List<int> texts,
        List<(int Stored, int New)> headings, SortedDictionary<int, SortedSet<string>> losses, List<(int Index, string Type, bool Replaced)> orphans,
        HashSet<string> fresh)
    {
        foreach (var (index, position) in headings)
        {
            Record(losses, index, KeepPresentation(current.Blocks[index], gap[position]));
            Record(losses, index, KeepLinks(current.Blocks[index], gap[position]));
        }
        var storedTypes = stored.Select(x => RichDocument.TypeOf(current.Blocks[x])).ToHashSet(StringComparer.Ordinal);
        var replaced = texts.Any(x => !storedTypes.Contains(RichDocument.TypeOf(gap[x])));
        var storedHeadings = stored.Where(x => RichDocument.TypeOf(current.Blocks[x]) == "heading").ToList();
        var newHeadings = texts.Where(x => RichDocument.TypeOf(gap[x]) == "heading").ToList();
        var pairedStored = headings.Select(x => x.Stored).ToHashSet();
        foreach (var index in storedHeadings.Where(x => !pairedStored.Contains(x)))
        {
            if (newHeadings.Count > 0)
            {
                Record(losses, index, Dropped(current.Blocks[index]));
            }
            else
            {
                orphans.Add((index, "heading", replaced));
            }
        }
        var pairedHeadings = headings.Select(x => x.New).ToHashSet();
        if (newHeadings.Any(x => !pairedHeadings.Contains(x)))
        {
            fresh.Add("heading");
        }
        var others = stored.Except(storedHeadings).ToList();
        var news = texts.Where(x => RichDocument.TypeOf(gap[x]) != "heading").ToList();
        var (pairs, unplaced, deleted) = PairGap(others.Select(x => current.Blocks[x]).ToList(), Enumerable.Range(0, others.Count).ToList(), gap, news);
        foreach (var (left, right) in pairs)
        {
            Record(losses, others[left], KeepPresentation(current.Blocks[others[left]], gap[right]));
            Record(losses, others[left], KeepLinks(current.Blocks[others[left]], gap[right]));
        }
        foreach (var left in unplaced)
        {
            Record(losses, others[left], Dropped(current.Blocks[others[left]]));
        }
        foreach (var left in deleted)
        {
            orphans.Add((others[left], RichDocument.TypeOf(current.Blocks[others[left]]), replaced));
        }
        var paired = pairs.Select(x => x.New).ToHashSet();
        foreach (var right in news.Where(x => !paired.Contains(x)))
        {
            fresh.Add(RichDocument.TypeOf(gap[right]));
        }
    }

    /// <summary>
    /// Blocks moved as they are (D-H2): each stored block the alignment left out, matched to a left-out new block with exactly its
    /// key, in order. The new block is then the stored block itself: heading id, presentation and link attributes included.
    /// Placeholders bring their nodes back anyway.
    /// </summary>
    private static Dictionary<int, int> Moved(List<string> keys, List<string> newKeys, HashSet<int> aligned, HashSet<int> alignedNew)
    {
        var stored = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var index = 0; index < keys.Count; index++)
        {
            if (!aligned.Contains(index) && keys[index].StartsWith("t:", StringComparison.Ordinal))
            {
                if (!stored.TryGetValue(keys[index], out var queue))
                {
                    stored[keys[index]] = queue = new Queue<int>();
                }
                queue.Enqueue(index);
            }
        }
        var moved = new Dictionary<int, int>();
        for (var index = 0; index < newKeys.Count; index++)
        {
            if (!alignedNew.Contains(index) && stored.TryGetValue(newKeys[index], out var queue) && queue.TryDequeue(out var source))
            {
                moved[index] = source;
            }
        }
        return moved;
    }

    private static void Record(SortedDictionary<int, SortedSet<string>> losses, int index, IEnumerable<(JsonObject Node, string Key)> lost)
    {
        foreach (var (_, key) in lost)
        {
            if (!losses.TryGetValue(index, out var keys))
            {
                losses[index] = keys = new SortedSet<string>(StringComparer.Ordinal);
            }
            keys.Add(key);
        }
    }

    /// <summary>
    /// The stored node's presentation onto the edited node of the same type, and its children's onto the children they are paired
    /// with; returns what could not be placed.
    /// </summary>
    private static List<(JsonObject Node, string Key)> KeepPresentation(JsonObject stored, JsonObject converted)
    {
        var lost = new List<(JsonObject, string)>();
        if (RichDocument.TypeOf(stored) != RichDocument.TypeOf(converted))
        {
            lost.AddRange(Formatted(stored));
            return lost;
        }
        KeepOwn(stored, converted);
        if (RichDocument.TypeOf(stored) == "table")
        {
            KeepTable(stored, converted, lost);
        }
        else if (RichDocument.TypeOf(stored) is not ("paragraph" or "heading" or "codeBlock"))
        {
            KeepChildren(stored, converted, lost);
        }
        return lost;
    }

    private static void KeepChildren(JsonObject stored, JsonObject converted, List<(JsonObject, string)> lost)
    {
        var before = Children(stored);
        var after = Children(converted);
        var (pairs, unplaced, _) = Pair(before, after);
        foreach (var (left, right) in pairs)
        {
            lost.AddRange(KeepPresentation(before[left], after[right]));
        }
        foreach (var left in unplaced)
        {
            lost.AddRange(Formatted(before[left]));
        }
    }

    /// <summary>
    /// A table: its columns paired by their header cells and its rows like any children. A column's width goes to its cells in every
    /// row (a new row included, as the editor's geometry wants); a paired row keeps its height, and its cells in paired columns their
    /// colour and alignment. A new row or column has the editor's defaults.
    /// </summary>
    private static void KeepTable(JsonObject stored, JsonObject converted, List<(JsonObject, string)> lost)
    {
        var rows = Children(stored);
        var edited = Children(converted);
        if (rows.Count == 0 || edited.Count == 0)
        {
            return;
        }
        var (columns, dropped, _) = Pair(Children(rows[0]), Children(edited[0]));
        foreach (var row in edited)
        {
            var cells = Children(row);
            foreach (var (left, right) in columns)
            {
                if (right < cells.Count && Children(rows[0]).ElementAtOrDefault(left)?["attrs"]?["colwidth"] is { } width)
                {
                    Attributes(cells[right])["colwidth"] = width.DeepClone();
                }
            }
        }
        foreach (var column in dropped)
        {
            foreach (var row in rows)
            {
                if (Children(row).ElementAtOrDefault(column) is { } cell)
                {
                    lost.AddRange(Formatted(cell));
                }
            }
        }
        var (pairs, unplaced, _) = Pair(rows, edited);
        foreach (var (left, right) in pairs)
        {
            KeepOwn(rows[left], edited[right]);
            var before = Children(rows[left]);
            var after = Children(edited[right]);
            foreach (var (column, position) in columns.Where(x => x.Stored < before.Count && x.New < after.Count))
            {
                KeepOwn(before[column], after[position]);
                KeepChildren(before[column], after[position], lost);
            }
        }
        foreach (var row in unplaced)
        {
            // The width is the column's and stays with it; a dropped column was counted above.
            lost.AddRange(Formatted(rows[row]).Where(x => x.Key != "colwidth"));
        }
    }

    /// <summary>
    /// Pairs stored and edited siblings: equal content first (<see cref="Align"/> on the content without presentation), then within each
    /// gap per <see cref="PairGap"/>.
    /// </summary>
    private static (List<(int Stored, int New)> Pairs, List<int> Unplaced, List<int> Deleted) Pair(IReadOnlyList<JsonObject> stored,
        IReadOnlyList<JsonObject> converted)
    {
        var pairs = new List<(int, int)>();
        var unplaced = new List<int>();
        var deleted = new List<int>();
        var previousStored = 0;
        var previousNew = 0;
        foreach (var (left, right) in Align(stored.Select(BareKey).ToList(), converted.Select(BareKey).ToList()).Append((stored.Count, converted.Count)))
        {
            var gap = PairGap(stored, Enumerable.Range(previousStored, left - previousStored).ToList(), converted,
                Enumerable.Range(previousNew, right - previousNew).ToList());
            pairs.AddRange(gap.Pairs);
            unplaced.AddRange(gap.Unplaced);
            deleted.AddRange(gap.Deleted);
            if (left < stored.Count)
            {
                pairs.Add((left, right));
            }
            previousStored = left + 1;
            previousNew = right + 1;
        }
        return (pairs, unplaced, deleted);
    }

    /// <summary>
    /// Within one rewritten gap (what equal content did not pair), the stored and new nodes of each type paired in order, but only when
    /// they are as many (edits in place): with a node inserted or removed beside edited ones, which stored node a new one continues is
    /// not clear, so the stored ones are returned and the new ones keep the defaults. A paragraph split keeps its look on every part;
    /// paragraphs merged keep the look they all share. A type with no new node in the gap was deleted, changed into another kind of
    /// block or moved away: returned as deleted, the caller tells which.
    /// </summary>
    private static (List<(int Stored, int New)> Pairs, List<int> Unplaced, List<int> Deleted) PairGap(IReadOnlyList<JsonObject> stored, List<int> olds,
        IReadOnlyList<JsonObject> converted, List<int> news)
    {
        var pairs = new List<(int, int)>();
        var unplaced = new List<int>();
        var deleted = new List<int>();
        foreach (var kind in olds.GroupBy(x => RichDocument.TypeOf(stored[x])))
        {
            var left = kind.ToList();
            var right = news.Where(x => RichDocument.TypeOf(converted[x]) == kind.Key).ToList();
            var textblock = kind.Key is "paragraph" or "heading";
            if (right.Count == 0)
            {
                deleted.AddRange(left);
                continue;
            }
            if (textblock && left.Count == 1 && right.Count > 1)
            {
                pairs.AddRange(right.Select(x => (left[0], x)));
                continue;
            }
            if (textblock && right.Count == 1 && left.Count > 1 && left.All(x => Look(stored[x]) == Look(stored[left[0]])))
            {
                pairs.Add((left[0], right[0]));
                continue;
            }
            if (left.Count == right.Count)
            {
                pairs.AddRange(left.Zip(right));
            }
            else
            {
                unplaced.AddRange(left);
            }
        }
        return (pairs, unplaced, deleted);
    }

    private static void KeepOwn(JsonObject stored, JsonObject converted)
    {
        if (RichMarkdownProjection.Presentation.TryGetValue(RichDocument.TypeOf(stored), out var keys) && stored["attrs"] is JsonObject kept)
        {
            foreach (var key in keys)
            {
                if (kept[key] is { } value)
                {
                    Attributes(converted)[key] = value.DeepClone();
                }
            }
        }
        // A nested heading (quote, callout, list) keeps its id at any depth (D-M1); a top-level one already has the id of its section.
        if (RichDocument.TypeOf(stored) == "heading" && stored["attrs"]?["headingId"] is JsonValue id && converted["attrs"]?["headingId"] is null)
        {
            Attributes(converted)["headingId"] = id.DeepClone();
        }
    }

    private static JsonObject Attributes(JsonObject node)
    {
        if (node["attrs"] is not JsonObject attributes)
        {
            attributes = [];
            node["attrs"] = attributes;
        }
        return attributes;
    }

    private static List<JsonObject> Children(JsonObject node)
    {
        return (node["content"] as JsonArray ?? []).OfType<JsonObject>().ToList();
    }

    /// <summary>A node's own presentation as text, to compare looks.</summary>
    private static string Look(JsonObject node)
    {
        var look = new JsonObject();
        foreach (var (_, key) in Formatted(node).Where(x => x.Node == node))
        {
            look[key] = node["attrs"]![key]!.DeepClone();
        }
        return look.ToJsonString();
    }

    /// <summary>The presentation set within a node (itself included): what an edit would lose with it. Left alignment and decimal numbering are the defaults.</summary>
    private static List<(JsonObject Node, string Key)> Formatted(JsonObject node)
    {
        var found = new List<(JsonObject, string)>();
        Walk(node, x =>
        {
            if (RichMarkdownProjection.Presentation.TryGetValue(RichDocument.TypeOf(x), out var keys) && x["attrs"] is JsonObject attributes)
            {
                foreach (var key in keys)
                {
                    if (attributes[key] is { } value && !(value is JsonValue text && text.TryGetValue<string>(out var name) && (key, name) is ("textAlign", "left") or ("type", "1") or (_, "")))
                    {
                        found.Add((x, key));
                    }
                }
            }
        });
        return found;
    }

    /// <summary>A block as Markdown sees it without its presentation: the key that pairs an unchanged child.</summary>
    private static string BareKey(JsonObject node)
    {
        var bare = (JsonObject)node.DeepClone();
        Walk(bare, x =>
        {
            if (RichMarkdownProjection.Presentation.TryGetValue(RichDocument.TypeOf(x), out var keys) && x["attrs"] is JsonObject attributes)
            {
                foreach (var key in keys)
                {
                    attributes.Remove(key);
                }
            }
        });
        return Key(bare);
    }

    /// <summary>
    /// 409 <c>formatting_lost</c>: the edited blocks named by section (heading id) and position, with what they would lose, and in
    /// <c>blocks</c> the same as data. No stored text is echoed.
    /// </summary>
    private static ApiException FormattingLost(RichDocument current, ContentProjection projection, Dictionary<int, ContentSection> sections,
        SortedDictionary<int, SortedSet<string>> losses)
    {
        var described = new List<string>();
        var blocks = new List<Dictionary<string, object?>>();
        foreach (var (index, keys) in losses)
        {
            var heading = sections.Keys.Where(x => x <= index).DefaultIfEmpty(-1).Max();
            var headingId = heading < 0 ? null : sections[heading].HeadingId;
            var ordinal = projection.Blocks.Count(x => x.DocumentIndex >= heading && x.DocumentIndex <= index);
            var type = RichDocument.TypeOf(current.Blocks[index]);
            described.Add(BlockNames.GetValueOrDefault(type, "blok") + " (" + ordinal.ToString(CultureInfo.InvariantCulture) + ". blok "
                + (headingId is null ? "před prvním nadpisem" : "sekce " + headingId) + ": "
                + string.Join(", ", keys.Select(x => PresentationNames.GetValueOrDefault(x, x))) + ")");
            blocks.Add(new Dictionary<string, object?> { ["headingId"] = headingId, ["block"] = ordinal, ["type"] = type, ["attributes"] = keys.ToArray() });
        }
        return new ApiException(409, "Úprava by odstranila formátování, které Markdown nenese: " + string.Join("; ", described)
            + ". Zachovejte tvar upravených bloků (stejné řádky a sloupce tabulky, položky seznamu), nebo formátování výslovně zahoďte s resetFormatting: true; nic se neuložilo.",
            "formatting_lost", new Dictionary<string, object?> { ["blocks"] = blocks });
    }

    /// <summary>
    /// The links of an edited block keep the target, rel and class of the links of the block it replaces (D-L4): links to one address
    /// in order, all of them when they all look the same; links whose address changed in order when as many changed. A stored link
    /// with its own attributes and no such counterpart is returned as lost.
    /// </summary>
    private static List<(JsonObject Node, string Key)> KeepLinks(JsonObject stored, JsonObject converted)
    {
        var lost = new List<(JsonObject, string)>();
        var before = Links(stored).GroupBy(x => x.Href, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Select(y => y.Attributes).ToList(), StringComparer.Ordinal);
        var after = Links(converted).GroupBy(x => x.Href, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Select(y => y.Attributes).ToList(), StringComparer.Ordinal);
        void Carry(List<JsonObject> olds, List<JsonObject> news)
        {
            var shared = olds.Select(LinkLook).Distinct(StringComparer.Ordinal).Count() == 1;
            for (var index = 0; index < news.Count && (shared || index < olds.Count); index++)
            {
                var kept = olds[Math.Min(index, olds.Count - 1)];
                foreach (var key in new[] { "target", "rel", "class" })
                {
                    news[index].Remove(key);
                    if (kept.TryGetPropertyValue(key, out var value))
                    {
                        news[index][key] = value?.DeepClone();
                    }
                }
            }
            if (!shared && olds.Count != news.Count)
            {
                lost.AddRange(olds.Where(IsStyledLink).Select(x => (x, "link")));
            }
        }
        foreach (var (href, news) in after)
        {
            if (before.TryGetValue(href, out var olds))
            {
                Carry(olds, news);
            }
        }
        var vanished = before.Where(x => !after.ContainsKey(x.Key)).SelectMany(x => x.Value).ToList();
        var appeared = after.Where(x => !before.ContainsKey(x.Key)).SelectMany(x => x.Value).ToList();
        if (vanished.Count > 0 && vanished.Count == appeared.Count)
        {
            for (var index = 0; index < vanished.Count; index++)
            {
                Carry([vanished[index]], [appeared[index]]);
            }
        }
        else if (appeared.Count > 0)
        {
            lost.AddRange(vanished.Where(IsStyledLink).Select(x => (x, "link")));
        }
        return lost;
    }

    /// <summary>What a stored top-level block that has no counterpart loses: its presentation and the attributes of its links.</summary>
    private static List<(JsonObject Node, string Key)> Dropped(JsonObject block)
    {
        return [.. Formatted(block), .. Links(block).Select(x => x.Attributes).Where(IsStyledLink).Select(x => (x, "link"))];
    }

    private static string LinkLook(JsonObject attributes)
    {
        return new JsonArray([.. new[] { "target", "rel", "class" }.Select(key => RichDocument.Attribute(attributes, "link", key)?.DeepClone())]).ToJsonString();
    }

    /// <summary>A link with attributes other than the ones a new Markdown link gets (<see cref="RichNodes.LinkMark"/>).</summary>
    private static bool IsStyledLink(JsonObject attributes)
    {
        return LinkLook(attributes) != DefaultLinkLook;
    }

    private static List<(string Href, JsonObject Attributes)> Links(JsonObject block)
    {
        var links = new List<(string, JsonObject)>();
        Walk(block, node =>
        {
            foreach (var mark in (node["marks"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (RichDocument.TypeOf(mark) == "link" && mark["attrs"] is JsonObject attributes && attributes["href"] is JsonValue value
                    && value.TryGetValue<string>(out var href))
                {
                    links.Add((href, attributes));
                }
            }
        });
        return links;
    }

    /// <summary>
    /// A whole page removes a component only when asked: every placeholder left out must be listed (409 <c>components_removed</c>
    /// naming them with their labels), and every listed number must be a placeholder of the page that the Markdown left out.
    /// </summary>
    private static void RequireRemovals(ContentProjection projection, IReadOnlyList<int> missing, IReadOnlyCollection<int> listed)
    {
        // Sets, not lists: the client sends the list, so its length must not multiply with the page's components.
        var components = projection.Components.Select(x => x.Index).ToHashSet();
        var omitted = missing.ToHashSet();
        var requested = listed.ToHashSet();
        var unknown = requested.Where(x => !components.Contains(x)).Order().ToList();
        var kept = requested.Where(x => components.Contains(x) && !omitted.Contains(x)).Order().ToList();
        if (unknown.Count > 0 || kept.Count > 0)
        {
            var problems = new List<string>();
            if (unknown.Count > 0)
            {
                problems.Add(Listed(unknown.Select(MarkdownToRich.Placeholder)) + " na stránce není");
            }
            if (kept.Count > 0)
            {
                problems.Add(Listed(kept.Select(MarkdownToRich.Placeholder)) + " v Markdownu zůstává");
            }
            throw new ApiException(400, "removeComponents uvádí jen značky ⟦c:N⟧ stránky, které Markdown vynechává: " + string.Join("; ", problems) + ".",
                "invalid_argument");
        }
        var unlisted = missing.Where(x => !requested.Contains(x)).ToList();
        if (unlisted.Count > 0)
        {
            throw new ApiException(409, "Zápis by odebral komponenty " + Described(projection, unlisted) + ". Vraťte jejich značky do Markdownu, " +
                "nebo je pro odebrání uveďte v removeComponents; nic se neuložilo. Byla-li předchozí četba zkrácená (truncated: true), značky z nepřečtené části v Markdownu chybí: " +
                "dočtěte stránku přes nextCursor, nebo upravte jen sekci (atlas_update_section).", "components_removed",
                new Dictionary<string, object?> { ["components"] = unlisted });
        }
    }

    private static string Described(ContentProjection projection, IEnumerable<int> indices)
    {
        var labels = projection.Components.ToDictionary(x => x.Index, x => x.Label);
        return Listed(indices.Select(x => MarkdownToRich.Placeholder(x) + (labels.TryGetValue(x, out var label) ? " (" + label + ")" : "")));
    }

    /// <summary>At most <see cref="ListedNames"/> names and how many more: a message never grows with what the client sent.</summary>
    private static string Listed(IEnumerable<string> names)
    {
        var all = names.ToList();
        var shown = string.Join(", ", all.Take(ListedNames));
        return all.Count > ListedNames ? shown + " a dalších " + (all.Count - ListedNames).ToString(CultureInfo.InvariantCulture) : shown;
    }

    /// <summary>The empty paragraphs between a projected block and the next one within the range.</summary>
    private static IEnumerable<JsonObject> Trailing(RichDocument current, List<ProjectedBlock> originals, int position, int end)
    {
        var start = originals[position].DocumentIndex;
        var next = position + 1 < originals.Count ? originals[position + 1].DocumentIndex : end;
        return current.Blocks.Skip(start + 1).Take(next - start - 1).Select(Clone);
    }

    /// <param name="keepStored">An embed edit (<see cref="UpdateEmbed"/>): heading ids and plugin declarations stay exactly as stored.</param>
    private static MergeResult Finish(JsonObject envelope, List<JsonObject> blocks, IEnumerable<string> warnings, MergeContext context, bool keepStored = false,
        bool endParagraph = true)
    {
        // The editor keeps a paragraph after a closing list, table or component; writing it here opens the page unchanged.
        if (endParagraph && (blocks.Count == 0 || RichDocument.TypeOf(blocks[^1]) != "paragraph"))
        {
            blocks.Add(new JsonObject { ["type"] = "paragraph" });
        }
        if (!keepStored)
        {
            RequireHeadingIds(blocks, context);
        }
        var document = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(blocks.Select(x => (JsonNode)x).ToArray()) };
        if (!keepStored)
        {
            DeclarePlugins(envelope, document);
        }
        var result = new JsonObject();
        foreach (var (key, value) in envelope)
        {
            result[key] = value?.DeepClone();
            if (key == "version")
            {
                result["document"] = document;
            }
        }
        result["document"] ??= document;
        var json = result.ToJsonString(RichDocument.JsonWrite);
        var markdown = EditorMarkdownWriter.Write(document);
        try
        {
            Rules.ValidateDocument(json, markdown);
        }
        catch (ApiException error) when (error.Code == "document_too_large")
        {
            throw new ApiException(413, error.Message, "invalid_markdown");
        }
        return new MergeResult(json, markdown, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Every heading ends with a valid id unique in the page: a legacy heading (none, malformed or a temporary one) or a
    /// later duplicate gets a new id, as D8 says for writes.
    /// </summary>
    private static void RequireHeadingIds(List<JsonObject> blocks, MergeContext context)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in blocks.Where(x => RichDocument.TypeOf(x) == "heading"))
        {
            var attributes = heading["attrs"] as JsonObject ?? [];
            heading["attrs"] = attributes;
            var id = attributes["headingId"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            if (id is null || !HeadingIds.IsValid(id) || HeadingIds.IsTemporary(id) || !used.Add(id))
            {
                id = context.NewHeadingId();
                attributes["headingId"] = id;
                used.Add(id);
            }
        }
    }

    /// <summary>Declares the plugin of every node type present; declarations already in the envelope stay as they are.</summary>
    internal static void DeclarePlugins(JsonObject envelope, JsonObject document)
    {
        var plugins = envelope["plugins"] as JsonArray ?? [];
        envelope["plugins"] = plugins;
        var declared = plugins.Select(x => x?["id"] is JsonValue id && id.TryGetValue<string>(out var name) ? name : null).ToHashSet(StringComparer.Ordinal);
        var needed = new SortedSet<string>(StringComparer.Ordinal);
        Walk(document, node =>
        {
            if (PluginOfNode.TryGetValue(RichDocument.TypeOf(node), out var plugin) && !declared.Contains(plugin) && CollaborationSchema.CurrentPluginVersions.ContainsKey(plugin))
            {
                needed.Add(plugin);
            }
            // The requirement heading's attributes and the dependency line belong to atlas-requirements (schema 3).
            if (node["attrs"] is JsonObject attributes && !declared.Contains("atlas-requirements")
                && (attributes.ContainsKey("dependencies") || (RichDocument.TypeOf(node) == "heading" && RequirementHeadingMarkdown.Attributes.Any(attributes.ContainsKey))))
            {
                needed.Add("atlas-requirements");
            }
        });
        foreach (var plugin in needed)
        {
            plugins.Add(new JsonObject { ["id"] = plugin, ["version"] = CollaborationSchema.CurrentPluginVersions[plugin] });
        }
    }

    // ---- input ----

    private static void RequireSize(string markdown)
    {
        if (markdown.Length > Rules.MaxDocumentMarkdownLength)
        {
            throw new ApiException(413, "Markdown je příliš dlouhý: nejvýše 1 000 000 znaků. Upravte stránku po sekcích.", "invalid_markdown");
        }
    }

    /// <summary>
    /// The strict parse (an invalid <c>a-</c> component is 400 <c>invalid_document</c>; a linked table's cells take ids of the context),
    /// refused when it holds a password directive: in the input, or in the text it parses to (an HTML
    /// entity can spell the colon), code included. Existing references come back only through their placeholders.
    /// </summary>
    private static MarkdownToRichResult Parse(string markdown, MergeContext context)
    {
        var parsed = MarkdownToRich.Parse(markdown, MarkdownToRich.PagePlugins, strict: true, () => "chart-" + context.NewHeadingId());
        var written = EditorMarkdownWriter.WriteProjection(new JsonObject { ["type"] = "doc", ["content"] = parsed.Blocks.DeepClone() });
        if (SecretDirective().IsMatch(markdown) || SecretDirective().IsMatch(written) || Texts(parsed.Blocks).Any(x => SecretDirective().IsMatch(x)))
        {
            throw new ApiException(400, "Odkaz na heslo nejde zapsat Markdownem (:secret{…}); stávající odkazy zůstávají jako značky ⟦c:N⟧.", "secret_directive_forbidden");
        }
        return parsed;
    }

    // ---- sections ----

    private static (int Start, int End, ContentSection Section) SectionRange(RichDocument current, ContentProjection projection, string headingId)
    {
        var headings = projection.Blocks.Where(x => RichDocument.TypeOf(current.Blocks[x.DocumentIndex]) == "heading").ToList();
        var ordinal = projection.Sections.ToList().FindIndex(x => x.HeadingId == headingId);
        if (ordinal < 0 || ordinal >= headings.Count)
        {
            // A temporary id is the n-th heading of the version it was read from: once the page moved on (a save, people's live edits
            // checkpointed before the write) it addresses nothing safely, so the agent reads the sections again instead of guessing.
            if (HeadingIds.IsTemporary(headingId))
            {
                throw new ApiException(400, $"Dočasné ID nadpisu {headingId} platí jen pro verzi stránky, ze které bylo přečteno, a stránka se mezitím změnila. " +
                    "Načtěte ji znovu (atlas_get_page, version \"working\") a použijte headingId a expectedVersion z nového čtení.", "invalid_argument");
            }
            throw new ApiException(404, "Sekce s tímto ID nadpisu na stránce není.", "section_not_found");
        }
        var section = projection.Sections[ordinal];
        var next = projection.Sections.Skip(ordinal + 1).Select((x, offset) => (x, offset)).FirstOrDefault(x => x.x.Level <= section.Level);
        var end = next.x is null ? current.Blocks.Count : headings[ordinal + 1 + next.offset].DocumentIndex;
        return (headings[ordinal].DocumentIndex, end, section);
    }

    /// <summary>The projection's section of each heading block (the n-th section is the n-th heading).</summary>
    private static Dictionary<int, ContentSection> SectionsByBlock(RichDocument current, ContentProjection projection)
    {
        return projection.Blocks.Where(x => RichDocument.TypeOf(current.Blocks[x.DocumentIndex]) == "heading")
            .Zip(projection.Sections).ToDictionary(x => x.First.DocumentIndex, x => x.Second);
    }

    // ---- alignment ----

    /// <summary>
    /// The longest common subsequence of two key lists as index pairs in order. A common prefix and suffix are taken
    /// directly; a middle too large for the table is taken as rewritten.
    /// </summary>
    private static List<(int Left, int Right)> Align(List<string> left, List<string> right)
    {
        var pairs = new List<(int, int)>();
        var prefix = 0;
        while (prefix < left.Count && prefix < right.Count && left[prefix] == right[prefix])
        {
            pairs.Add((prefix, prefix));
            prefix++;
        }
        var suffix = 0;
        while (suffix < left.Count - prefix && suffix < right.Count - prefix && left[^(suffix + 1)] == right[^(suffix + 1)])
        {
            suffix++;
        }
        var rows = left.Count - prefix - suffix;
        var columns = right.Count - prefix - suffix;
        if (rows > 0 && columns > 0 && (long)rows * columns <= MaxAlignmentCells)
        {
            var table = new int[rows + 1, columns + 1];
            for (var i = rows - 1; i >= 0; i--)
            {
                for (var j = columns - 1; j >= 0; j--)
                {
                    table[i, j] = left[prefix + i] == right[prefix + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
                }
            }
            for (int i = 0, j = 0; i < rows && j < columns;)
            {
                if (left[prefix + i] == right[prefix + j])
                {
                    pairs.Add((prefix + i, prefix + j));
                    i++;
                    j++;
                }
                else if (table[i + 1, j] >= table[i, j + 1])
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }
        }
        for (var index = suffix; index > 0; index--)
        {
            pairs.Add((left.Count - index, right.Count - index));
        }
        return pairs;
    }

    /// <summary>A block as Markdown sees it (<see cref="RichDocument.Comparable"/>): heading ids and list tightness do not count.</summary>
    private static string Key(JsonObject block)
    {
        return RichDocument.Comparable(block)!.ToJsonString();
    }

    // ---- helpers ----

    private static int Level(JsonNode heading)
    {
        return heading["attrs"]?["level"] is JsonValue value && value.TryGetValue<int>(out var level) ? level : 1;
    }

    private static int? BlockSlot(JsonNode block)
    {
        return RichDocument.TypeOf(block) == "paragraph" && block["content"] is JsonArray { Count: 1 } content && content[0] is JsonObject slot
            && RichDocument.TypeOf(slot) == MarkdownToRich.SlotNodeType && slot["marks"] is null
            && slot["attrs"]?["index"] is JsonValue value && value.TryGetValue<int>(out var index) ? index : null;
    }

    private static bool IsEmptyParagraph(JsonObject block)
    {
        return RichDocument.TypeOf(block) == "paragraph" && block["content"] is not JsonArray { Count: > 0 };
    }

    private static JsonObject Clone(JsonObject block)
    {
        return (JsonObject)block.DeepClone();
    }

    /// <summary>The text of every text node, code included.</summary>
    private static IEnumerable<string> Texts(JsonNode node)
    {
        var texts = new List<string>();
        Walk(node, x =>
        {
            if (RichDocument.TypeOf(x) == "text" && x["text"] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                texts.Add(text);
            }
        });
        return texts;
    }

    /// <summary>Link targets of the stored revision: a link the page already held is not new information.</summary>
    private static HashSet<string> LinkTargets(JsonObject document)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);
        Walk(document, node =>
        {
            foreach (var mark in (node["marks"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (RichDocument.TypeOf(mark) == "link" && mark["attrs"]?["href"] is JsonValue value && value.TryGetValue<string>(out var href))
                {
                    targets.Add(href);
                }
            }
            if (RichDocument.TypeOf(node) == "pageReference" && node["attrs"]?["url"] is JsonValue url && url.TryGetValue<string>(out var reference))
            {
                targets.Add(reference);
            }
        });
        return targets;
    }

    private static void WalkAll(IEnumerable<JsonNode> nodes, Action<JsonObject> visit)
    {
        foreach (var node in nodes)
        {
            Walk(node, visit);
        }
    }

    private static void Walk(JsonNode? node, Action<JsonObject> visit)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, visit);
                }
                break;
            case JsonObject value:
                visit(value);
                Walk(value["content"], visit);
                break;
        }
    }

    /// <summary>The conversion of new blocks: placeholders back to their nodes, page links resolved, links checked.</summary>
    private sealed class Conversion(MergeContext context, Dictionary<int, SlotBinding> bindings, HashSet<string> knownLinks)
    {
        private readonly List<string> m_warnings = [];
        private readonly Dictionary<string, PageLinkResolution> m_titles = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, bool> m_pages = [];
        private readonly Dictionary<string, Queue<JsonObject>> m_images = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(JsonObject Node, int Block)>> m_sources = new(StringComparer.Ordinal);
        // Per address the stored images still to take: those whose own Markdown no parsed image repeats first (taken ones skipped lazily).
        private readonly Dictionary<string, (Queue<JsonObject> Unwanted, Queue<JsonObject> Wanted)> m_free = new(StringComparer.Ordinal);
        private readonly HashSet<JsonObject> m_taken = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> m_wanted = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Warnings => m_warnings;

        /// <summary>The number of a block placeholder standing alone as a block; null for anything else.</summary>
        public int? IsBlockSlot(JsonNode block)
        {
            return BlockSlot(block) is { } index && bindings.TryGetValue(index, out var binding) && !IsInline(binding) ? index : null;
        }

        /// <summary>
        /// The placeholders of <paramref name="blocks"/> are exactly <paramref name="expected"/> in order, each a block
        /// placeholder alone on its line when it stands for a block; otherwise <c>components_changed</c> naming the difference.
        /// With <paramref name="omissible"/> (the whole page) a missing or reordered placeholder is no problem; the missing ones
        /// are returned.
        /// </summary>
        public IReadOnlyList<int> RequireSlots(JsonArray blocks, IReadOnlyList<int> expected, string rule, bool omissible = false)
        {
            var found = new List<(int Index, bool Alone)>();
            foreach (var block in blocks.OfType<JsonObject>())
            {
                if (BlockSlot(block) is { } alone)
                {
                    found.Add((alone, true));
                    continue;
                }
                Walk(block, node =>
                {
                    if (RichDocument.TypeOf(node) == MarkdownToRich.SlotNodeType && node["attrs"]?["index"] is JsonValue value && value.TryGetValue<int>(out var index))
                    {
                        found.Add((index, false));
                    }
                });
            }
            var problems = new List<string>();
            var wanted = expected.ToHashSet();
            var present = found.Select(x => x.Index).ToHashSet();
            var foreign = found.Select(x => x.Index).Where(x => !wanted.Contains(x)).Distinct().ToList();
            var missing = expected.Where(x => !present.Contains(x)).ToList();
            var repeated = found.GroupBy(x => x.Index).Where(x => x.Count() > 1 && wanted.Contains(x.Key)).Select(x => x.Key).ToList();
            var moved = found.Where(x => !x.Alone && bindings.TryGetValue(x.Index, out var binding) && !IsInline(binding) && wanted.Contains(x.Index))
                .Select(x => x.Index).Distinct().ToList();
            if (foreign.Count > 0)
            {
                problems.Add("navíc " + Names(foreign));
            }
            if (missing.Count > 0 && !omissible)
            {
                problems.Add("chybí " + Names(missing));
            }
            if (repeated.Count > 0)
            {
                problems.Add("vícekrát " + Names(repeated));
            }
            if (moved.Count > 0)
            {
                problems.Add("blok musí zůstat samostatně na řádku: " + Names(moved));
            }
            if (problems.Count == 0 && !omissible && !found.Select(x => x.Index).SequenceEqual(expected))
            {
                problems.Add("změněné pořadí");
            }
            if (problems.Count > 0)
            {
                throw new ApiException(400, rule + " (" + string.Join("; ", problems) + ")", "components_changed");
            }
            return missing;
        }

        /// <summary>
        /// The stored attachment images of <paramref name="blocks"/> (top-level index and block) that the images of
        /// <paramref name="parsed"/> take back: the same Markdown first, then the same address (<see cref="Image"/>).
        /// </summary>
        public void KeepImages(IEnumerable<(int Index, JsonObject Block)> blocks, JsonArray parsed)
        {
            var stored = new List<(JsonObject Node, string Key, string Src)>();
            foreach (var (index, block) in blocks)
            {
                Walk(block, node =>
                {
                    if (RichDocument.TypeOf(node) == "image" && ImageKey(node) is { } key)
                    {
                        if (!m_images.TryGetValue(key, out var queue))
                        {
                            m_images[key] = queue = new Queue<JsonObject>();
                        }
                        queue.Enqueue(node);
                        var src = TextOf(node["attrs"]!["src"])!;
                        if (!m_sources.TryGetValue(src, out var list))
                        {
                            m_sources[src] = list = [];
                        }
                        list.Add((node, index));
                        stored.Add((node, key, src));
                    }
                });
            }
            Walk(parsed, node =>
            {
                if (RichDocument.TypeOf(node) == "image" && ImageKey(node) is { } key)
                {
                    m_wanted.Add(key);
                }
            });
            foreach (var (node, key, src) in stored)
            {
                if (!m_free.TryGetValue(src, out var free))
                {
                    m_free[src] = free = (new Queue<JsonObject>(), new Queue<JsonObject>());
                }
                (m_wanted.Contains(key) ? free.Wanted : free.Unwanted).Enqueue(node);
            }
        }

        /// <summary>Parsed images that got the editor's default node so far.</summary>
        public int DefaultedImages { get; private set; }

        /// <summary>
        /// Stored images with a size or look that no parsed image took back. Where a parsed image got the editor's default node in the
        /// same place (the caller's gap), the picture was replaced in Markdown and its look has nowhere to go; an image removed without
        /// a replacement loses nothing.
        /// </summary>
        public IEnumerable<(int Index, JsonObject Image)> LostImages()
        {
            return m_sources.Values.SelectMany(x => x).Where(x => !m_taken.Contains(x.Node)
                && new[] { "width", "height", "presentation" }.Any(key => x.Node["attrs"]?[key] is not null)).Select(x => (x.Block, x.Node));
        }

        /// <summary>
        /// A parsed image: the stored node of an unchanged one; else the stored node of the same address whose own Markdown no parsed
        /// image repeats (preferred) with the new alternative text and title, keeping size and look (D-H1); otherwise the editor's
        /// default node.
        /// </summary>
        private JsonObject Image(JsonObject image)
        {
            if (ImageKey(image) is not { } key)
            {
                return image;
            }
            if (m_images.TryGetValue(key, out var queue))
            {
                while (queue.TryDequeue(out var same))
                {
                    if (m_taken.Add(same))
                    {
                        return (JsonObject)same.DeepClone();
                    }
                }
            }
            if (!m_free.TryGetValue(TextOf(image["attrs"]!["src"])!, out var free) || (Take(free.Unwanted) ?? Take(free.Wanted)) is not { } stored)
            {
                DefaultedImages++;
                return image;
            }
            var kept = (JsonObject)stored.DeepClone();
            kept["attrs"]!["alt"] = image["attrs"]!["alt"]?.DeepClone();
            kept["attrs"]!["title"] = image["attrs"]!["title"]?.DeepClone();
            return kept;
        }

        /// <summary>The first stored image of <paramref name="queue"/> not taken yet, now taken; null when none is left.</summary>
        private JsonObject? Take(Queue<JsonObject> queue)
        {
            while (queue.TryDequeue(out var node))
            {
                if (m_taken.Add(node))
                {
                    return node;
                }
            }
            return null;
        }

        /// <summary>An attachment image as Markdown sees it (<see cref="MarkdownToRich.ImageNode"/>); null for any other image.</summary>
        private static string? ImageKey(JsonObject image)
        {
            var attrs = image["attrs"];
            return TextOf(attrs?["src"]) is { } src && MarkdownToRich.AttachmentImageSrc(src) == src
                ? new JsonArray(src, TextOf(attrs?["alt"]) ?? "", TextOf(attrs?["title"]) ?? "").ToJsonString()
                : null;
        }

        private static string? TextOf(JsonNode? value)
        {
            return value is JsonValue item && item.TryGetValue<string>(out var text) ? text : null;
        }

        public JsonObject Convert(JsonNode? block)
        {
            if (block is not JsonObject value)
            {
                throw new ApiException(400, "Markdown se nepodařilo převést.", "invalid_markdown");
            }
            if (IsBlockSlot(value) is { } slot)
            {
                return (JsonObject)bindings[slot].Original.DeepClone();
            }
            var result = (JsonObject)value.DeepClone();
            if (RichDocument.TypeOf(result) == "image")
            {
                return Image(result);
            }
            ConvertContent(result);
            return result;
        }

        private void ConvertContent(JsonObject node)
        {
            if (node["content"] is not JsonArray content)
            {
                return;
            }
            var converted = new List<JsonNode?>();
            foreach (var child in content.ToArray())
            {
                content.Remove(child);
                if (child is not JsonObject item)
                {
                    continue;
                }
                switch (RichDocument.TypeOf(item))
                {
                    case MarkdownToRich.SlotNodeType:
                        converted.Add(bindings[item["attrs"]!["index"]!.GetValue<int>()].Original.DeepClone());
                        break;
                    case MarkdownToRich.PageLinkNodeType:
                        converted.Add(PageLink(item));
                        break;
                    case "pageReference":
                        // A link of the dependency line (the parser makes no other): as a link, only to a page the actor may read or one the
                        // revision already held; otherwise it is left out.
                        if (ReadableReference(item))
                        {
                            converted.Add(item);
                        }
                        break;
                    case "text":
                        CheckLinks(item);
                        converted.Add(item);
                        break;
                    case "image":
                        converted.Add(Image(item));
                        break;
                    default:
                        ConvertContent(item);
                        converted.Add(item);
                        break;
                }
            }
            // A removed link can leave two text nodes with equal marks next to each other.
            node["content"] = RichDocument.MergeText(converted);
            if (RequirementHeadingMarkdown.IsDependencyLine(node))
            {
                // A link left out: the line keeps the others, one separator between each two; with none it is an empty paragraph.
                var links = converted.Where(x => RichDocument.TypeOf(x) == "pageReference").Select(x => x!.DeepClone()).ToList();
                node["content"] = new JsonArray(links.SelectMany((x, i) => i == 0 ? [x] : new JsonNode?[] { new JsonObject { ["type"] = "text", ["text"] = ", " }, x }).ToArray());
                if (links.Count == 0)
                {
                    node.Remove("attrs");
                    node.Remove("content");
                }
            }
        }

        private bool ReadableReference(JsonObject reference)
        {
            if (TextOf(reference["attrs"]?["url"]) is { } url && knownLinks.Contains(url))
            {
                return true;
            }
            if (!Guid.TryParse(TextOf(reference["attrs"]?["id"]), out var id))
            {
                return false;
            }
            if (!m_pages.TryGetValue(id, out var readable))
            {
                readable = context.PageExists(id);
                m_pages[id] = readable;
            }
            if (!readable)
            {
                Warn("Odkaz na stránku nebyl převeden: stránka není dostupná.");
            }
            return readable;
        }

        /// <summary><c>[[Název]]</c>: the editor's page reference for exactly one readable page, otherwise the text and a warning.</summary>
        private JsonObject PageLink(JsonObject node)
        {
            var title = node["attrs"]!["title"]!.GetValue<string>();
            if (!m_titles.TryGetValue(title, out var resolution))
            {
                resolution = context.ResolveTitle(title);
                m_titles[title] = resolution;
            }
            if (resolution.Match == PageLinkMatch.Unique)
            {
                var id = resolution.PageId.ToString();
                return new JsonObject
                {
                    ["type"] = "pageReference",
                    ["attrs"] = new JsonObject { ["id"] = id, ["title"] = resolution.Title, ["url"] = "#/pages/" + id },
                };
            }
            Warn("Odkaz [[" + title + "]] nebyl převeden: "
                + (resolution.Match == PageLinkMatch.Ambiguous ? "názvu odpovídá více stránek." : "stránka nebyla nalezena."));
            return new JsonObject { ["type"] = "text", ["text"] = "[[" + title + "]]" };
        }

        /// <summary>
        /// A link to an Atlas page stays only for a page the actor may read or a target the revision already held; otherwise
        /// it is text, with one warning that does not tell a missing page from an unreadable one.
        /// </summary>
        private void CheckLinks(JsonObject text)
        {
            if (text["marks"] is not JsonArray marks)
            {
                return;
            }
            foreach (var mark in marks.OfType<JsonObject>().ToArray())
            {
                if (RichDocument.TypeOf(mark) != "link" || mark["attrs"]?["href"] is not JsonValue value || !value.TryGetValue<string>(out var href)
                    || PageHref().Match(href) is not { Success: true } page || knownLinks.Contains(href))
                {
                    continue;
                }
                var id = Guid.Parse(page.Groups["id"].Value);
                if (!m_pages.TryGetValue(id, out var readable))
                {
                    readable = context.PageExists(id);
                    m_pages[id] = readable;
                }
                if (!readable)
                {
                    marks.Remove(mark);
                    Warn("Odkaz na stránku nebyl převeden: stránka není dostupná.");
                }
            }
            if (marks.Count == 0)
            {
                text.Remove("marks");
            }
        }

        public void Warn(string warning)
        {
            if (!m_warnings.Contains(warning))
            {
                m_warnings.Add(warning);
            }
        }

        private static bool IsInline(SlotBinding binding)
        {
            // A block placeholder replaces a top-level block (/content/N); anything deeper is inline.
            return binding.Path.Count(x => x == '/') > 2;
        }

        private static string Names(IEnumerable<int> indices)
        {
            return Listed(indices.Select(MarkdownToRich.Placeholder));
        }
    }
}
