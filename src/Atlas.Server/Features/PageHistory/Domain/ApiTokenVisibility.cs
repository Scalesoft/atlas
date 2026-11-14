using Atlas.Server.Features.PersonalApi.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageHistory.Domain;

/// <summary>
/// Who may see a personal API token's name next to a change, publication or opening: its owner and wiki administrators (D4).
/// Everyone else gets the channel only; the server decides, so the DTO for them carries no token at all. One place for
/// History, Recent publications and Access history.
/// </summary>
public static class ApiTokenVisibility
{
    public static bool CanSeeName(UserAccount actor, bool actorIsWikiAdmin, Guid tokenOwnerId) => actorIsWikiAdmin || actor.Id == tokenOwnerId;

    /// <summary>The tokens among <paramref name="tokenIds"/> the actor may see by name; the others are absent from the result.
    /// The wiki administrator check runs only when a token belongs to someone else.</summary>
    public static async Task<IReadOnlyDictionary<Guid, ApiTokenRef>> VisibleAsync(ISession session, UserAccount actor, IEnumerable<Guid?> tokenIds, CancellationToken ct)
    {
        var ids = tokenIds.OfType<Guid>().Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, ApiTokenRef>();
        }
        var rows = await session.QueryOver<PersonalApiToken>().WhereRestrictionOn(x => x.Id).IsIn(ids)
            .Select(x => x.Id, x => x.UserId, x => x.Name, x => x.ActiveTo)
            .ListAsync<object[]>(ct);
        var admin = rows.Any(row => (Guid)row[1] != actor.Id) && await EffectiveWikiAdmin.IsAdminAsync(session, actor, ct);
        return rows.Where(row => CanSeeName(actor, admin, (Guid)row[1]))
            .ToDictionary(row => (Guid)row[0], row => new ApiTokenRef((Guid)row[0], (string)row[2], row[3] is not null));
    }

    public static ApiTokenRef? Find(this IReadOnlyDictionary<Guid, ApiTokenRef> visible, Guid? tokenId)
        => tokenId is { } id && visible.TryGetValue(id, out var token) ? token : null;
}
