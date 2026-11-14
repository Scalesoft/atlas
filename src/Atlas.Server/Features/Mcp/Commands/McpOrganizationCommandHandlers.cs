using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageOrganization.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Commands;

// The page organization of MCP (spec section 4): the shared move, cross-space move, trash, restore and trash listing of the web and
// REST, each in its own transaction with the channel mcp and the token from IRequestChannel. The shared handlers decide every right,
// the CAS on the tree versions and the page version, cycles, hidden parents, the explicit subtree scope and the live-edit guard of
// D3. MCP adds G5 under the same locks, taken in the shared order (advisory, spaces ascending), so a switch of "Zahrnout do AI a
// nástrojů MCP" cannot slip between the check and the write; an excluded page or space answers the 404 of a missing one. The
// conflicts are re-worded for a model: what changed and to re-read the tree. Nothing here undoes itself: a restore or another move
// does, and the audit of the call filter records every attempt.

/// <summary>The locks and the G5 decision every MCP organization handler starts with.</summary>
public sealed class McpOrganizationGuard
{
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly McpVisibility m_visibility;

    public McpOrganizationGuard(ISession session, IAtlasLocks locks, McpVisibility visibility)
    {
        m_session = session;
        m_locks = locks;
        m_visibility = visibility;
    }

    /// <summary>The page's space, locked FOR UPDATE (the shared handler takes the same lock next), after G5 said yes.</summary>
    public async Task<Guid> LockPageSpaceAsync(Guid pageId, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        var space = await SpaceOfAsync(pageId, ct);
        if (!await m_locks.LockSpaceForUpdateAsync(space, ct))
        {
            throw new ApiException(404, McpVisibility.MissingPage);
        }
        await m_visibility.RequirePageAsync(pageId, ct);
        return space;
    }

    /// <summary>The spaces of several pages locked FOR UPDATE in ascending order before any page is touched (a batch over several spaces
    /// takes them in the one order a two-space move does). A missing page is skipped: its own <see cref="LockPageSpaceAsync"/> answers 404.</summary>
    public async Task LockSpacesOfPagesAsync(IReadOnlyCollection<Guid> pageIds, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        var spaces = await m_session.QueryOver<Page>().WhereRestrictionOn(x => x.Id).IsIn(pageIds.ToArray()).Select(x => x.SpaceId).ListAsync<Guid>(ct);
        foreach (var space in spaces.Distinct().Order())
        {
            await m_locks.LockSpaceForUpdateAsync(space, ct);
        }
    }

    /// <summary>The page's space locked FOR SHARE (content shared, space, then G5), for the handlers whose shared write takes the space only
    /// FOR SHARE (the page gate's edit: save, embeds, attachment store). A switch of "Zahrnout do AI" locks the space FOR UPDATE, so G5 holds until commit.</summary>
    public async Task<Guid> LockPageSpaceSharedAsync(Guid pageId, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        var space = await SpaceOfAsync(pageId, ct);
        await m_locks.LockSpaceSharedAsync(space, ct);
        await m_visibility.RequirePageAsync(pageId, ct);
        return space;
    }

    /// <summary>The source space of the page and the target space, both locked FOR UPDATE in ascending order as the shared move does.
    /// Either one excluded from MCP (or missing) answers 404 and nothing is written.</summary>
    public async Task<Guid> LockBothSpacesAsync(Guid pageId, Guid targetSpaceId, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        var source = await SpaceOfAsync(pageId, ct);
        foreach (var space in new[] { source, targetSpaceId }.Distinct().Order())
        {
            if (!await m_locks.LockSpaceForUpdateAsync(space, ct))
            {
                throw space == source ? new ApiException(404, McpVisibility.MissingPage) : McpWriteErrors.MissingSpace();
            }
        }
        await m_visibility.RequirePageAsync(pageId, ct);
        try
        {
            await m_visibility.RequireSpaceAsync(targetSpaceId, ct);
        }
        catch (ApiException)
        {
            throw McpWriteErrors.MissingSpace();
        }
        return source;
    }

    /// <summary>A space to change, locked FOR UPDATE (the shared handler takes the same lock next), after G5 said yes.</summary>
    public async Task LockSpaceForWriteAsync(Guid spaceId, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        if (!await m_locks.LockSpaceForUpdateAsync(spaceId, ct))
        {
            throw McpWriteErrors.MissingSpace();
        }
        try
        {
            await m_visibility.RequireSpaceAsync(spaceId, ct);
        }
        catch (ApiException)
        {
            throw McpWriteErrors.MissingSpace();
        }
    }

    /// <summary>A space to read, locked FOR SHARE, after G5 said yes.</summary>
    public async Task LockSpaceForReadAsync(Guid spaceId, CancellationToken ct)
    {
        await m_locks.AcquireContentSharedAsync(ct);
        if (!await LiveSpace.ExistsAsync(m_session, spaceId, ct))
        {
            throw McpWriteErrors.MissingSpace();
        }
        await m_locks.LockSpaceSharedAsync(spaceId, ct);
        try
        {
            await m_visibility.RequireSpaceAsync(spaceId, ct);
        }
        catch (ApiException)
        {
            throw McpWriteErrors.MissingSpace();
        }
    }

    private async Task<Guid> SpaceOfAsync(Guid pageId, CancellationToken ct)
    {
        var space = await m_session.QueryOver<Page>().Where(x => x.Id == pageId).Select(x => x.SpaceId).SingleOrDefaultAsync<Guid>(ct);
        return space == Guid.Empty ? throw new ApiException(404, McpVisibility.MissingPage) : space;
    }
}

/// <summary>The conflicts of the shared organization handlers worded for a model: what happened, that nothing changed, what to call next.</summary>
public static class McpOrganizationErrors
{
    private const string Reread = "Načtěte strom prostoru znovu (atlas_list_pages) a akci zopakujte s aktuálními verzemi.";

    /// <summary>The re-worded conflict, or the refusal as the handler gave it. Runs inside the still open transaction of the handler.</summary>
    public static async Task<ApiException> ForAsync(ApiException error, ISession session, Guid pageId, IReadOnlyList<Guid> spaces, CancellationToken ct)
    {
        switch (error.Code)
        {
            case "tree_conflict":
                var extensions = new Dictionary<string, object?>();
                if (spaces.Count == 1)
                {
                    extensions["currentTreeVersion"] = await TreeVersion.ReadAsync(session, spaces[0], ct);
                }
                else
                {
                    extensions["currentSourceTreeVersion"] = await TreeVersion.ReadAsync(session, spaces[0], ct);
                    extensions["currentTargetTreeVersion"] = await TreeVersion.ReadAsync(session, spaces[1], ct);
                }
                return new ApiException(409, "Strom prostoru se mezitím změnil, nebo expectedTreeVersion není aktuální. Nic se nezměnilo. " + Reread,
                    "tree_conflict", extensions);
            case "version_conflict":
                var current = (await session.GetAsync<Page>(pageId, ct))?.Version;
                return new ApiException(409, "Stránku mezitím někdo změnil" + (current is null ? "" : $" (aktuální verze {current})") + ". Nic se nezměnilo. " + Reread,
                    "version_conflict", current is null ? null : new Dictionary<string, object?> { ["currentVersion"] = current });
            case "collaboration_unsaved":
                return new ApiException(409, "Na stránce nebo některé z jejích podstránek jsou neuložené živé úpravy. Nic se nezměnilo. " +
                    "Počkejte, až se uloží (atlas_list_pages je označuje jako hasUnsavedLiveEdits), a zkuste to znovu.", "collaboration_unsaved");
            case "space_changed":
                return new ApiException(409, "Stránka mezitím byla přesunuta do jiného prostoru. Nic se nezměnilo. " + Reread, "space_changed");
            case "subtree_confirmation_required":
                return new ApiException(409, "Stránka má podstránky a rozsah nebyl výslovně potvrzen. Nic se nezměnilo. Zopakujte s includeSubtree: true, " +
                    "má-li se akce týkat celého podstromu, nebo podstránky nejdřív přesuňte jinam (atlas_move_page).", "subtree_confirmation_required");
            default:
                return error;
        }
    }
}

public sealed class MoveMcpPageHandler
{
    private readonly ISession m_session;
    private readonly McpOrganizationGuard m_guard;
    private readonly MovePageHandler m_move;

    public MoveMcpPageHandler(ISession session, McpOrganizationGuard guard, MovePageHandler move)
    {
        m_session = session;
        m_guard = guard;
        m_move = move;
    }

    public async Task<McpOrganizationResult> Handle(MoveMcpPage command, CancellationToken ct)
    {
        var space = await m_guard.LockPageSpaceAsync(command.PageId, ct);
        try
        {
            return new McpOrganizationResult(space, await m_move.Handle(new MovePage(command.PageId, command.Request), ct));
        }
        catch (ApiException error)
        {
            throw await McpOrganizationErrors.ForAsync(error, m_session, command.PageId, [space], ct);
        }
    }
}

public sealed class SortMcpTreeLevelHandler
{
    private readonly ISession m_session;
    private readonly McpOrganizationGuard m_guard;
    private readonly SortTreeLevelHandler m_sort;

    public SortMcpTreeLevelHandler(ISession session, McpOrganizationGuard guard, SortTreeLevelHandler sort)
    {
        m_session = session;
        m_guard = guard;
        m_sort = sort;
    }

    public async Task<McpOrganizationResult> Handle(SortMcpTreeLevel command, CancellationToken ct)
    {
        if (command.Request.ParentId is { } parent)
        {
            if (await m_guard.LockPageSpaceAsync(parent, ct) != command.SpaceId)
            {
                throw new ApiException(404, McpVisibility.MissingPage);
            }
        }
        else
        {
            await m_guard.LockSpaceForWriteAsync(command.SpaceId, ct);
        }
        try
        {
            return new McpOrganizationResult(command.SpaceId, await m_sort.Handle(new SortTreeLevel(command.SpaceId, command.Request), ct));
        }
        catch (ApiException error)
        {
            throw await McpOrganizationErrors.ForAsync(error, m_session, command.Request.ParentId ?? Guid.Empty, [command.SpaceId], ct);
        }
    }
}

public sealed class ArrangeMcpPagesHandler
{
    private readonly ISession m_session;
    private readonly McpOrganizationGuard m_guard;
    private readonly MovePageHandler m_move;

    public ArrangeMcpPagesHandler(ISession session, McpOrganizationGuard guard, MovePageHandler move)
    {
        m_session = session;
        m_guard = guard;
        m_move = move;
    }

    /// <summary>
    /// The moves in order through the shared move, the tree version handed from one to the next. Each move is the web's (rights,
    /// cycles, hidden parents, positions), so a later move sees the earlier ones. The first refusal throws with <c>items</c> naming
    /// its index (as atlas_apply_labels does) and the transaction undoes the earlier moves: all or nothing.
    /// </summary>
    public async Task<McpOrganizationResult> Handle(ArrangeMcpPages command, CancellationToken ct)
    {
        var version = command.ExpectedTreeVersion;
        Guid? space = null;
        var affected = new List<Guid>();
        var cancelled = false;
        for (var index = 0; index < command.Moves.Count; index++)
        {
            var move = command.Moves[index];
            try
            {
                var pageSpace = await m_guard.LockPageSpaceAsync(move.PageId, ct);
                if (space is { } first && first != pageSpace)
                {
                    throw new ApiException(400, "Všechny stránky musí být v jednom prostoru; přesun mezi prostory je atlas_move_page_to_space.", "invalid_argument");
                }
                space = pageSpace;
                try
                {
                    var change = await m_move.Handle(new MovePage(move.PageId, new MovePageRequest(move.ParentId, move.BeforePageId, version)), ct);
                    version = change.Version;
                    affected.AddRange(change.AffectedIds);
                    cancelled |= change.TitleOrderCancelled;
                }
                catch (ApiException error)
                {
                    throw await McpOrganizationErrors.ForAsync(error, m_session, move.PageId, [pageSpace], ct);
                }
            }
            catch (ApiException error) when (error.Status is >= 400 and < 500)
            {
                throw Failed(index, move.PageId, command.Moves.Count, error);
            }
        }
        return new McpOrganizationResult(space!.Value, new OrganizationChange(version, affected.Distinct().ToArray(), TitleOrderCancelled: cancelled));
    }

    private static ApiException Failed(int index, Guid pageId, int count, ApiException error)
    {
        var item = new Dictionary<string, object?>
        {
            ["index"] = index, ["pageId"] = pageId, ["status"] = error.Status, ["code"] = error.Code ?? McpErrors.CodeFor(error.Status), ["message"] = error.Message
        };
        foreach (var (key, value) in error.Extensions ?? new Dictionary<string, object?>())
        {
            item.TryAdd(key, value);
        }
        return new ApiException(error.Status, $"Nic se nepřesunulo: přesun moves[{index}] z {count} (pageId {pageId}) nelze provést: {item["code"]}, {error.Message}",
            (string)item["code"]!, new Dictionary<string, object?> { ["items"] = new[] { item } });
    }
}

public sealed class MoveMcpPageToSpaceHandler
{
    private readonly ISession m_session;
    private readonly McpOrganizationGuard m_guard;
    private readonly MovePageToSpaceHandler m_move;

    public MoveMcpPageToSpaceHandler(ISession session, McpOrganizationGuard guard, MovePageToSpaceHandler move)
    {
        m_session = session;
        m_guard = guard;
        m_move = move;
    }

    public async Task<CrossSpaceChange> Handle(MoveMcpPageToSpace command, CancellationToken ct)
    {
        var source = await m_guard.LockBothSpacesAsync(command.PageId, command.Request.TargetSpaceId, ct);
        try
        {
            return await m_move.Handle(new MovePageToSpace(command.PageId, command.Request), ct);
        }
        catch (ApiException error)
        {
            throw await McpOrganizationErrors.ForAsync(error, m_session, command.PageId, [source, command.Request.TargetSpaceId], ct);
        }
    }
}

public sealed class TrashMcpPageHandler
{
    private readonly ISession m_session;
    private readonly McpOrganizationGuard m_guard;
    private readonly TrashPageHandler m_trash;

    public TrashMcpPageHandler(ISession session, McpOrganizationGuard guard, TrashPageHandler trash)
    {
        m_session = session;
        m_guard = guard;
        m_trash = trash;
    }

    public async Task<McpOrganizationResult> Handle(TrashMcpPage command, CancellationToken ct)
    {
        var space = await m_guard.LockPageSpaceAsync(command.PageId, ct);
        try
        {
            return new McpOrganizationResult(space, await m_trash.Handle(new TrashPage(command.PageId, command.Request), ct));
        }
        catch (ApiException error)
        {
            throw await McpOrganizationErrors.ForAsync(error, m_session, command.PageId, [space], ct);
        }
    }
}

public sealed class RestoreMcpPageHandler
{
    private readonly ISession m_session;
    private readonly McpOrganizationGuard m_guard;
    private readonly UntrashPageHandler m_untrash;

    public RestoreMcpPageHandler(ISession session, McpOrganizationGuard guard, UntrashPageHandler untrash)
    {
        m_session = session;
        m_guard = guard;
        m_untrash = untrash;
    }

    public async Task<McpOrganizationResult> Handle(RestoreMcpPage command, CancellationToken ct)
    {
        var space = await m_guard.LockPageSpaceAsync(command.PageId, ct);
        try
        {
            return new McpOrganizationResult(space, await m_untrash.Handle(new UntrashPage(command.PageId, command.Request), ct));
        }
        catch (ApiException error)
        {
            throw await McpOrganizationErrors.ForAsync(error, m_session, command.PageId, [space], ct);
        }
    }
}

public sealed class ListMcpTrashHandler
{
    private readonly McpOrganizationGuard m_guard;
    private readonly ListPageTrashHandler m_list;

    public ListMcpTrashHandler(McpOrganizationGuard guard, ListPageTrashHandler list)
    {
        m_guard = guard;
        m_list = list;
    }

    public async Task<PageTrashDto> Handle(ListMcpTrash query, CancellationToken ct)
    {
        await m_guard.LockSpaceForReadAsync(query.SpaceId, ct);
        return await m_list.Handle(new ListPageTrash(query.SpaceId, query.Offset, query.Limit), ct);
    }
}
