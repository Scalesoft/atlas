using System.Text.Json;
using NHibernate;
using NHibernate.Criterion;
using Atlas.Server.Features.Collaboration.Contracts;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>Business writes fencing the live relay state. The caller owns the page row lock and the transaction; projections are producer-validated envelopes.</summary>
public sealed class CollaborationCheckpoints
{
    private readonly ISession m_session;

    public CollaborationCheckpoints(ISession session)
    {
        m_session = session;
    }

    /// <param name="revisionId">The page's current revision: the live document must hold it (be based on it) to be saved as the next one.</param>
    public async Task<CollaborationCheckpoint> ValidateCheckpointAsync(Page page, Guid generation, Guid revisionId, long? sequence, CancellationToken ct)
    {
        var state = await LiveDocument.LockAsync(m_session, page.Id, generation, ct);
        if (sequence is { } expected && expected != state.Sequence)
        {
            throw CollaborationErrors.Stale();
        }
        if (state.StateSha256 is null || state.SnapshotSequence != state.Sequence)
        {
            // The newest snapshot was refused as page content: the reason, not a wait the relay will never end (D-M3).
            if (state.RefusedSequence == state.Sequence && state.Refusal is { } refusal)
            {
                throw Refused(refusal);
            }
            throw new ApiException(409, "Společné změny se ještě ukládají.", "collaboration_pending");
        }
        // A live write's revision whose update is not in the log yet (LivePageWrites, between its commit and its append; after a crash
        // there, until OpenCollaboration replaces the generation): saving the live document now would drop that revision's change.
        if (state.BaseRevisionId != revisionId)
        {
            throw new ApiException(409, "Společné změny se ještě ukládají.", "collaboration_pending");
        }
        return new CollaborationCheckpoint(state.Sequence, state.ProjectionJson!);
    }

    /// <summary>True when <paramref name="revisionId"/> is where the live generation stands: a checkpoint of it (or its base), so its content is in the live document.</summary>
    public async Task<bool> IsCheckpointAsync(Guid pageId, Guid generation, Guid revisionId, CancellationToken ct)
    {
        return await m_session.QueryOver<PageCollaboration>()
            .Where(x => x.PageId == pageId && x.Generation == generation && x.BaseRevisionId == revisionId)
            .RowCountAsync(ct) > 0;
    }

    /// <summary>Who wrote the unsaved updates of the generation that a checkpoint validated by <see cref="ValidateCheckpointAsync"/> saves (its
    /// lock holds the head): every one of them is an author of that revision, not only the one who saves it. An agent's update counts as its
    /// token's user. Shortcut: a tail pruned past the relay's window (CollaborationLog) is not credited; a checkpoint every 30 s keeps it short.</summary>
    public async Task<IList<Guid>> UnsavedAuthorsAsync(Guid pageId, Guid generation, CancellationToken ct)
    {
        var state = (await m_session.GetAsync<PageCollaboration>(pageId, ct))!;
        var (saved, head) = (state.LastSavedSequence ?? 0, state.Sequence);
        if (saved >= head)
        {
            return [];
        }
        return await m_session.QueryOver<CollaborationUpdate>()
            .Where(x => x.PageId == pageId && x.Generation == generation && x.Sequence > saved && x.Sequence <= head)
            .Select(Projections.Distinct(Projections.Property<CollaborationUpdate>(x => x.UserId)))
            .ListAsync<Guid>(ct);
    }

    public async Task AdvanceCheckpointAsync(Page page, PageRevision revision, CancellationToken ct, bool markSaved = false)
    {
        if (revision.PageId != page.Id)
        {
            throw new ArgumentException("Revision belongs to another page.");
        }
        var state = await m_session.GetAsync<PageCollaboration>(page.Id, LockMode.Upgrade, ct) ?? throw CollaborationErrors.Stale();
        state.BaseRevisionId = revision.Id;
        if (markSaved)
        {
            state.LastSavedSequence = state.Sequence;
        }
        state.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Guards a write that resets the generation without carrying one (personal API save, history restore, source replace, trash):
    /// the reset deletes the log, so it must never discard what other users typed. The caller holds the page row lock, which
    /// excludes every append and checkpoint writer. Unsaved updates of the current generation that all come from the actor
    /// are superseded by the write; any other author, or a tail that was already pruned and cannot be attributed, refuses.
    /// The message never names who is editing; a caller over several pages may say so in its own words. Snapshot bodies are never loaded.
    /// With <paramref name="includeOwn"/> (the mcp channel, D3) any unsaved update refuses, the actor's own included: an agent acting
    /// for the token owner must never silently discard what the owner is typing in a browser.
    /// </summary>
    public async Task RequireNoForeignUnsavedAsync(Guid pageId, Guid actorId, CancellationToken ct, string? message = null, bool includeOwn = false)
    {
        var live = await m_session.QueryOver<PageCollaboration>()
            .Where(x => x.PageId == pageId)
            .Select(x => x.Generation, x => x.Sequence, x => x.LastSavedSequence)
            .SingleOrDefaultAsync<object[]>(ct);
        if (live is null)
        {
            return;
        }
        var generation = (Guid)live[0];
        var sequence = (long)live[1];
        var saved = (long?)live[2] ?? 0;
        if (saved >= sequence)
        {
            return;
        }
        if (includeOwn)
        {
            throw new ApiException(409, message ?? UnsavedMessage, "collaboration_unsaved");
        }
        var own = await m_session.QueryOver<CollaborationUpdate>()
            .Where(x => x.PageId == pageId && x.Generation == generation && x.Sequence > saved && x.UserId == actorId)
            .RowCountInt64Async(ct);
        if (own != sequence - saved)
        {
            throw new ApiException(409, message ?? "Na stránce jsou neuložené živé úpravy jiných uživatelů. Počkejte, až je uloží, a zkuste to znovu.", "collaboration_unsaved");
        }
    }

    /// <summary><see cref="RequireNoForeignUnsavedAsync(Guid, Guid, CancellationToken, string?, bool)"/> over many pages in one statement,
    /// with one more only for a page that holds unsaved edits at all.</summary>
    public async Task RequireNoForeignUnsavedAsync(IReadOnlyCollection<Guid> pageIds, Guid actorId, string? message, bool includeOwn, CancellationToken ct)
    {
        var unsaved = await m_session.QueryOver<PageCollaboration>()
            .Where(GuidList.In("PageId", pageIds))
            .And(Expression.Sql("{alias}.\"Sequence\" > coalesce({alias}.\"LastSavedSequence\", 0)"))
            .Select(x => x.PageId).ListAsync<Guid>(ct);
        foreach (var pageId in unsaved.Order())
        {
            await RequireNoForeignUnsavedAsync(pageId, actorId, ct, message, includeOwn);
        }
    }

    /// <summary>The refusal over any unsaved live edits (includeOwn), which does not say whose they are.</summary>
    public const string UnsavedMessage = "Na stránce jsou neuložené živé úpravy. Počkejte, až se uloží, a zkuste to znovu.";

    public async Task ResetAsync(Page page, PageRevision revision, CancellationToken ct)
    {
        if (revision.PageId != page.Id)
        {
            throw new ArgumentException("Revision belongs to another page.");
        }
        if (LivePageWriteScope.Current is { } live && live.PageId == page.Id)
        {
            await KeepLiveAsync(live, revision, ct);
            return;
        }
        // Flush a newly created revision before moving the operational foreign key to it.
        await m_session.FlushAsync(ct);
        var state = await m_session.GetAsync<PageCollaboration>(page.Id, LockMode.Upgrade, ct);
        if (state is null)
        {
            return;
        }
        await m_session.CreateQuery("delete from CollaborationUpdate where PageId = :page")
            .SetParameter("page", page.Id)
            .ExecuteUpdateAsync(ct);
        await CollaborationSecretReferences.ClearAsync(m_session, [page.Id], ct);
        Reset(state, revision.Id);
    }

    /// <summary>
    /// The save of a live write (3.6-H) keeps the generation: its target must still be what the agent read. The revision becomes the live
    /// document's base only with its update (<see cref="LivePageWrites"/> appends it after the commit, then MarkLiveWriteSaved; failing that,
    /// resets as before): until then the live document lacks it, so no checkpoint of the live document is taken (<see cref="ValidateCheckpointAsync"/>).
    /// An unchanged save writes nothing.
    /// </summary>
    private async Task KeepLiveAsync(LivePageWriteScope live, PageRevision revision, CancellationToken ct)
    {
        var current = live.Written?.Id ?? live.StartRevisionId;
        if (revision.Id == current)
        {
            return;
        }
        await m_session.FlushAsync(ct);
        var currentJson = live.Written?.ContentJson ?? (await m_session.GetAsync<PageRevision>(current, ct))!.ContentJson;
        await LiveWriteTarget.RequireAsync(m_session, live, currentJson, revision.ContentJson, ct);
        var state = await m_session.GetAsync<PageCollaboration>(revision.PageId, LockMode.Upgrade, ct);
        if (state?.Generation != live.Generation)
        {
            throw new ApiException(409, "Stránku mezitím někdo obnovil nebo přesunul. Načtěte ji znovu a zkuste to znovu.", "collaboration_reset");
        }
        live.Written = (revision.Id, revision.ContentJson);
    }

    /// <summary><see cref="ResetAsync"/> of many locked pages to their active revisions (the trash, a move to another space), in a
    /// constant number of statements that never read a snapshot.</summary>
    public async Task ResetAllAsync(IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        // The caller's page row locks exclude every append and checkpoint writer, so the states need no lock of their own. A pending
        // change of a loaded state is written first, so the commit cannot write it over the reset.
        await m_session.FlushAsync(ct);
        await m_session.CreateSQLQuery($"""DELETE FROM "CollaborationUpdate" WHERE "PageId" = {GuidList.Any(":pages")}""")
            .SetParameter("pages", GuidList.Param(pageIds)).ExecuteUpdateAsync(ct);
        await CollaborationSecretReferences.ClearAsync(m_session, pageIds, ct);
        // As Reset, with a new generation of each page.
        await m_session.CreateSQLQuery($"""
                UPDATE "PageCollaboration" c SET "Generation" = gen_random_uuid(), "BaseRevisionId" = r."Id", "Sequence" = 0, "LastSavedSequence" = 0,
                    "SnapshotSequence" = 0, "PendingCount" = 0, "PendingBytes" = 0, "SchemaBound" = false, "SchemaJson" = NULL, "State" = NULL,
                    "StateSha256" = NULL, "ProjectionJson" = NULL, "RefusedSequence" = NULL,
                    "Refusal" = NULL, "UpdatedAt" = :now
                FROM "PageRevision" r
                WHERE r."PageId" = c."PageId" AND r."ActiveTo" IS NULL AND c."PageId" = {GuidList.Any(":pages")}
                """)
            .SetParameter("now", DateTime.UtcNow, NHibernateUtil.UtcDateTime)
            .SetParameter("pages", GuidList.Param(pageIds)).ExecuteUpdateAsync(ct);
    }

    private static void Reset(PageCollaboration state, Guid revisionId)
    {
        state.Generation = Guid.NewGuid();
        state.BaseRevisionId = revisionId;
        state.Sequence = 0;
        state.LastSavedSequence = 0;
        state.SnapshotSequence = 0;
        state.PendingCount = 0;
        state.PendingBytes = 0;
        state.SchemaBound = false;
        state.SchemaJson = null;
        state.State = null;
        state.StateSha256 = null;
        state.ProjectionJson = null;
        state.RefusedSequence = null;
        state.Refusal = null;
        state.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>The refusal a flush recorded (<see cref="FlushRefusal"/>) as the save's error, its code and path included.</summary>
    private static ApiException Refused(string refusal)
    {
        var recorded = JsonSerializer.Deserialize<FlushRefusal>(refusal)!;
        return new ApiException(recorded.Status, "Společné změny nejde uložit, server je odmítl: " + recorded.Message, recorded.Code,
            recorded.Path is null ? null : new Dictionary<string, object?> { ["path"] = recorded.Path });
    }
}

/// <summary>What <c>PageCollaboration.Refusal</c> holds: the validation error of a refused snapshot, never its content.</summary>
public sealed record FlushRefusal(int Status, string? Code, string Message, string? Path)
{
    public static string Of(ApiException error) =>
        JsonSerializer.Serialize(new FlushRefusal(error.Status, error.Code, error.Message, error.Extensions?.GetValueOrDefault("path") as string));
}
