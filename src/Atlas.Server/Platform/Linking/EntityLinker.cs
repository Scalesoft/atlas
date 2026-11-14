using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Markdown.Sync.Recognition;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Platform.Linking;

/// <summary>A person, page or issue a reference may be: <paramref name="Id"/> is what <c>choice.targetId</c> names.</summary>
public sealed record ReferenceCandidate(Guid Id, string Label, string? Hint);

/// <summary>A chip a write made: its kind, the text it replaced, the entity and its name.</summary>
public sealed record LinkedReference(string Kind, string Text, Guid? TargetId, string Label, bool Created = false);

/// <summary>What a write left to a person: an unresolved chip or plain text the core knows, at <paramref name="Ordinal"/> of the
/// working revision when the write knows it.</summary>
public sealed record OfferedReference(string Kind, string Text, IReadOnlyList<ReferenceCandidate> Candidates, int? Ordinal = null);

public sealed record LinkWarning(string Type, string Text);

/// <summary>
/// The one report of a linking write (audit engine §6), in the MCP result and the /api/v1 answer alike: every chip made
/// (<c>linked</c>), the unresolved chips and the suggestions (inflected names, aliases, initials, page titles) it only offers.
/// </summary>
public sealed record LinkReport(IReadOnlyList<LinkedReference> Linked, IReadOnlyList<OfferedReference> Unresolved,
    IReadOnlyList<OfferedReference> Suggested, IReadOnlyList<LinkWarning> Warnings)
{
    public static readonly LinkReport Empty = new([], [], [], []);
}

/// <summary>
/// The server's linking (audit engine §6 <c>EntityLinker</c>): recognition over a document with the place's extraction lexicon (the same
/// rows and ordinals as <c>ContentReference</c>), what of it the actor may see (their own lexicon: an entity they cannot read is no
/// candidate and leaves no trace), and the chips made of it. A whole name of one visible person becomes a chip, of several an unresolved
/// chip; aliases, initials, inflected forms and page titles are only reported (D4).
/// </summary>
public sealed class EntityLinker(PlaceNames names, PlaceLexiconCache places)
{
    /// <summary>The report of the last <see cref="LinkWriteAsync"/> in this scope: the save that ran it is the caller's.</summary>
    public LinkReport Last { get; private set; } = LinkReport.Empty;

    public Task<PlaceLexicon> ExtractionLexiconAsync(Guid spaceId, CancellationToken ct) => places.GetAsync(spaceId, names, ct);

    public Task<PlaceLexicon> ActorLexiconAsync(Guid spaceId, UserAccount actor, CancellationToken ct) => names.ReadAsync(spaceId, actor, ct);

    /// <summary>The candidates of <paramref name="occurrence"/> the actor may see, best first: the most linked in the place, then by name.</summary>
    public static IReadOnlyList<ReferenceCandidate> Visible(ContentOccurrence occurrence, PlaceLexicon actor) => occurrence.Candidates
        .Select(key => actor.Entities.GetValueOrDefault(key)).OfType<PlaceEntity>()
        .OrderByDescending(entity => entity.Uses).ThenBy(entity => entity.Label, StringComparer.CurrentCulture).ThenBy(entity => entity.Hint, StringComparer.CurrentCulture)
        .Select(entity => new ReferenceCandidate(entity.Id, entity.Label, entity.Hint)).ToArray();

    /// <summary>What the core decides for the actor: a whole name of one visible person a chip, of several an unresolved chip, else an
    /// offer; nothing when the actor sees none of it (issues keep their key).</summary>
    public static RecognitionDecision Decision(ContentOccurrence occurrence, PlaceLexicon actor)
    {
        if (occurrence.Kind == "issue")
        {
            return RecognitionDecision.Suggest;
        }
        var visible = occurrence.Candidates.Count(actor.Entities.ContainsKey);
        if (visible == 0)
        {
            return RecognitionDecision.None;
        }
        if (occurrence.Decision is not (RecognitionDecision.Chip or RecognitionDecision.Unresolved) || actor.Lexicon.Truncated)
        {
            return RecognitionDecision.Suggest;
        }
        return visible == 1 ? RecognitionDecision.Chip : occurrence.Kind == "person" ? RecognitionDecision.Unresolved : RecognitionDecision.Suggest;
    }

    /// <summary>
    /// A Markdown write's linking (<c>link: "auto"</c>, the MCP and /api/v1 saves): recognition only in the top-level blocks that are not
    /// in <paramref name="previousJson"/> (what the write changed; untouched text and a text a person kept stay), the chips made there and
    /// the report in <see cref="Last"/>. Returns the document, or null when nothing changed.
    /// </summary>
    public async Task<string?> LinkWriteAsync(Guid spaceId, UserAccount actor, string? previousJson, string contentJson, CancellationToken ct)
    {
        Last = LinkReport.Empty;
        var root = JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!;
        if (root["document"]?["content"] is not JsonArray blocks || blocks.Count == 0)
        {
            return null;
        }
        var kept = new HashSet<string>(StringComparer.Ordinal);
        if (previousJson is not null && JsonNode.Parse(previousJson, documentOptions: MarkdownDocumentValidator.JsonOptions)?["document"]?["content"] is JsonArray before)
        {
            kept.UnionWith(before.Select(block => block?.ToJsonString() ?? ""));
        }
        var changed = blocks.Where(block => block is not null && !kept.Contains(block.ToJsonString())).ToArray();
        if (changed.Length == 0)
        {
            return null;
        }
        var extraction = await ExtractionLexiconAsync(spaceId, ct);
        var occurrences = changed.SelectMany(block => ContentReferences.Read(block, extraction)).ToArray();
        if (occurrences.Length == 0)
        {
            return null;
        }
        var actorLexicon = await ActorLexiconAsync(spaceId, actor, ct);
        var (linked, report) = Auto(occurrences, actorLexicon);
        Last = report;
        return linked ? ContentReferences.Write(root) : null;
    }

    /// <summary>The chips <c>link: "auto"</c> makes of <paramref name="occurrences"/> (applied in place, the last first) and the report.</summary>
    public static (bool Changed, LinkReport Report) Auto(IReadOnlyList<ContentOccurrence> occurrences, PlaceLexicon actor, bool ordinals = false)
    {
        var (linked, unresolved, suggested) = (new List<LinkedReference>(), new List<OfferedReference>(), new List<OfferedReference>());
        var changed = false;
        foreach (var occurrence in occurrences.Where(occurrence => occurrence.State != "linked").OrderByDescending(occurrence => occurrence.Ordinal))
        {
            var candidates = Visible(occurrence, actor);
            var ordinal = ordinals ? occurrence.Ordinal : (int?)null;
            if (occurrence.State == "unresolved")
            {
                unresolved.Add(new OfferedReference(occurrence.Kind, occurrence.Text, candidates, ordinal));
                continue;
            }
            switch (Decision(occurrence, actor))
            {
                case RecognitionDecision.Chip when occurrence.Place is not null:
                    var entity = actor.Entities[occurrence.Candidates.First(actor.Entities.ContainsKey)];
                    ContentReferences.Link(occurrence, entity.Kind == "page" ? ContentReferences.PageChip(entity) : ContentReferences.PersonChip(entity));
                    linked.Add(new LinkedReference(entity.Kind, occurrence.Text, entity.Id, entity.Label));
                    changed = true;
                    break;
                case RecognitionDecision.Unresolved when occurrence.Place is not null:
                    ContentReferences.Link(occurrence, ContentReferences.UnresolvedChip(occurrence.Text));
                    unresolved.Add(new OfferedReference(occurrence.Kind, occurrence.Text, candidates));
                    changed = true;
                    break;
                case RecognitionDecision.None:
                    break;
                default:
                    suggested.Add(new OfferedReference(occurrence.Kind, occurrence.Text, candidates, ordinal));
                    break;
            }
        }
        linked.Reverse();
        unresolved.Reverse();
        suggested.Reverse();
        return (changed, new LinkReport(linked, unresolved, suggested, []));
    }
}
