using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync.Recognition;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Content;
using Atlas.Server.Platform.Linking;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Linking.Queries;

/// <summary>GET /api/spaces/{id}/unlinked: the space's unresolved chips and suggestions, page by page (<paramref name="Cursor"/>).
/// <paramref name="PageId"/>: only that page's (the overview reloads one page after a conflict).</summary>
public sealed record ListUnlinked(Guid SpaceId, string? Cursor, int? Limit, Guid? PageId = null);

/// <summary>One occurrence a person (or an agent) may link: its page, its place (<paramref name="Ordinal"/> in the revision the reader
/// edits, the heading <paramref name="Anchor"/>), its text as written, the block's text around it as a reader sees it
/// (<paramref name="Context"/>, at most 160 characters, it contains <paramref name="Text"/>) and who or what it may be.
/// <paramref name="Version"/> is the page's version, the <c>expectedVersion</c> of a resolve.</summary>
public sealed record UnlinkedReference(Guid PageId, string PageTitle, int Ordinal, string? Anchor, string Text, string Kind, string State,
    IReadOnlyList<ReferenceCandidate> Candidates, int Version, string? Context);

public sealed record UnlinkedList(IReadOnlyList<UnlinkedReference> Items, string? NextCursor);

/// <summary>POST /api/v1/recognize: a text in a space, or a page (its revision the reader edits, else the published one).</summary>
public sealed record Recognize(string? Text, Guid? SpaceId, Guid? PageId);

/// <summary>A hit: in a page with its <paramref name="Ordinal"/> (for a resolve), in a text with its offsets. <paramref name="Decision"/>
/// what <c>link: "auto"</c> would do: <c>chip</c>, <c>unresolved</c> or <c>suggest</c>.</summary>
public sealed record RecognizedReference(int? Ordinal, string Kind, string Text, int? From, int? To, string State, string Decision, string? Anchor,
    IReadOnlyList<ReferenceCandidate> Candidates, string? Context = null);

public sealed record RecognizeResult(Guid? PageId, int? Version, IReadOnlyList<RecognizedReference> Hits);

public sealed class ListUnlinkedHandler(ISession session, IContentAccess access, PageListing listing, IRequestChannel channel, EntityLinker linker)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public async Task<UnlinkedList> Handle(ListUnlinked query, CancellationToken ct)
    {
        var limit = query.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaxLimit)
        {
            throw new ApiException(400, "limit je 1 až {maxLimit}.", "invalid_argument", args: new Dictionary<string, object?> { ["maxLimit"] = MaxLimit });
        }
        var actor = await access.LockActorAsync(query.SpaceId, ct);
        await listing.RequireSpaceAsync(actor, query.SpaceId, ct);
        await LinkingScope.RequireAsync(session, channel, query.SpaceId, LinkingScope.MissingSpace, ct);
        var (afterPage, afterOrdinal) = ReadCursor(query.Cursor);
        var lexicon = await linker.ActorLexiconAsync(query.SpaceId, actor, ct);
        var items = new List<UnlinkedReference>();
        var batch = Math.Max(limit * 2, 100);
        // Rows the reader sees none of are skipped, so a window may take several reads; each read is one index range.
        for (var round = 0; round < 20; round++)
        {
            var read = session.CreateSQLQuery($"""
                    SELECT r."SourceId", r."Ordinal", r."Kind", r."State", r."Text", r."Anchor", r."Context", array_to_string(r."Candidates", ',') AS "Candidates",
                        CASE WHEN e."Role" >= 2 THEN w."Title" ELSE pr."Title" END AS "Title", p."Version"
                    FROM "Page" p
                    JOIN "EffectivePageAccess" e ON e."PageId" = p."Id" AND e."UserId" = :actor AND e."Role" >= 1
                    JOIN "PageRevision" w ON w."PageId" = p."Id" AND w."ActiveTo" IS NULL
                    LEFT JOIN "PageRevision" pr ON pr."Id" = p."PublishedRevisionId"
                    JOIN "ContentReference" r ON r."SourceId" = p."Id"
                        AND r."SourcePart" = 'rev:' || CAST(CASE WHEN e."Role" >= 2 THEN w."Id" ELSE p."PublishedRevisionId" END AS text)
                    WHERE p."SpaceId" = :space AND NOT p."IsTrashed" AND NOT p."IsTemplate" AND r."State" <> 'linked' {(query.PageId is null ? "" : """AND p."Id" = :page""")}
                        AND (r."SourceId", r."Ordinal") > (:afterPage, :afterOrdinal)
                    ORDER BY r."SourceId", r."Ordinal"
                    LIMIT :batch
                    """)
                .AddScalar("SourceId", NHibernateUtil.Guid).AddScalar("Ordinal", NHibernateUtil.Int32).AddScalar("Kind", NHibernateUtil.String)
                .AddScalar("State", NHibernateUtil.String).AddScalar("Text", NHibernateUtil.String).AddScalar("Anchor", NHibernateUtil.String).AddScalar("Context", NHibernateUtil.String)
                .AddScalar("Candidates", NHibernateUtil.String).AddScalar("Title", NHibernateUtil.String).AddScalar("Version", NHibernateUtil.Int32)
                .SetParameter("actor", actor.Id).SetParameter("space", query.SpaceId).SetParameter("afterPage", afterPage)
                .SetParameter("afterOrdinal", afterOrdinal).SetParameter("batch", batch);
            // One page as its own condition, not an OR with a flag, so the plan of the space-wide read stays the index range.
            var rows = await (query.PageId is { } page ? read.SetParameter("page", page) : read).ListAsync<object[]>(ct);
            foreach (var row in rows)
            {
                (afterPage, afterOrdinal) = ((Guid)row[0], (int)row[1]);
                var occurrence = new ContentOccurrence
                {
                    Ordinal = (int)row[1], Kind = (string)row[2], State = (string)row[3], Text = (string?)row[4] ?? "",
                    Candidates = (row[7] as string)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [],
                };
                var candidates = EntityLinker.Visible(occurrence, lexicon);
                // A suggestion of nobody the reader may see is no suggestion; an unresolved chip waits for a pick whoever it may be.
                if (occurrence.State == "suggested" && occurrence.Kind != "issue" && candidates.Count == 0)
                {
                    continue;
                }
                items.Add(new UnlinkedReference(afterPage, (string?)row[8] ?? "", occurrence.Ordinal, (string?)row[5], occurrence.Text, occurrence.Kind,
                    occurrence.State, candidates, (int)row[9], (string?)row[6]));
                if (items.Count == limit)
                {
                    return new UnlinkedList(items, Cursor(afterPage, afterOrdinal));
                }
            }
            if (rows.Count < batch)
            {
                return new UnlinkedList(items, null);
            }
        }
        return new UnlinkedList(items, Cursor(afterPage, afterOrdinal));
    }

    private static string Cursor(Guid page, int ordinal) => $"{page:N}.{ordinal}";

    private static (Guid Page, int Ordinal) ReadCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (Guid.Empty, -1);
        }
        var parts = cursor.Split('.');
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var page) && int.TryParse(parts[1], out var ordinal) && ordinal >= 0
            ? (page, ordinal)
            : throw new ApiException(400, "Neplatný cursor. Použijte hodnotu nextCursor z předchozího výsledku beze změny.", "invalid_argument");
    }
}

public sealed class RecognizeHandler(ISession session, IContentAccess access, PageListing listing, PageGate gate, IRequestChannel channel, EntityLinker linker)
{
    /// <summary>The most text one call reads.</summary>
    public const int MaxText = 100_000;

    public async Task<RecognizeResult> Handle(Recognize query, CancellationToken ct)
    {
        if (query.PageId is { } pageId)
        {
            var opened = (await gate.ReadAsync(pageId, AccessOperation.ReadContent, ct)).Require();
            var page = opened.Page;
            await LinkingScope.RequireAsync(session, channel, page.SpaceId, LinkingScope.MissingPage, ct);
            // An editor reads the working revision, anyone else the publication.
            var revision = (opened.CanEdit
                ? await session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null).SingleOrDefaultAsync(ct)
                : page.PublishedRevisionId is { } published ? await session.GetAsync<PageRevision>(published, ct) : null)
                ?? throw new ApiException(404, LinkingScope.MissingPage);
            var extraction = await linker.ExtractionLexiconAsync(page.SpaceId, ct);
            var actor = await linker.ActorLexiconAsync(page.SpaceId, opened.Actor, ct);
            var hits = ContentReferences.Read(revision.ContentJson, extraction, pageId).Occurrences
                .Where(occurrence => occurrence.State != "linked")
                .Select(occurrence => (Occurrence: occurrence, Candidates: EntityLinker.Visible(occurrence, actor), Decision: EntityLinker.Decision(occurrence, actor)))
                .Where(hit => hit.Occurrence.State == "unresolved" || hit.Decision != RecognitionDecision.None)
                .Select(hit => new RecognizedReference(hit.Occurrence.Ordinal, hit.Occurrence.Kind, hit.Occurrence.Text, null, null, hit.Occurrence.State,
                    Name(hit.Occurrence.State == "unresolved" ? RecognitionDecision.Unresolved : hit.Decision), hit.Occurrence.Anchor, hit.Candidates, hit.Occurrence.Context))
                .ToArray();
            return new RecognizeResult(pageId, page.Version, hits);
        }
        if (query.SpaceId is not { } spaceId)
        {
            throw new ApiException(400, "Zadejte pageId, nebo spaceId s textem.", "invalid_argument");
        }
        var reader = await access.LockActorAsync(spaceId, ct);
        await listing.RequireSpaceAsync(reader, spaceId, ct);
        await LinkingScope.RequireAsync(session, channel, spaceId, LinkingScope.MissingSpace, ct);
        if (string.IsNullOrWhiteSpace(query.Text) || query.Text.Length > MaxText)
        {
            throw new ApiException(400, "text je povinný, nejvýše {maxText} znaků.", "invalid_argument", args: new Dictionary<string, object?> { ["maxText"] = MaxText });
        }
        var lexicon = await linker.ExtractionLexiconAsync(spaceId, ct);
        var own = await linker.ActorLexiconAsync(spaceId, reader, ct);
        var found = lexicon.Index.RecognizeBlock(query.Text, lexicon.Lexicon.YouTrackProjects, ContentReferences.MaxRows)
            .Select(hit => (Hit: hit, Occurrence: new ContentOccurrence
            {
                Ordinal = 0, Kind = hit.Kind, State = "suggested", Text = hit.Text, Decision = hit.Decision,
                Candidates = hit.Kind == "issue" ? ["issue:" + hit.Text.TrimStart('#')] : hit.Candidates.Select(PlaceLexicon.KeyOf).ToArray(),
            }))
            .Select(x => (x.Hit, x.Occurrence, Decision: EntityLinker.Decision(x.Occurrence, own)))
            .Where(x => x.Decision != RecognitionDecision.None)
            .Select(x => new RecognizedReference(null, x.Hit.Kind, x.Hit.Text, x.Hit.From, x.Hit.To, "suggested", Name(x.Decision), null, EntityLinker.Visible(x.Occurrence, own)))
            .ToArray();
        return new RecognizeResult(null, null, found);
    }

    private static string Name(RecognitionDecision decision) => decision.ToString().ToLowerInvariant();
}
