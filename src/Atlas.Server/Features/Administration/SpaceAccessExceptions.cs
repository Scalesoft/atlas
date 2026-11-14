using NHibernate.Criterion;
using Atlas.Server.Features.Access.Contracts;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Features.Vaults.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Administration;

/// <summary>
/// What inside a space differs from the whole space ("Výjimky"): pages with own access or with grants of their own (a page grant
/// applies with or without own access, D3), and vaults and secrets with grants of their own. Trashed pages, archived vaults and
/// removed secrets decide nothing and are left out. Another owner's unpublished draft stays hidden from a space Admin, as the
/// private draft rule hides it everywhere else; a WikiAdmin sees it.
/// </summary>
internal static class SpaceAccessExceptions
{
    public static async Task<IReadOnlyList<AccessExceptionItem>> ReadAsync(ISession session, UserAccount viewer, Guid spaceId, CancellationToken ct)
    {
        var wikiAdmin = await EffectiveWikiAdmin.IsAdminAsync(session, viewer, ct);
        var own = await session.QueryOver<Page>().Where(p => p.SpaceId == spaceId && p.OwnAccess && !p.IsTrashed)
            .Select(p => p.Id).Take(AccessGrantRows.Limit).ListAsync<Guid>(ct);
        var spacePages = QueryOver.Of<Page>().Where(p => p.SpaceId == spaceId && !p.IsTrashed).Select(p => p.Id);
        var pageGrants = (await session.QueryOver<ResourceGrant>().Where(g => g.ActiveTo == null)
            .And(Subqueries.PropertyIn("PageId", spacePages.DetachedCriteria))
            .SelectList(list => list.SelectGroup(g => g.PageId).SelectCount(g => g.Id))
            .Take(AccessGrantRows.Limit)
            .ListAsync<object[]>(ct)).ToDictionary(row => (Guid)row[0], row => Convert.ToInt32(row[1]));
        var ids = own.Concat(pageGrants.Keys).Distinct().ToArray();
        var pages = await AccessPageTitles.EntriesAsync(session, ids, ct);
        var parents = await AccessPageTitles.EntriesAsync(session, pages.Values.Select(p => p.ParentId).OfType<Guid>().ToArray(), ct);
        var ownAccess = own.ToHashSet();
        var items = pages.Values.Where(page => AccessPageTitles.Visible(page, viewer, wikiAdmin)).Select(page => new AccessExceptionItem("page", page.Id,
                AccessPageTitles.Title(page), AccessPageTitles.ParentTitle(page, parents, viewer, wikiAdmin), ownAccess.Contains(page.Id) ? "own" : "grants",
                pageGrants.GetValueOrDefault(page.Id)))
            .OrderBy(item => item.Title, StringComparer.CurrentCulture).ToList();

        if (!viewer.IsWikiAdmin && await new SpaceRoles(session).EffectiveAsync(viewer, spaceId, ct) != SpaceRole.Admin)
        {
            return items;
        }
        var vaultGrants = await session.QueryOver<ResourceGrant>().Where(g => g.ActiveTo == null && g.VaultSpaceId == spaceId)
            .SelectList(list => list.SelectGroup(g => g.VaultId).SelectGroup(g => g.SecretId).SelectCount(g => g.Id))
            .ListAsync<object[]>(ct);
        var vaults = (await session.QueryOver<Vault>().Where(v => v.SpaceId == spaceId && v.ArchivedAt == null).ListAsync(ct)).ToDictionary(v => v.Id);
        var secretIds = vaultGrants.Select(row => row[1]).OfType<Guid>().Distinct().ToArray();
        var secrets = secretIds.Length == 0 ? new Dictionary<Guid, VaultSecret>() : (await session.QueryOver<VaultSecret>()
            .Where(s => s.RemovedAt == null)
            .And(GrantQueryLimits.InChunks(Projections.Property<VaultSecret>(s => s.Id), secretIds))
            .ListAsync(ct)).ToDictionary(s => s.Id);
        foreach (var row in vaultGrants.Where(row => vaults.ContainsKey((Guid)row[0])).OrderBy(row => row[1] is null ? 0 : 1))
        {
            var vault = vaults[(Guid)row[0]];
            var count = Convert.ToInt32(row[2]);
            if (row[1] is not Guid secretId)
            {
                items.Add(new AccessExceptionItem("vault", vault.Id, vault.Name, null, "grants", count));
            }
            else if (secrets.TryGetValue(secretId, out var secret))
            {
                items.Add(new AccessExceptionItem("secret", secret.Id, secret.Title, vault.Name, "grants", count));
            }
        }
        return items;
    }
}
