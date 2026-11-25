using Atlas.Markdown.Sync;
using Atlas.Server.Features.Auth.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Queries;

/// <summary>„Moje barva“ of an account as the stamped colour (the palette entry's light variant): null for "auto", which the live
/// sessions resolve from the account's hash (<see cref="CollaboratorPalette.Auto"/>).</summary>
public static class CollaboratorColorPreference
{
    public static async Task<string?> ReadAsync(ISession session, Guid userId, CancellationToken ct)
    {
        var id = await session.QueryOver<UserAppearancePreference>()
            .Where(x => x.UserId == userId && x.ActiveTo == null)
            .Select(x => x.CollaboratorColor)
            .SingleOrDefaultAsync<string>(ct);
        return CollaboratorPalette.SlotOf(id) is >= 0 and var slot ? CollaboratorPalette.Entries[slot].Light : null;
    }

    /// <summary>Every active account's chosen colour (none for "auto"), user id → stamped colour, in one query: the static surfaces (history,
    /// discussion, the peek) draw authors in it (Atlas.Web live/collaboratorColors.ts), the others in their automatic one. Any signed-in
    /// user reads it (GET /api/collaborator-colors): a colour is not private, and a deactivated account's is left out.</summary>
    public static async Task<Dictionary<Guid, string>> ReadAllAsync(ISession session, CancellationToken ct)
    {
        var rows = await session.QueryOver<UserAppearancePreference>()
            .Where(x => x.ActiveTo == null && x.CollaboratorColor != "auto")
            .WithSubquery.WhereProperty(x => x.UserId).In(NHibernate.Criterion.QueryOver.Of<UserAccount>().Where(u => u.IsActive).Select(u => u.Id))
            .SelectList(list => list.Select(x => x.UserId).Select(x => x.CollaboratorColor))
            .ListAsync<object[]>(ct);
        return rows.Select(row => (UserId: (Guid)row[0], Slot: CollaboratorPalette.SlotOf((string)row[1])))
            .Where(row => row.Slot >= 0).DistinctBy(row => row.UserId)
            .ToDictionary(row => row.UserId, row => CollaboratorPalette.Entries[row.Slot].Light);
    }
}
