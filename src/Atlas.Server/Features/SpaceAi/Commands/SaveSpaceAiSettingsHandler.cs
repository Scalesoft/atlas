using Atlas.Server.Features.SpaceAi.Contracts;
using Atlas.Server.Features.SpaceAi.Domain;
using Atlas.Server.Features.SpaceAi.Queries;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceAi.Commands;

/// <summary>
/// "Zahrnout prostor do AI". Authorized before anything is read (404 for everyone but space admins and WikiAdmins), then
/// serialized per space so two admins saving the same version get one change and one 409, never a duplicate current row.
/// Saving the current value writes nothing and keeps the version. The change takes effect at the next retrieval query
/// through AccessSql.SpaceInAi; the index worker removes the space's vectors later.
/// </summary>
public sealed class SaveSpaceAiSettingsHandler(ISession session, IContentAccess access, IAtlasLocks locks, IJobRequests jobs)
{
    public async Task<SpaceAiSettingsView> Handle(SaveSpaceAiSettings command, CancellationToken ct)
    {
        // Match the answer gate's Space FOR SHARE, before the actor lock and without a shared-to-exclusive upgrade.
        await locks.AcquireContentSharedAsync(ct);
        if (!await locks.LockSpaceForUpdateAsync(command.SpaceId, ct))
        {
            throw new ApiException(404, "Prostor nebyl nalezen nebo k němu nemáte přístup.");
        }
        var actor = await access.LockActorAsync(command.SpaceId, ct);
        await access.RequireSpaceAsync(command.SpaceId, SpaceRole.Admin, ct);
        var included = command.Request.Included ?? throw new ApiException(400, "Zvolte, zda prostor zahrnout do AI.");
        await locks.AcquireAdvisoryAsync($"space-ai:{command.SpaceId:N}", ct);
        var previous = await CurrentSpaceAi.LoadAsync(session, command.SpaceId, ct);
        AdministrationVersion.Expect(previous?.ActiveFrom, command.Request.ExpectedVersion);
        if ((previous?.Included ?? true) == included)
        {
            return CurrentSpaceAi.ToView(previous);
        }
        var now = Temporal.NextBoundary(previous?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (previous is not null)
        {
            previous.ActiveTo = now;
            // One current row per space: close it before the replacement insert reaches the database.
            await session.FlushAsync(ct);
        }
        var saved = new SpaceAiSetting { Id = Guid.NewGuid(), SpaceId = command.SpaceId, Included = included, ChangedById = actor.Id, ActiveFrom = now };
        await session.SaveAsync(saved, ct);
        await AiIndex.IndexTasks.EnqueueSpaceAsync(session, command.SpaceId, ct);
        // An included space's pages wait in the index backlog: its job (IndexJob, "ai-index.index") plans again once this commits.
        await jobs.RescheduleAsync("ai-index.index", ct);
        return CurrentSpaceAi.ToView(saved);
    }
}
