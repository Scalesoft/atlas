using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.TrashCleanup.Contracts;
using Atlas.Server.Features.TrashCleanup.Domain;
using Atlas.Server.Features.TrashCleanup.Queries;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.TrashCleanup.Commands;

/// <summary>
/// The wiki's rule of the cleanup (Správa wiki → Koš prostorů): a wiki admin only, CAS on the current interval. Saving the current
/// value writes nothing, so the history records only real decisions. Switching it to one rule for all makes the spaces' own rows idle,
/// not gone: they apply again once spaces decide.
/// </summary>
public sealed class WikiTrashCleanupHandler(ISession session, ModuleSettingsAccess access, IAtlasLocks locks)
{
    private const long SettingsLockKey = 731145097;

    public async Task<WikiTrashCleanupView> Handle(GetWikiTrashCleanup query, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        return CurrentTrashCleanup.ToView(await CurrentTrashCleanup.LoadWikiAsync(session, ct));
    }

    public async Task<WikiTrashCleanupView> Handle(SaveWikiTrashCleanup command, CancellationToken ct)
    {
        var actor = await access.RequireWikiAdminAsync(ct);
        var request = command.Request;
        if (request.Scope is not (TrashCleanupSetting.SpaceScope or TrashCleanupSetting.WikiScope))
        {
            throw new ApiException(400, "Zvolte, zda o čištění rozhodují prostory, nebo platí jedno pravidlo všude.");
        }
        CurrentTrashCleanup.Validate(request.Automatic, request.Days);
        await locks.AcquireAdvisoryAsync(SettingsLockKey, ct);
        var previous = await CurrentTrashCleanup.LoadWikiAsync(session, ct);
        AdministrationVersion.Expect(previous?.ActiveFrom, request.ExpectedVersion);
        if (previous is not null && previous.Scope == request.Scope && previous.Automatic == request.Automatic && previous.Days == request.Days)
        {
            return CurrentTrashCleanup.ToView(previous);
        }
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = now;
            // One current row: close it before the replacement insert reaches the database.
            await session.FlushAsync(ct);
        }
        var saved = new TrashCleanupSetting
        {
            Id = Guid.NewGuid(), Scope = request.Scope, Automatic = request.Automatic!.Value, Days = request.Days!.Value, ChangedById = actor.Id, ActiveFrom = now
        };
        await session.SaveAsync(saved, ct);
        return CurrentTrashCleanup.ToView(saved);
    }
}

/// <summary>
/// A space's cleanup (Nastavení prostoru → Koš): space admins and wiki admins only; a member gets 403, anyone else the 404 of a space it
/// cannot see. While the wiki decides for all the space's rule is read-only (409 decided_by_wiki). Serialized per space as
/// "Zahrnout prostor do AI".
/// </summary>
public sealed class SpaceTrashCleanupHandler(ISession session, IContentAccess access, IAtlasLocks locks, ContentParticipants participants, ObjectTypes types)
{
    public async Task<SpaceTrashCleanupView> Handle(GetSpaceTrashCleanup query, CancellationToken ct)
    {
        await access.LockActorAsync(query.SpaceId, ct);
        await access.RequireSpaceAsync(query.SpaceId, SpaceRole.Admin, ct);
        return await ViewAsync(query.SpaceId, await CurrentTrashCleanup.LoadSpaceAsync(session, query.SpaceId, ct), ct);
    }

    public async Task<SpaceTrashCleanupView> Handle(SaveSpaceTrashCleanup command, CancellationToken ct)
    {
        await locks.AcquireContentSharedAsync(ct);
        if (!await locks.LockSpaceForUpdateAsync(command.SpaceId, ct))
        {
            throw new ApiException(404, "Prostor nebyl nalezen nebo k němu nemáte přístup.");
        }
        var actor = await access.LockActorAsync(command.SpaceId, ct);
        await access.RequireSpaceAsync(command.SpaceId, SpaceRole.Admin, ct);
        var request = command.Request;
        CurrentTrashCleanup.Validate(request.Automatic, request.Days);
        if (await CurrentTrashCleanup.LoadWikiAsync(session, ct) is { Scope: TrashCleanupSetting.WikiScope })
        {
            throw new ApiException(409, "O čištění rozhoduje pravidlo celé wiki ve Správě wiki.", "decided_by_wiki");
        }
        await locks.AcquireAdvisoryAsync($"space-trash-cleanup:{command.SpaceId:N}", ct);
        var previous = await CurrentTrashCleanup.LoadSpaceAsync(session, command.SpaceId, ct);
        AdministrationVersion.Expect(previous?.ActiveFrom, request.ExpectedVersion);
        if (previous is not null && previous.Automatic == request.Automatic && previous.Days == request.Days)
        {
            return await ViewAsync(command.SpaceId, previous, ct);
        }
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = now;
            await session.FlushAsync(ct);
        }
        var saved = new SpaceTrashCleanupSetting
        {
            Id = Guid.NewGuid(), SpaceId = command.SpaceId, Automatic = request.Automatic!.Value, Days = request.Days!.Value, ChangedById = actor.Id, ActiveFrom = now
        };
        await session.SaveAsync(saved, ct);
        return await ViewAsync(command.SpaceId, saved, ct);
    }

    private async Task<SpaceTrashCleanupView> ViewAsync(Guid spaceId, SpaceTrashCleanupSetting? own, CancellationToken ct)
    {
        var rule = CurrentTrashCleanup.Rule(await CurrentTrashCleanup.LoadWikiAsync(session, ct), own, true);
        var counts = await TrashCleanupCandidates.CountAsync(session, participants, types, spaceId, rule.Days, DateTime.UtcNow, ct);
        return new SpaceTrashCleanupView(own?.Automatic ?? false, own?.Days ?? CurrentTrashCleanup.DefaultDays,
            own is null ? null : AdministrationVersion.Format(own.ActiveFrom), rule.DecidedByWiki, rule.Automatic, rule.Days, counts);
    }
}
