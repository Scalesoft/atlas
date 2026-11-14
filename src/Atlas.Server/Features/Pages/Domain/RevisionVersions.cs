using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Domain;

/// <summary>
/// 3.6-C: how saved revisions group into the versions of the page history („Pravidla slučování“ in the 3.6 plan). Revisions stay
/// immutable (O2): a version is the revisions sharing a <see cref="PageRevision.GroupId"/>, and its newest one carries what the
/// version changed (<see cref="RevisionChanges"/>) against the newest revision of the version before it.
/// </summary>
public static partial class RevisionVersions
{
    /// <summary>Rule 2: a pause this long ends the version.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(30);
    /// <summary>Rule 3: the longest version.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromHours(4);
    /// <summary>Rule 5: writes of the same AI source this close join its version.</summary>
    public static readonly TimeSpan SameAiSource = TimeSpan.FromMinutes(10);
    /// <summary>Rule 6: a large change rewrites more than half of the document and at least this many lines, so a short page that
    /// grows while typed does not break into a version per checkpoint.</summary>
    public const int LargeLines = 20;
    private const int MaxListed = 20;
    private const int MaxText = 200;
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>A change with AI: over MCP, the personal API or the assistant, or one its author marked as written with AI.</summary>
    public static bool IsAi(PageRevision revision) => IsAi(revision.Channel, revision.AiAssisted);

    public static bool IsAi(string channel, bool aiAssisted) => AiChannels.Contains(channel) || aiAssisted;

    /// <summary>The channels whose every change is with AI (<see cref="IsAi(string, bool)"/>), for the queries that decide it in SQL.</summary>
    public static readonly string[] AiChannels = [ContentChannels.Mcp, ContentChannels.Api, ContentChannels.Assistant];

    /// <summary>Whether <paramref name="next"/>, saved right after <paramref name="previous"/>, continues its version by rules 2–5 and 7.
    /// Explicit actions, a publication and a large change (rules 1 and 6) are decided by <see cref="PlaceAsync"/>.</summary>
    public static bool Continues(PageRevision previous, PageRevision next) => CloseReason(previous, next) is null;

    /// <summary>Why <paramref name="next"/> does not continue the version of <paramref name="previous"/> (<see cref="VersionCloseReasons"/>);
    /// null when it does.</summary>
    public static string? CloseReason(PageRevision previous, PageRevision next)
    {
        var pause = next.ActiveFrom - previous.ActiveFrom;
        return pause > Pause ? VersionCloseReasons.Pause
            : next.ActiveFrom - previous.GroupStartedAt > Longest ? VersionCloseReasons.Longest
            : Day(previous.GroupStartedAt) != Day(next.ActiveFrom) ? VersionCloseReasons.Day
            : IsAi(previous) != IsAi(next) || previous.Channel != next.Channel ? VersionCloseReasons.AiBoundary
            // People writing at once share one version (rule 7); a change with AI joins only its own source (rule 5).
            : !IsAi(next) || pause <= SameAiSource && previous.AuthorId == next.AuthorId && previous.ApiTokenId == next.ApiTokenId
                && previous.AiConversationId == next.AiConversationId && previous.AiAssisted == next.AiAssisted ? null
            : VersionCloseReasons.AiBoundary;
    }

    /// <summary>
    /// Places <paramref name="next"/>, saved after <paramref name="previous"/> (null for the page's first revision), into a version and
    /// stores what that version changed. <paramref name="explicitAction"/>: a restore or a source replacement, always a version of its
    /// own (rule 1), as is a version after a publication (sealed) or an import. Call before the revision is saved.
    /// A version that closes is announced (<see cref="VersionClosed"/>) through <paramref name="outbox"/>. <paramref name="coauthors"/>: the
    /// people whose live updates a checkpoint of the live document holds besides its author's (CollaborationCheckpoints.UnsavedAuthorsAsync).
    /// </summary>
    public static async Task PlaceAsync(ISession session, Outbox outbox, Page page, PageRevision? previous, PageRevision next, bool explicitAction,
        CancellationToken ct, IEnumerable<Guid>? coauthors = null)
    {
        var sealedPrevious = previous is not null && (previous.Channel == ContentChannels.Import || previous.Id == page.PublishedRevisionId);
        // An older revision's Markdown as today's writer reads it (RevisionMarkdown): a dialect change is no change.
        var previousMarkdown = previous is null ? "" : RevisionMarkdown.Of(previous);
        next.IsExplicit = explicitAction || IsLarge(previousMarkdown, next.Markdown);
        var reason = previous is null ? null : CloseReason(previous, next);
        var contributors = new List<RevisionContributor>();
        var basis = previous is null ? null : new Snapshot(previous.Title, previous.ContentJson, previousMarkdown);
        Announce(outbox, page.Id, previous, next, previous is null || previous.IsExplicit || reason == VersionCloseReasons.Pause ? null
            : sealedPrevious ? VersionCloseReasons.Published : next.IsExplicit ? VersionCloseReasons.Explicit : reason);
        if (previous is not null && !previous.IsExplicit && !sealedPrevious && !next.IsExplicit && reason is null)
        {
            next.GroupId = previous!.GroupId;
            next.GroupStartedAt = previous.GroupStartedAt;
            next.CheckpointCount = previous.CheckpointCount + 1;
            next.BaseRevisionId = previous.BaseRevisionId;
            contributors.AddRange(Read(previous.ChangeSummary)?.Contributors ?? [new RevisionContributor(previous.AuthorId, previous.CheckpointCount)]);
            // The version's changes are measured from where it started, not summed per checkpoint: retyping a sentence ten times is one change.
            // A save loaded it with the current revision (SavePageHandler); another writer reads it here.
            basis = previous.BaseRevisionId is { } id && await session.GetAsync<PageRevision>(id, ct) is { } start
                ? new Snapshot(start.Title, start.ContentJson, RevisionMarkdown.Of(start))
                : null;
        }
        else
        {
            next.GroupId = next.Id;
            next.GroupStartedAt = next.ActiveFrom;
            next.CheckpointCount = 1;
            next.BaseRevisionId = previous?.Id;
        }
        // The saver and everyone whose live updates the checkpoint folds in, once each: the saver of a live page is only who typed last.
        foreach (var author in (coauthors ?? []).Prepend(next.AuthorId).Distinct())
        {
            var own = contributors.FindIndex(x => x.Id == author);
            if (own < 0)
            {
                contributors.Add(new RevisionContributor(author, 1));
            }
            else
            {
                contributors[own] = contributors[own] with { Checkpoints = contributors[own].Checkpoints + 1 };
            }
        }
        var changes = Summarize(basis, new Snapshot(next.Title, next.ContentJson, next.Markdown),
            previous is not null && previous.Id != next.BaseRevisionId ? previous.ContentJson : null);
        next.ChangeSummary = JsonSerializer.Serialize(changes with { Contributors = contributors }, Json);
    }

    /// <summary>
    /// The versions <paramref name="next"/> closes: the previous one for <paramref name="closing"/> (null when it continues, or was announced
    /// already: an explicit revision when it was saved, a pause by <see cref="VersionPauseCheck"/>), and an explicit revision's own at once.
    /// Otherwise a check after <see cref="Pause"/> tells whether anything followed. Nothing while no consumer takes the event.
    /// </summary>
    private static void Announce(Outbox outbox, Guid pageId, PageRevision? previous, PageRevision next, string? closing)
    {
        if (!outbox.IsConsumed<VersionClosed>())
        {
            return;
        }
        var key = pageId.ToString();
        if (closing is not null)
        {
            outbox.Enqueue(new VersionClosed(pageId, previous!.Id, closing), key);
        }
        if (next.IsExplicit)
        {
            outbox.Enqueue(new VersionClosed(pageId, next.Id, VersionCloseReasons.Explicit), key);
        }
        else
        {
            outbox.Enqueue(new VersionPauseCheck(pageId, next.Id), key, Pause);
        }
    }

    /// <summary>The stored changes of a revision; null for one from before 3.6.</summary>
    public static RevisionChanges? Read(string? json) => json is null ? null : JsonSerializer.Deserialize<RevisionChanges>(json, Json);

    public sealed record Snapshot(string Title, string ContentJson, string Markdown);

    /// <summary>Rule 6, from the Markdown alone: the write changed at least <see cref="LargeLines"/> lines and more than half of the page.</summary>
    public static bool IsLarge(string before, string after)
    {
        var oldLines = before.Split('\n');
        var newLines = after.Split('\n');
        var (added, removed) = Diff(oldLines, newLines, string.IsNullOrWhiteSpace);
        var changed = Math.Max(added.Count, removed.Count);
        return changed >= LargeLines && changed * 2 > Math.Max(oldLines.Count(Text), newLines.Count(Text));

        static bool Text(string line) => !string.IsNullOrWhiteSpace(line);
    }

    /// <summary>What <paramref name="after"/> changed against <paramref name="before"/> (null: an empty page); the requirements it touched
    /// against <paramref name="previousContentJson"/>, the revision right before it when that is not <paramref name="before"/>.</summary>
    public static RevisionChanges Summarize(Snapshot? before, Snapshot after, string? previousContentJson = null)
    {
        var oldLines = (before?.Markdown ?? "").Split('\n');
        var newLines = after.Markdown.Split('\n');
        var (added, removed) = Diff(oldLines, newLines, string.IsNullOrWhiteSpace);
        var oldHeadings = Headings(oldLines);
        var newHeadings = Headings(newLines);
        var oldContent = Content.Of(before?.ContentJson);
        var newContent = Content.Of(after.ContentJson);
        var (addedBlocks, removedBlocks) = Diff(oldContent.Blocks, newContent.Blocks, _ => false);
        // A change no line shows (a list reordered, bold, an attribute) names its section too: by the top-level block it changed.
        var sections = added.Select(i => newHeadings[i]).Concat(removed.Select(i => oldHeadings[i]))
            .Concat(addedBlocks.Select(i => newContent.Sections[i])).Concat(removedBlocks.Select(i => oldContent.Sections[i]))
            .OfType<string>().Distinct().Take(MaxListed).ToList();
        // Every state change, not MaxListed: a block's history (projector_get_requirement_history) reads them.
        var states = newContent.Requirements.Where(x => oldContent.Requirements.TryGetValue(x.Key, out var old) && old.State != x.Value.State)
            .Select(x => new RequirementStateChange(x.Value.Title, oldContent.Requirements[x.Key].State, x.Value.State, x.Key)).ToList();
        // A page without requirements is not parsed a third time.
        var last = previousContentJson is null ? oldContent
            : newContent.Requirements.Count > 0 && (previousContentJson.Contains("\"requirement\"", StringComparison.Ordinal)
                || previousContentJson.Contains("\"state\"", StringComparison.Ordinal)) ? Content.Of(previousContentJson)
            : Content.Of(null);
        var touched = newContent.Requirements.Where(x => !last.Requirements.TryGetValue(x.Key, out var old) || old.Fingerprint != x.Value.Fingerprint)
            .Select(x => x.Key).ToList();
        var oldAttachments = before is null ? new HashSet<Guid>() : AttachmentReferences.Extract(before.ContentJson);
        var newAttachments = AttachmentReferences.Extract(after.ContentJson);
        return new RevisionChanges
        {
            Added = added.Count,
            Removed = removed.Count,
            Blocks = Math.Max(addedBlocks.Count, removedBlocks.Count),
            Sections = sections,
            States = states,
            Touched = touched,
            RequirementsAdded = newContent.Requirements.Keys.Count(key => !oldContent.Requirements.ContainsKey(key)),
            RequirementsRemoved = oldContent.Requirements.Keys.Count(key => !newContent.Requirements.ContainsKey(key)),
            ContactsAdded = newContent.Contacts.Where(x => !oldContent.Contacts.ContainsKey(x.Key)).Select(x => x.Value).Take(MaxListed).ToList(),
            ContactsRemoved = oldContent.Contacts.Where(x => !newContent.Contacts.ContainsKey(x.Key)).Select(x => x.Value).Take(MaxListed).ToList(),
            AttachmentsAdded = newAttachments.Count(id => !oldAttachments.Contains(id)),
            AttachmentsRemoved = oldAttachments.Count(id => !newAttachments.Contains(id)),
            TitleBefore = before is not null && before.Title != after.Title ? before.Title : null
        };
    }

    private static DateOnly Day(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone));

    /// <summary>
    /// The positions of the items <paramref name="after"/> added and <paramref name="before"/> lost, as multisets past the common start and
    /// end: linear for any page size, and a moved item is no change. Items <paramref name="ignored"/> (blank lines) count as neither.
    /// </summary>
    private static (List<int> Added, List<int> Removed) Diff(IReadOnlyList<string> before, IReadOnlyList<string> after, Func<string, bool> ignored)
    {
        var start = 0;
        while (start < before.Count && start < after.Count && before[start] == after[start])
        {
            start++;
        }
        var end = 0;
        while (end < before.Count - start && end < after.Count - start && before[before.Count - 1 - end] == after[after.Count - 1 - end])
        {
            end++;
        }
        return (Unmatched(after, before), Unmatched(before, after));

        List<int> Unmatched(IReadOnlyList<string> items, IReadOnlyList<string> other)
        {
            var available = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = start; i < other.Count - end; i++)
            {
                available[other[i]] = available.GetValueOrDefault(other[i]) + 1;
            }
            var result = new List<int>();
            for (var i = start; i < items.Count - end; i++)
            {
                if (ignored(items[i]))
                {
                    continue;
                }
                if (available.GetValueOrDefault(items[i]) > 0)
                {
                    available[items[i]]--;
                }
                else
                {
                    result.Add(i);
                }
            }
            return result;
        }
    }

    /// <summary>For each Markdown line the heading of its section (null before the first one); lines in code fences are no headings.</summary>
    private static string?[] Headings(string[] lines)
    {
        var result = new string?[lines.Length];
        string? current = null;
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fenced = !fenced;
            }
            else if (!fenced && Heading().Match(lines[i]) is { Success: true } heading && heading.Groups[1].Value.Trim() is { Length: > 0 } text)
            {
                current = Clip(text);
            }
            result[i] = current;
        }
        return result;
    }

    [GeneratedRegex(@"^#{1,6}[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    private static string Clip(string text) => text.Length <= MaxText ? text : text[..MaxText];

    /// <summary>The parts of a stored document the summary compares: its top-level blocks with the heading of the section each is in (null
    /// before the first), requirements by block id and contacts.</summary>
    private sealed record Content(List<string> Blocks, List<string?> Sections, Dictionary<string, (string Title, string State, string Fingerprint)> Requirements,
        Dictionary<string, string> Contacts)
    {
        public static Content Of(string? contentJson)
        {
            var content = new Content([], [], new Dictionary<string, (string, string, string)>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal));
            if (contentJson is null)
            {
                return content;
            }
            // A revision stored before 3.6 keeps its default attributes, the next save does not: the same block is no change.
            contentJson = RichDocument.WithoutDefaultAttributes(contentJson);
            using var json = JsonDocument.Parse(contentJson, MarkdownDocumentValidator.JsonOptions);
            if (!json.RootElement.TryGetProperty("document", out var document) || !document.TryGetProperty("content", out var blocks)
                || blocks.ValueKind != JsonValueKind.Array)
            {
                return content;
            }
            string? section = null;
            foreach (var block in blocks.EnumerateArray())
            {
                content.Blocks.Add(block.GetRawText());
                if (block.ValueKind == JsonValueKind.Object && Text(block, "type") == "heading" && string.Concat(Texts(block)).Trim() is { Length: > 0 } heading)
                {
                    section = Clip(heading);
                }
                content.Sections.Add(section);
                content.Visit(block);
            }
            if (contentJson.Contains("\"state\"", StringComparison.Ordinal))
            {
                content.Headings(System.Text.Json.Nodes.JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)?["document"]?["content"]
                    as System.Text.Json.Nodes.JsonArray ?? []);
            }
            return content;
        }

        /// <summary>A requirement heading (shape 2026-11-04) by its headingId; its change is one of its heading, dependency line or facts line.</summary>
        private void Headings(System.Text.Json.Nodes.JsonArray page)
        {
            foreach (var range in RequirementHeadings.Ranges(page))
            {
                var heading = page[range.Heading]!;
                if (RequirementHeadings.HeadingId(heading) is { Length: > 0 } id)
                {
                    var title = RequirementText.Title(new System.Text.Json.Nodes.JsonObject { ["content"] = new System.Text.Json.Nodes.JsonArray(
                        new System.Text.Json.Nodes.JsonObject { ["type"] = "paragraph", ["content"] = heading["content"]?.DeepClone() }) });
                    Requirements[id] = (Clip(title), RequirementText.Value(heading, "state"), heading.ToJsonString() + "\u001f"
                        + (range.Dependencies is { } d ? page[d]!.ToJsonString() : "") + "\u001f" + (range.Facts is { } f ? page[f]!.ToJsonString() : ""));
                }
            }
        }

        private void Visit(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            var type = node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var attrs = node.TryGetProperty("attrs", out var a) ? a : default;
            if (type == "requirement" && attrs.ValueKind == JsonValueKind.Object && Text(attrs, "id") is { Length: > 0 } id)
            {
                // What the chip counts as a change of the block: its attributes and its title, not its notes (2026103160's fingerprint).
                var title = node.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0
                    ? parts[0].GetRawText() : "";
                Requirements[id] = (Clip(string.Concat(Texts(node)).Trim()), Text(attrs, "state") is { Length: > 0 } state ? state : "planned",
                    attrs.GetRawText() + "\u001f" + title);
            }
            foreach (var contact in ContactSnapshots.Named(type, attrs))
            {
                var name = Text(contact, "name");
                var key = Text(contact, "objectId") is { Length: > 0 } objectId ? objectId : "name:" + name;
                if (name.Length > 0)
                {
                    Contacts.TryAdd(key, Clip(name));
                }
            }
            if (node.TryGetProperty("content", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    Visit(child);
                }
            }
        }

        private static IEnumerable<string> Texts(JsonElement node)
        {
            if (node.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                yield return text.GetString()!;
            }
            if (node.TryGetProperty("content", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in children.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).SelectMany(Texts))
                {
                    yield return part;
                }
            }
        }

        private static string Text(JsonElement value, string name)
            => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : "";
    }
}
