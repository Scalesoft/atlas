using System.Text.Json.Nodes;
using Atlas.Server.Features.Projector.Contracts;

namespace Atlas.Server.Features.Projector.Persistence;

/// <summary>
/// The definitions of published catalog pages without password references, the same for every reader: one entry per page (review
/// A2-2), with its revision and content hash (an import or a source replace changes a revision in place), so a new publication
/// replaces the old one, and the page's content is never kept. Bounded by an estimate of the bytes it holds: an entry that does not
/// fit pushes out others, one larger than the budget is not kept. Shared between requests, so read only: the insertion clones a
/// definition before it changes the copy.
/// </summary>
public sealed class ProjectorCatalogCache(long budget = ProjectorCatalogCache.DefaultBudget)
{
    public const long DefaultBudget = 64L << 20;

    private sealed record Entry(Guid RevisionId, string Hash, long Bytes, IReadOnlyList<CatalogDefinition> Definitions);

    private readonly Dictionary<Guid, Entry> m_entries = new();
    private long m_bytes;

    public int Count
    {
        get
        {
            lock (m_entries)
            {
                return m_entries.Count;
            }
        }
    }

    public long Bytes
    {
        get
        {
            lock (m_entries)
            {
                return m_bytes;
            }
        }
    }

    public IReadOnlyList<CatalogDefinition>? Find(Guid pageId, Guid revisionId, string hash)
    {
        lock (m_entries)
        {
            return m_entries.TryGetValue(pageId, out var entry) && entry.RevisionId == revisionId && entry.Hash == hash ? entry.Definitions : null;
        }
    }

    public void Add(Guid pageId, Guid revisionId, string hash, long bytes, IReadOnlyList<CatalogDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            Materialize(definition.ContentJson);
        }
        lock (m_entries)
        {
            if (m_entries.Remove(pageId, out var replaced))
            {
                m_bytes -= replaced.Bytes;
            }
            if (bytes > budget)
            {
                return;
            }
            // shortcut: evicts in the dictionary's order, not the least recently used; an LRU once catalogs outgrow the budget.
            foreach (var (other, entry) in m_entries.ToArray())
            {
                if (m_bytes + bytes <= budget)
                {
                    break;
                }
                m_entries.Remove(other);
                m_bytes -= entry.Bytes;
            }
            m_entries[pageId] = new Entry(revisionId, hash, bytes, definitions);
            m_bytes += bytes;
        }
    }

    /// <summary>A parsed JsonNode builds its children on first access, which is not safe from several threads: build them all now.</summary>
    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var (_, child) in value)
                {
                    Materialize(child);
                }
                break;
            case JsonArray value:
                foreach (var child in value)
                {
                    Materialize(child);
                }
                break;
        }
    }
}
