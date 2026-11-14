using Microsoft.Extensions.Caching.Memory;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Pages.Domain;

/// <summary>
/// 3.6: a revision's Markdown for history reads (the revision view, the +/− counts of a version) as today's writer projects its content
/// with the content migrations applied (<see cref="RichDocument.Parse"/>, LegacyDocumentRepair), not the Markdown stored in the dialect
/// of its day, so a dialect change shows as no change. The stored column stays for the current and published revisions (search, the Git
/// backup and MCP read it). Cached by revision id, checked against its content, which an import may rewrite in place.
/// </summary>
public static class RevisionMarkdown
{
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 64_000_000 });

    public static string Of(PageRevision revision)
    {
        var hash = revision.ContentJson.GetHashCode(StringComparison.Ordinal);
        if (Cache.TryGetValue(revision.Id, out (int Hash, string Markdown) cached) && cached.Hash == hash)
        {
            return cached.Markdown;
        }
        string markdown;
        try
        {
            markdown = EditorMarkdownWriter.Write(RichDocument.Parse(revision.ContentJson).Document);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not a rich document today's writer reads (or one it cannot repeat, e.g. heading level -1): what was stored is all there is.
            return revision.Markdown;
        }
        Remember(revision.Id, hash, markdown);
        return markdown;
    }

    /// <summary>A revision just saved with the Markdown the server projected from its content: the next save compares against it uncached otherwise.</summary>
    public static void Remember(PageRevision revision)
        => Remember(revision.Id, revision.ContentJson.GetHashCode(StringComparison.Ordinal), revision.Markdown);

    private static void Remember(Guid id, int hash, string markdown)
        => Cache.Set(id, (hash, markdown), new MemoryCacheEntryOptions { Size = Math.Max(1, markdown.Length), SlidingExpiration = TimeSpan.FromHours(1) });
}
