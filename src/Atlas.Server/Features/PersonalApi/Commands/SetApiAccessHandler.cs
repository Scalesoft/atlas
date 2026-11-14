using NHibernate;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi.Domain;
using Atlas.Server.Features.PersonalApi.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PersonalApi.Commands;

/// <summary>Sets an account's API level. Level none closes every live token, exactly as switching the permission off did; a lower level
/// keeps them open and authentication narrows their effective scopes from the next request.</summary>
public sealed class SetApiAccessHandler
{
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;
    private readonly PersonalApiActor m_actor;

    public SetApiAccessHandler(ISession session, IAtlasLocks locks, PersonalApiActor actor)
    {
        m_session = session;
        m_locks = locks;
        m_actor = actor;
    }

    public async Task<ApiPermissionDto> Handle(SetApiAccess command, CancellationToken ct)
    {
        // Same ordering as global account administration avoids cross-admin lock inversions.
        await m_locks.AcquireContentExclusiveAsync(ct);
        var actor = await m_actor.LockAsync(ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "Povolení API spravuje správce wiki.");
        }
        if (await m_session.GetAsync<UserAccount>(command.UserId, LockMode.Upgrade, ct) is null)
        {
            throw new ApiException(404, "Uživatel nebyl nalezen.");
        }
        var permission = await CurrentApiPermission.LoadAsync(m_session, command.UserId, ct);
        AdministrationVersion.Expect(permission?.ActiveFrom, command.Request.ExpectedVersion);
        var current = CurrentApiPermission.Level(permission);
        var requested = Requested(command.Request, current);
        if (requested == current)
        {
            return CurrentApiPermission.ToDto(permission);
        }
        var now = Temporal.NextBoundary(permission?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (permission is not null)
        {
            permission.ActiveTo = now;
            await m_session.FlushAsync(ct);
        }
        var next = new UserApiPermission
        {
            Id = Guid.NewGuid(),
            UserId = command.UserId,
            Level = requested,
            ChangedById = actor.Id,
            ActiveFrom = now,
        };
        await m_session.SaveAsync(next, ct);
        await AccessAuditLog.RecordAsync(m_session, actor.Id,
            requested == ApiLevels.None ? "api_permission.disabled" : current == ApiLevels.None ? "api_permission.enabled" : "api_permission.changed",
            AccessAuditLog.ApiPermission, next.Id, ct);
        if (requested == ApiLevels.None)
        {
            await CloseLiveTokensAsync(command.UserId, now, ct);
        }
        return CurrentApiPermission.ToDto(next);
    }

    /// <summary>Level wins; a former level (draft, publish, direct) is refused as unknown, never mapped. A client of 1.5 sends only the
    /// switch: off is none; on enables write, the 1.5 meaning, but never changes the level of an account that already has one.</summary>
    private static string Requested(ChangeApiPermission request, string current)
    {
        if (request.Level is { } level)
        {
            return ApiLevels.IsKnown(level) ? level : throw new ApiException(400, "Neznámá úroveň osobního API.");
        }
        return request.CanIssueTokens switch
        {
            true => current == ApiLevels.None ? ApiLevels.Write : current,
            false => ApiLevels.None,
            null => throw new ApiException(400, "Zvolte úroveň osobního API.")
        };
    }

    // Closing usable credentials prevents resurrection if permission is restored later.
    // Temporal.NextBoundary(ActiveFrom, now) is exactly the previous GREATEST(now, ActiveFrom + 1 microsecond).
    // The set is bounded by the twenty-live-token rule only together with ExpiresAt > now, which is also the
    // filter authentication applies: an already expired interval can never be authenticated again, so leaving
    // its ActiveTo open changes nothing a caller can observe beyond that credential's own metadata.
    private async Task CloseLiveTokensAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var live = await m_session.QueryOver<PersonalApiToken>()
            .Where(x => x.UserId == userId && x.ActiveTo == null && x.ExpiresAt > now)
            .ListAsync(ct);
        foreach (var token in live)
        {
            token.ActiveTo = Temporal.NextBoundary(token.ActiveFrom, now);
        }
    }
}
