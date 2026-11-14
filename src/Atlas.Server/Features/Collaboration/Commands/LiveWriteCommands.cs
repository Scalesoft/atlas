using NHibernate;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Commands;

/// <summary>A page's live generation as a live write sees it (3.6-H); <see cref="Page"/> is null without a collaboration row, and also when
/// the actor may not edit the page or, for an MCP tool (<paramref name="Mcp"/>), its space is out of AI: the write then runs as today and its
/// handler refuses it before anything touches the live document.</summary>
public sealed record GetLiveWritePage(Guid PageId, bool Mcp = false);
public sealed record LiveWritePage(Guid Generation, long Sequence, long LastSavedSequence, int Version, string Title, Guid RevisionId);
public sealed record LiveWritePageResult(LiveWritePage? Page);

/// <summary>Who an agent writes for: the account's name and stamp, and the personal API token's name.</summary>
public sealed record GetLiveWriteAgent(Guid AccountId, Guid? TokenId);
public sealed record LiveWriteAgent(string Name, string? SecurityStamp, string? TokenName);

/// <summary>The live update of a write is appended: its sequence is saved and its revision is the live document's base from now on.</summary>
public sealed record MarkLiveWriteSaved(Guid PageId, Guid Generation, long Sequence, Guid RevisionId);

/// <summary>The fallback of a live write that could not be appended: today's reset to the write's revision, unless another write reset
/// <paramref name="Generation"/> meanwhile (its new generation may already hold what people typed since).</summary>
public sealed record ResetLiveWrite(Guid PageId, Guid RevisionId, Guid Generation);

/// <summary>Whether a revision a live write saved was committed.</summary>
public sealed record LiveWriteRevisionExists(Guid RevisionId);
public sealed record LiveWriteRevision(bool Exists);

public sealed class LiveWriteCommandsHandler(ISession session, CollaborationCheckpoints checkpoints, PageGate gate, McpVisibility visibility)
{
    public async Task<LiveWritePageResult> Handle(GetLiveWritePage query, CancellationToken ct)
    {
        if ((await gate.ReadAsync(query.PageId, AccessOperation.EditContent, ct)).State != PageOpeningState.Open
            || query.Mcp && !(await visibility.PagesInScopeAsync([query.PageId], ct)).Contains(query.PageId))
        {
            return new LiveWritePageResult(null);
        }
        var row = await session.CreateSQLQuery("""
                SELECT c."Generation", c."Sequence", coalesce(c."LastSavedSequence", 0) AS "Saved", p."Version", r."Title", r."Id" AS "RevisionId"
                FROM "PageCollaboration" c JOIN "Page" p ON p."Id" = c."PageId"
                JOIN "PageRevision" r ON r."PageId" = p."Id" AND r."ActiveTo" IS NULL
                WHERE c."PageId" = :page
                """)
            .AddScalar("Generation", NHibernateUtil.Guid).AddScalar("Sequence", NHibernateUtil.Int64).AddScalar("Saved", NHibernateUtil.Int64)
            .AddScalar("Version", NHibernateUtil.Int32).AddScalar("Title", NHibernateUtil.String).AddScalar("RevisionId", NHibernateUtil.Guid)
            .SetParameter("page", query.PageId).UniqueResultAsync<object[]>(ct);
        return new LiveWritePageResult(row is null ? null
            : new LiveWritePage((Guid)row[0], (long)row[1], (long)row[2], (int)row[3], (string)row[4], (Guid)row[5]));
    }

    public async Task<LiveWriteAgent> Handle(GetLiveWriteAgent query, CancellationToken ct)
    {
        var account = await session.GetAsync<UserAccount>(query.AccountId, ct) ?? throw new ApiException(401, "Přihlášení vypršelo.");
        var token = query.TokenId is { } id ? await session.GetAsync<PersonalApiToken>(id, ct) : null;
        return new LiveWriteAgent(account.DisplayName, account.SecurityStamp, token?.Name);
    }

    public async Task Handle(MarkLiveWriteSaved command, CancellationToken ct)
    {
        await session.CreateQuery("update PageCollaboration set LastSavedSequence = :sequence, BaseRevisionId = :revision, UpdatedAt = :now "
                + "where PageId = :page and Generation = :generation and Sequence = :sequence")
            .SetParameter("sequence", command.Sequence)
            .SetParameter("revision", command.RevisionId)
            .SetParameter("now", DateTime.UtcNow, NHibernateUtil.UtcDateTime)
            .SetParameter("page", command.PageId)
            .SetParameter("generation", command.Generation)
            .ExecuteUpdateAsync(ct);
    }

    public async Task Handle(ResetLiveWrite command, CancellationToken ct)
    {
        var page = await session.GetForUpdateAsync<Page>(command.PageId, ct);
        var revision = await session.GetAsync<PageRevision>(command.RevisionId, ct);
        if (page is not null && revision is not null && (await session.GetAsync<PageCollaboration>(page.Id, ct))?.Generation == command.Generation)
        {
            await checkpoints.ResetAsync(page, revision, ct);
        }
    }

    public async Task<LiveWriteRevision> Handle(LiveWriteRevisionExists query, CancellationToken ct)
        => new(await session.GetAsync<PageRevision>(query.RevisionId, ct) is not null);
}
