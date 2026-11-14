using System.Globalization;
using Atlas.Server.Features.Search;

namespace Atlas.Server.Features.Assistant.Sources;

/// <summary>
/// The one source type of 2.0 (AI-11): published pages, version 1. A reference names one chunk of the knowledge index:
/// <see cref="SourceReference.Id"/> is the page, <see cref="SourceReference.Revision"/> its published revision and
/// <see cref="SourceReference.Anchor"/> the chunk, <c>{generation:N}:{ordinal}</c> (two chunks of one section are two sources;
/// the section anchor for the view is <see cref="AuthorizedSource.HeadingId"/>). Its dependency columns keep the real FK
/// <c>(PageId, RevisionId)</c> (spec RAG §1: the extensibility never weakens the FK to the page revision).
/// </summary>
public sealed class PageSourceAdapter(AnswerSourceSql sql, SearchSql search, IContentAccess access, IAtlasLocks locks) : IKnowledgeSourceAdapter
{
    public const string SourceType = "page";
    public const int SourceVersion = 1;
    /// <summary>Tunable (step M): pages considered before their chunks are chosen, by default (AnswerEffort.Quick).</summary>
    public const int PagesConsidered = 8;
    /// <summary>Tunable (step M, O17): the similarity threshold of the vector candidates and of a page's further chunks.</summary>
    public const float MinSimilarity = 0.45f;

    public string Type => SourceType;
    public int Version => SourceVersion;

    public async Task<IReadOnlyList<AuthorizedSource>> RetrieveAsync(AnswerRetrievalRequest request, CancellationToken ct)
    {
        var (space, page) = request.Scope.Kind switch
        {
            AnswerScope.WikiKind => ((Guid?)null, (Guid?)null),
            AnswerScope.SpaceKind => (request.Scope.SpaceId ?? throw new ArgumentException("A space scope names its space.", nameof(request)), null),
            AnswerScope.PageKind => (request.Scope.SpaceId, request.Scope.PageId ?? throw new ArgumentException("A page scope names its page.", nameof(request))),
            _ => throw new ArgumentException("Unknown answer scope.", nameof(request)),
        };
        var text = AnswerQueryText.Create(request.Question);
        if (text is null || request.Take <= 0 || request.PerSource <= 0)
        {
            return [];
        }
        var rows = await sql.SourcesAsync(request.Actor.Id, request.Actor.SecurityStamp, space, page, text, request.Vector,
            request.Pages, request.Take, request.PerSource, MinSimilarity, ct);
        return rows.Select(row => new AuthorizedSource(
            new SourceReference(SourceType, SourceVersion, Format(row.PageId), Format(row.RevisionId), Anchor(row.GenerationId, row.Ordinal)),
            row.Title, row.SpaceName, row.SpaceId, row.HeadingTitle, row.Text, 1.0 / row.Rank,
            new ChunkLocator(row.GenerationId, row.PageId, row.RevisionId, row.Ordinal), row.HeadingId)).ToArray();
    }

    public async Task<IReadOnlyDictionary<SourceReference, string?>> LoadTextAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct)
    {
        var result = refs.Distinct().ToDictionary(reference => reference, _ => (string?)null);
        var chunks = result.Keys.Select(reference => (reference, chunk: Locate(reference))).Where(x => x.chunk is not null).ToArray();
        var texts = (await sql.ChunkTextsAsync(chunks.Select(x => x.chunk!).ToArray(), ct))
            .ToDictionary(row => new ChunkLocator(row.GenerationId, row.PageId, row.RevisionId, row.Ordinal), row => row.Text);
        foreach (var (reference, chunk) in chunks)
        {
            result[reference] = texts.GetValueOrDefault(chunk!);
        }
        return result;
    }

    /// <summary>
    /// Plan phase 3 R2 steps 1–3: the current spaces read without a lock; <see cref="IContentAccess.LockActorInSpacesAsync"/> (the
    /// shared content lock, the spaces FOR SHARE in id order, the account FOR SHARE with its active state and stamp: a revoked
    /// credential answers 401 as on every content path); the pages FOR SHARE in id order and their space checked again (a page
    /// moved in between is read once more, then refused); then ReadContent through IContentAccess, the trash, the publication,
    /// templates and <see cref="AccessSql.SpaceInAi"/>. The locks last until the transaction ends, so a revocation that starts after them
    /// waits for this transaction's commit, and one committed before them is seen. Excluding a space from AI takes Space FOR UPDATE
    /// (SaveSpaceAiSettingsHandler), so it follows the same ordering against this gate's Space FOR SHARE.
    /// </summary>
    public async Task<IReadOnlyDictionary<SourceReference, SourceStatus>> AuthorizeAsync(IReadOnlyList<SourceReference> refs,
        bool requireCurrentRevision, CancellationToken ct)
    {
        var result = refs.Distinct().ToDictionary(reference => reference, _ => SourceStatus.Unavailable);
        var parsed = result.Keys.Select(reference => (reference, page: Parse(reference))).Where(x => x.page is not null).ToArray();
        var pending = parsed.Select(x => x.page!.Value.PageId).Distinct().Order().ToList();
        if (pending.Count == 0)
        {
            return result;
        }
        var lockedSpaces = new HashSet<Guid>();
        var pages = new Dictionary<Guid, Page>();
        for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
        {
            var spaces = await sql.CurrentSpacesAsync(pending, ct);
            lockedSpaces.UnionWith(spaces.Values);
            await access.LockActorInSpacesAsync(lockedSpaces, ct);
            var moved = new List<Guid>();
            foreach (var pageId in pending.Where(spaces.ContainsKey))
            {
                var page = await locks.ReadPageSharedAsync(pageId, ct);
                if (page is null)
                {
                    continue;
                }
                if (lockedSpaces.Contains(page.SpaceId))
                {
                    pages[pageId] = page;
                }
                else
                {
                    moved.Add(pageId);
                }
            }
            pending = moved;
        }
        var candidates = pages.Values.Where(page => !page.IsTrashed && !page.IsTemplate && page.PublishedRevisionId is not null).OrderBy(page => page.Id).ToArray();
        var authorizations = candidates.Length == 0 ? [] : await access.AuthorizePagesAsync(candidates, AccessOperation.ReadContent, ct);
        var readable = candidates.Where((_, index) => authorizations[index].Allowed).ToArray();
        var excluded = await search.ExcludedFromAiAsync(readable.Select(page => page.SpaceId).ToHashSet(), ct);
        var published = readable.Where(page => !excluded.Contains(page.SpaceId)).ToDictionary(page => page.Id, page => page.PublishedRevisionId!.Value);
        foreach (var (reference, page) in parsed)
        {
            if (published.TryGetValue(page!.Value.PageId, out var current))
            {
                result[reference] = current == page.Value.RevisionId || !requireCurrentRevision ? SourceStatus.Available : SourceStatus.Changed;
            }
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<SourceReference, SourceStatus>> StatusAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct)
    {
        var result = refs.Distinct().ToDictionary(reference => reference, _ => SourceStatus.Unavailable);
        var parsed = result.Keys.Select(reference => (reference, page: Parse(reference))).Where(x => x.page is not null).ToArray();
        if (parsed.Length == 0)
        {
            return result;
        }
        // The readers' protocol: the shared content lock and the account FOR SHARE (a revoked credential answers 401).
        var actor = await access.LockActorAsync(null, ct);
        var rows = (await sql.StatusAsync(actor.Id, actor.SecurityStamp, parsed.Select(x => x.page!.Value).Distinct().ToArray(), ct))
            .ToDictionary(row => (row.PageId, row.RevisionId));
        foreach (var (reference, page) in parsed)
        {
            if (rows.TryGetValue(page!.Value, out var row) && row.Available)
            {
                result[reference] = row.Changed ? SourceStatus.Changed : SourceStatus.Available;
            }
        }
        return result;
    }

    /// <summary>The title of the page's current published revision, its space, and the heading of the referenced chunk only while
    /// the reference is that revision (a reader never sees a heading of an older one). Called after the status decided access.</summary>
    public async Task<IReadOnlyDictionary<SourceReference, SourceDescription?>> DescribeAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct)
    {
        var result = refs.Distinct().ToDictionary(reference => reference, _ => (SourceDescription?)null);
        // A whole page (an agent turn's source) is described through a chunk locator no chunk has: its title and space, no heading.
        var chunks = result.Keys.Select(reference => (reference, chunk: Locate(reference)
                ?? (reference.Anchor is null && Parse(reference) is { } page ? new ChunkLocator(Guid.Empty, page.PageId, page.RevisionId, 0) : null)))
            .Where(x => x.chunk is not null).ToArray();
        var rows = (await sql.DescribeAsync(chunks.Select(x => x.chunk!).Distinct().ToArray(), ct))
            .ToDictionary(row => new ChunkLocator(row.GenerationId, row.PageId, row.RevisionId, row.Ordinal));
        foreach (var (reference, chunk) in chunks)
        {
            if (rows.TryGetValue(chunk!, out var row))
            {
                result[reference] = new SourceDescription(row.Title, row.PageId, row.SpaceId, row.SpaceName, row.HeadingTitle);
            }
        }
        return result;
    }

    public SourceColumns Columns(AuthorizedSource source)
    {
        var page = Parse(source.Reference) ?? throw new ArgumentException("Not a page source of this adapter.", nameof(source));
        var chunk = source.Chunk ?? Locate(source.Reference);
        return new SourceColumns(SourceType, SourceVersion, page.PageId, page.RevisionId, null, null, chunk?.GenerationId, chunk?.Ordinal, source.HeadingId);
    }

    /// <summary>A chunk of a page revision, or the whole revision when the columns name no chunk (the source of an agent turn: the
    /// pages its tools returned, WP-14). A whole page has no text to load; access and publication decide as for a chunk.</summary>
    public SourceReference? Reference(SourceColumns columns)
    {
        if (columns.SourceType != SourceType || columns.SourceVersion != SourceVersion || columns is not { PageId: { } page, RevisionId: { } revision })
        {
            return null;
        }
        return columns switch
        {
            { GenerationId: { } generation, ChunkOrdinal: { } ordinal } => new SourceReference(SourceType, SourceVersion, Format(page), Format(revision), Anchor(generation, ordinal)),
            { GenerationId: null, ChunkOrdinal: null } => new SourceReference(SourceType, SourceVersion, Format(page), Format(revision), null),
            _ => null,
        };
    }

    private static string Format(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Anchor(Guid generation, int ordinal) => generation.ToString("N", CultureInfo.InvariantCulture) + ":" + ordinal.ToString(CultureInfo.InvariantCulture);

    private static (Guid PageId, Guid RevisionId)? Parse(SourceReference reference)
        => reference.Type == SourceType && reference.Version == SourceVersion
            && Guid.TryParse(reference.Id, out var page) && Guid.TryParse(reference.Revision, out var revision)
            ? (page, revision) : null;

    private static ChunkLocator? Locate(SourceReference reference)
    {
        if (Parse(reference) is not { } page || reference.Anchor?.Split(':') is not [var generation, var ordinal]
            || !Guid.TryParseExact(generation, "N", out var generationId)
            || !int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index > short.MaxValue)
        {
            return null;
        }
        return new ChunkLocator(generationId, page.PageId, page.RevisionId, index);
    }
}
