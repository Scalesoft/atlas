using System.Globalization;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.Security.Reauth;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess.Commands;

/// <summary>
/// The access of a new copy of a page with own access (Projector's deployment copy, reviews A2-1, A3-1–A3-3). Who manages the access
/// of the source and of the copy in the same space gets the source's current grants on the copy through SetPageGrant (bounded
/// delegation, the security-change guard): a grant longer than the actor's own authority ends with it, and a manager grant that
/// would need a fresh sign-in is left out; both are said in the warnings. Anyone else's copy is theirs alone: its other grants close
/// and the actor keeps editing it with an edit grant that ends when their edit right on the copy ends now.
/// </summary>
public sealed class PageGrantCopier(ISession session, ResourceGrantSnapshots grants, SetPageAccessModeHandler mode, SetPageGrantHandler grant,
    AccessChanges accessChanges)
{
    /// <summary>Gives <paramref name="copy"/> own access as described above; returns the warnings for the actor.</summary>
    public async Task<IReadOnlyList<string>> CopyAsync(UserAccount actor, Page source, Page copy, CancellationToken ct)
    {
        if (source.SpaceId == copy.SpaceId && (await PageAccessAuthority.ReadAsync(grants, actor, source, ct)).Allowed
            && await PageAccessAuthority.ReadAsync(grants, actor, copy, ct) is { Allowed: true } manages)
        {
            return await MirrorAsync(actor, source, copy, manages.Until, ct);
        }
        await KeepForActorAsync(actor, copy, ct);
        return [];
    }

    /// <summary>The switch copies the space defaults, then every grant of the source is set and every other one revoked, the actor's
    /// own last while their authority still holds.</summary>
    private async Task<IReadOnlyList<string>> MirrorAsync(UserAccount actor, Page source, Page copy, DateTime? until, CancellationToken ct)
    {
        if (!copy.OwnAccess)
        {
            var version = await SetPageAccessModeHandler.CurrentVersionAsync(session, copy.Id, ct);
            await mode.Handle(new SetPageAccessMode(copy.Id, new UpdatePageAccessMode(true, version is { } at ? AdministrationVersion.Format(at) : null)), ct);
        }
        var warnings = new List<string>();
        var now = DateTime.UtcNow;
        var wanted = (await CurrentAsync(source.Id, ct)).Where(x => x.ExpiresAt is null || x.ExpiresAt > now).ToArray();
        foreach (var row in wanted)
        {
            var expiresAt = row.ExpiresAt;
            if (until is { } end && (expiresAt is null || expiresAt > end))
            {
                if (row.StartAt >= end)
                {
                    warnings.Add($"Oprávnění pro {await NameAsync(row, ct)} začíná až po konci vaší správy přístupu, a proto nebylo zkopírováno.");
                    continue;
                }
                expiresAt = end;
                warnings.Add($"Oprávnění pro {await NameAsync(row, ct)} platí na kopii jen do {Prague(end)}, kdy končí vaše správa přístupu.");
            }
            try
            {
                await SetAsync(copy.Id, row.UserId, row.GroupId, PageAccessDirectory.RoleName(row.Role), row.StartAt, expiresAt, ct);
            }
            catch (ApiException error) when (row.Role == (int)AccessRole.DocumentManager && error.Code is ReauthRefusal.RequiredCode or ReauthRefusal.UnsupportedCode)
            {
                // Review A3-2: a fresh sign-in cannot be bound to a page that does not exist yet, and a token has none at all.
                warnings.Add($"Oprávnění správce pro {await NameAsync(row, ct)} nebylo zkopírováno — nastavte ho na kopii ručně.");
            }
        }
        var extra = (await CurrentAsync(copy.Id, ct))
            .Where(x => !wanted.Any(w => w.UserId == x.UserId && w.GroupId == x.GroupId)).OrderBy(x => x.UserId == actor.Id).ToArray();
        foreach (var row in extra)
        {
            await SetAsync(copy.Id, row.UserId, row.GroupId, null, null, null, ct);
        }
        return warnings;
    }

    private async Task SetAsync(Guid pageId, Guid? userId, Guid? groupId, string? role, DateTime? startAt, DateTime? expiresAt, CancellationToken ct)
    {
        var current = (await CurrentAsync(pageId, ct)).FirstOrDefault(x => x.UserId == userId && x.GroupId == groupId);
        await grant.Handle(new SetPageGrant(pageId, new UpdatePageGrant(userId, groupId, role, current is null ? null : AdministrationVersion.Format(current.ActiveFrom),
            Offset(startAt), Offset(expiresAt))), ct);
    }

    /// <summary>No ManageAccess needed: it widens nobody's access but the actor's, and theirs no further than they have it now (A3-1).</summary>
    private async Task KeepForActorAsync(UserAccount actor, Page copy, CancellationToken ct)
    {
        var (allowed, until) = (await grants.ForPageAsync(actor, copy, ct)).Authority(copy, AccessOperation.EditContent, DateTimeOffset.UtcNow);
        if (!allowed)
        {
            throw new ApiException(403, "Kopii nemůžete upravovat.");
        }
        copy.OwnAccess = true;
        await PageAccessModeHistory.RecordAsync(session, copy, actor.Id, ct);
        var kept = false;
        foreach (var row in await CurrentAsync(copy.Id, ct))
        {
            if (!kept && row.UserId == actor.Id && row.Role == (int)AccessRole.DocumentWriter && row.StartAt is null && row.ExpiresAt == until)
            {
                kept = true;
                continue;
            }
            await SetPageGrantHandler.WriteAsync(session, copy.Id, row.UserId, row.GroupId, row, null, null, null, () => Task.CompletedTask, actor.Id, ct);
        }
        if (!kept)
        {
            await SetPageGrantHandler.WriteAsync(session, copy.Id, actor.Id, null, null, (int)AccessRole.DocumentWriter, null, until, () => Task.CompletedTask, actor.Id, ct);
        }
        accessChanges.Changed();
    }

    private Task<IList<ResourceGrant>> CurrentAsync(Guid pageId, CancellationToken ct)
        => session.QueryOver<ResourceGrant>().Where(x => x.PageId == pageId && x.ActiveTo == null).ListAsync(ct);

    private async Task<string> NameAsync(ResourceGrant row, CancellationToken ct) => row.UserId is { } user
        ? (await session.GetAsync<UserAccount>(user, ct))?.DisplayName ?? ""
        : (await session.GetAsync<UserGroup>(row.GroupId!.Value, ct))?.Name ?? "";

    private static string Prague(DateTime utc)
        => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.SpecifyKind(utc, DateTimeKind.Utc), "Europe/Prague").ToString("d. M. yyyy H:mm", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Offset(DateTime? value) => value is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null;
}
