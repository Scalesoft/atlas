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
}
