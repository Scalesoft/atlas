using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Content;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>
/// One live write of a page in progress (3.6-H), for the flow of its writer: <see cref="LivePageWrites"/> holds the page's document gate and
/// has checkpointed what people typed, so the writer's own transaction runs on a revision equal to the live document. Its save does not
/// reset the generation (<see cref="CollaborationCheckpoints.ResetAsync"/>): the new revision is noted here, and after the commit it
/// reaches every open editor as a collaborator's update.
/// </summary>
public sealed class LivePageWriteScope(Guid pageId, Guid generation, int readVersion, int startVersion, Guid startRevisionId, bool wholeDocument)
{
    private static readonly AsyncLocal<LivePageWriteScope?> s_current = new();

    public static LivePageWriteScope? Current => s_current.Value;

    public Guid PageId => pageId;
    public Guid Generation => generation;
    /// <summary>The version the agent read (its expectedVersion); below <see cref="StartVersion"/> when people saved since.</summary>
    public int ReadVersion => readVersion;
    public int StartVersion => startVersion;
    public Guid StartRevisionId => startRevisionId;
    public bool WholeDocument => wholeDocument;

    /// <summary>The revision the write saved last (its id and content), or null while it saved none.</summary>
    public (Guid Id, string ContentJson)? Written { get; set; }

    public static IDisposable Use(LivePageWriteScope scope)
    {
        var previous = s_current.Value;
        s_current.Value = scope;
        return new Restore(previous);
    }

    private sealed class Restore(LivePageWriteScope? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }
}

/// <summary>
/// Concurrency of a live write per target, not per page (3.6-H): typing elsewhere never refuses it. The blocks the write changes in the
/// current document (between the first and the last block that differ) must all read the same, at the same place, in the revision the agent read; a
/// whole-document write (atlas_replace_page, PUT /api/v1/pages/{id}) needs the whole document unchanged since that read.
/// </summary>
public static class LiveWriteTarget
{
    public static async Task RequireAsync(ISession session, LivePageWriteScope live, string currentJson, string targetJson, CancellationToken ct)
    {
        if (live.ReadVersion >= live.StartVersion)
        {
            return;
        }
        var read = await session.QueryOver<PageRevision>().Where(x => x.PageId == live.PageId && x.Version == live.ReadVersion)
            .Select(x => x.ContentJson).SingleOrDefaultAsync<string>(ct);
        if (read is null || !Unchanged(read, currentJson, targetJson, live.WholeDocument))
        {
            throw Changed(live.WholeDocument);
        }
    }

    /// <summary>The refusal of a write whose target changed since the agent's read: it reads again and repeats the change.</summary>
    public static ApiException Changed(bool wholeDocument) => new(409, wholeDocument
            ? "Stránku mezitím upravil někdo jiný. Načtěte ji znovu a změnu zopakujte nad aktuálním obsahem."
            : "Místo, které měníte, mezitím upravil někdo jiný. Načtěte stránku znovu a změnu zopakujte.",
        "target_changed");

    public static bool Unchanged(string readJson, string currentJson, string targetJson, bool wholeDocument)
    {
        var read = Blocks(readJson);
        var current = Blocks(currentJson);
        if (wholeDocument)
        {
            return read.SequenceEqual(current);
        }
        var target = Blocks(targetJson);
        var start = 0;
        while (start < current.Count && start < target.Count && current[start] == target[start]) start++;
        var end = 0;
        while (end < current.Count - start && end < target.Count - start && current[^(end + 1)] == target[^(end + 1)]) end++;
        var changed = current.Skip(start).Take(current.Count - start - end).ToList();
        if (changed.Count == 0)
        {
            return true; // a pure insertion touches nothing
        }
        // The same blocks at the same place, not the same text anywhere in the read: aligning the read with the current document (longest
        // common subsequence of blocks, a heading's id part of it) explains more with the changed blocks unchanged at one place of the read
        // than with them changed. A tie is ambiguous (identical blocks elsewhere): a changed target.
        var codes = new Dictionary<string, int>();
        int[] Code(IEnumerable<string> blocks) => [.. blocks.Select(block => codes.TryGetValue(block, out var code) ? code : codes[block] = codes.Count)];
        var (readCodes, changedCodes) = (Code(read), Code(changed));
        var before = Aligned(readCodes, Code(current.Take(start)));
        var after = Aligned([.. readCodes.Reverse()], Code(current.Skip(current.Count - end).Reverse()));
        var asChanged = Enumerable.Range(0, read.Count + 1).Max(k => before[k] + after[read.Count - k]);
        var asUnchanged = Enumerable.Range(0, read.Count - changed.Count + 1)
            .Where(k => readCodes.AsSpan(k, changed.Count).SequenceEqual(changedCodes))
            .Select(k => before[k] + changed.Count + after[read.Count - k - changed.Count]).DefaultIfEmpty(-1).Max();
        return asUnchanged > asChanged;
    }

    /// <summary>For every k, the length of the longest common subsequence of read[..k] and <paramref name="blocks"/>.</summary>
    private static int[] Aligned(int[] read, int[] blocks)
    {
        var row = new int[blocks.Length + 1];
        var result = new int[read.Length + 1];
        for (var k = 1; k <= read.Length; k++)
        {
            var diagonal = 0;
            for (var j = 1; j <= blocks.Length; j++)
            {
                var above = row[j];
                row[j] = read[k - 1] == blocks[j - 1] ? diagonal + 1 : Math.Max(above, row[j - 1]);
                diagonal = above;
            }
            result[k] = row[blocks.Length];
        }
        return result;
    }

    /// <summary>
    /// The current document without the change one revision made (3.6-H, undo of an agent's write as a whole): the blocks the revision
    /// changed against <paramref name="beforeJson"/> (between the first and the last that differ) are found unchanged in the current
    /// document, exactly once, and put back as they were. Null when that part has changed since, or is not there once.
    /// </summary>
    public static string? Undo(string currentJson, string changedJson, string beforeJson)
    {
        var after = Blocks(changedJson);
        var before = Blocks(beforeJson);
        var start = 0;
        while (start < after.Count && start < before.Count && after[start] == before[start]) start++;
        var end = 0;
        while (end < after.Count - start && end < before.Count - start && after[^(end + 1)] == before[^(end + 1)]) end++;
        var changed = after.Skip(start).Take(after.Count - start - end).ToList();
        var current = Blocks(currentJson);
        int at;
        if (changed.Count > 0)
        {
            var found = Enumerable.Range(0, Math.Max(0, current.Count - changed.Count + 1))
                .Where(index => current.Skip(index).Take(changed.Count).SequenceEqual(changed)).Take(2).ToList();
            if (found.Count != 1) return null;
            at = found[0];
        }
        else
        {
            // The revision only removed blocks: they go back after the block that preceded them, or first.
            var anchors = start == 0 ? [-1] : current.Select((block, index) => (block, index)).Where(x => x.block == after[start - 1]).Select(x => x.index).ToList();
            if (anchors.Count != 1) return null;
            at = anchors[0] + 1;
        }
        var root = JsonNode.Parse(currentJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!;
        var content = root["document"]!["content"]!.AsArray();
        var restored = JsonNode.Parse(beforeJson, documentOptions: MarkdownDocumentValidator.JsonOptions)!["document"]!["content"]!.AsArray()
            .Skip(start).Take(before.Count - start - end).Select(block => block?.DeepClone()).ToList();
        for (var i = 0; i < changed.Count; i++) content.RemoveAt(at);
        for (var i = 0; i < restored.Count; i++) content.Insert(at + i, restored[i]);
        return root.ToJsonString(RichDocument.JsonWrite);
    }

    /// <summary>The top-level blocks of a stored document, each as canonical JSON (<see cref="Canonical"/>).</summary>
    private static List<string> Blocks(string contentJson)
    {
        var root = JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions);
        return root?["document"]?["content"] is JsonArray blocks ? blocks.Select(block => Canonical(block)?.ToJsonString() ?? "null").ToList() : [];
    }

    /// <summary>A block as the editor reads it, whatever wrote it: an attribute that is null or at its schema default (the editor's save
    /// writes them all, an API write only those it sets) is absent, and so are empty attributes; keys sorted.</summary>
    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Key == "attrs" && pair.Value is JsonObject attrs ? (JsonNode?)WithoutDefaults(value["type"] is JsonValue type && type.TryGetValue<string>(out var name) ? name : null, attrs) : pair.Value))
            .Where(pair => pair.Value is not null && !(pair.Key == "attrs" && pair.Value is JsonObject { Count: 0 }))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, Canonical(pair.Value)))),
        JsonArray items => new JsonArray([.. items.Select(Canonical)]),
        _ => node?.DeepClone(),
    };

    private static JsonObject WithoutDefaults(string? type, JsonObject attrs) => new(attrs
        .Where(attr => attr.Value is not null && !(type is not null && HostDocumentValidator.Instance.Schema.AttributeDefault(type, attr.Key) is { } fallback
            && JsonNode.DeepEquals(attr.Value, JsonNode.Parse(fallback.GetRawText()))))
        .Select(attr => KeyValuePair.Create(attr.Key, (JsonNode?)attr.Value!.DeepClone())));
}
