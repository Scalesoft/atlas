using NHibernate.Criterion;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Access.Domain;

/// <summary>Page titles for the access lists, under the private draft rule: another owner's unpublished page is shown only to a
/// WikiAdmin (PageVisibility), so neither its title nor its existence reaches a space Admin through a card.</summary>
public static class AccessPageTitles
{
    public static async Task<IReadOnlyDictionary<Guid, SpacePageEntry>> EntriesAsync(ISession session, IReadOnlyCollection<Guid> ids,
        CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, SpacePageEntry>();
        }
        return (await session.QueryOver<SpacePageEntry>()
            .Where(GrantQueryLimits.InChunks(Projections.Property<SpacePageEntry>(p => p.Id), ids.Distinct().ToArray()))
            .ListAsync(ct)).ToDictionary(p => p.Id);
    }

    /// <summary>The published title of a published page, the working one of a draft.</summary>
    public static string Title(SpacePageEntry page) => (page.IsPublished ? page.PublishedTitle : page.DraftTitle) ?? page.DraftTitle ?? "";

    public static bool Visible(SpacePageEntry page, UserAccount viewer, bool wikiAdmin) => page.IsPublished || page.OwnerId == viewer.Id || wikiAdmin;

    /// <summary>The visible parent's title, the "Architektura" of "Architektura › Servery".</summary>
    public static string? ParentTitle(SpacePageEntry page, IReadOnlyDictionary<Guid, SpacePageEntry> parents, UserAccount viewer, bool wikiAdmin)
    {
        return page.ParentId is { } id && parents.TryGetValue(id, out var parent) && Visible(parent, viewer, wikiAdmin) ? Title(parent) : null;
    }
}
