using System.Text.Json.Nodes;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using NHibernate;
using NHibernate.Criterion;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Objects.Queries;

/// <summary>
/// The object library (objects spec §2.3): objects of the types the platform stores, filtered in SQL by the rules of
/// ObjectScopeAccess for the reader, so neither a row nor the total shows what it may not read. The text matches the title or any
/// field's value (never a field's name). Pages and boards have their own library (the tree, favorites and search). Constant
/// queries: the account, its WikiAdmin state and space roles, a count, a page.
/// </summary>
// shortcut: the text is matched by a scan of the readable rows' fields (ILIKE over jsonb_each_text); add a trigram index over a
// stored search text when a directory grows past some ten thousand objects.
public sealed class ListObjectsHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly SpaceRoles m_roles;
    private readonly ObjectScopeAccess m_scopes;
    private readonly ObjectTypes m_types;

    public ListObjectsHandler(ISession session, IContentAccess access, SpaceRoles roles, ObjectScopeAccess scopes, ObjectTypes types)
    {
        m_session = session;
        m_access = access;
        m_roles = roles;
        m_scopes = scopes;
        m_types = types;
    }

    public async Task<ObjectList> Handle(ListObjects query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var types = m_types.StoredTypeIds().Where(type => query.Type is null || type == query.Type).ToArray();
        var actor = await m_access.LockActorAsync(null, ct);
        if (types.Length == 0 || !actor.IsActive)
        {
            return new ObjectList([], 0, page, ListObjects.PageSize);
        }
        var wikiAdmin = await m_roles.IsWikiAdminAsync(actor, ct);
        IReadOnlyDictionary<Guid, SpaceRole> roles = wikiAdmin ? new Dictionary<Guid, SpaceRole>() : await m_roles.EffectiveAsync(actor, ct);
        var readable = Restrictions.Disjunction()
            .Add(Restrictions.Where<ContentResource>(x => x.Scope == ObjectScopes.Wiki))
            .Add(Restrictions.Where<ContentResource>(x => x.Scope == ObjectScopes.Private && x.OwnerId == actor.Id));
        var inSpace = Restrictions.Where<ContentResource>(x => x.Scope == ObjectScopes.Space);
        if (wikiAdmin)
        {
            readable.Add(Restrictions.And(inSpace, Subqueries.WhereProperty<ContentResource>(x => x.SpaceId).In(LiveSpace.Ids())));
        }
        else if (roles.Keys.ToArray() is { Length: > 0 } spaces)
        {
            // The roles cover only spaces outside the trash.
            readable.Add(Restrictions.And(inSpace, Restrictions.In(Projections.Property<ContentResource>(x => x.SpaceId), spaces)));
        }
        var filter = m_session.QueryOver<ContentResource>()
            .Where(x => x.TrashedAt == null)
            .AndRestrictionOn(x => x.TypeId).IsIn(types)
            .And(readable);
        if (query.Scope is { } scope)
        {
            filter = filter.And(x => x.Scope == scope);
        }
        if (query.SpaceId is { } spaceId)
        {
            filter = filter.And(x => x.SpaceId == spaceId);
        }
        // Every typed word somewhere in the title or a field, diacritics and case aside („kovarik“ finds „Kovařík“).
        foreach (var word in SearchText.LookupWords(query.Q))
        {
            var pattern = "%" + Escape(word) + "%";
            filter = filter.And(Expression.Sql("""(lower(public.unaccent({alias}."Title")) LIKE ? OR EXISTS (SELECT 1 FROM jsonb_each_text({alias}."Data") field WHERE lower(public.unaccent(field.value)) LIKE ?))""",
                [pattern, pattern], [NHibernateUtil.String, NHibernateUtil.String]));
        }
        if (query.Names is { Count: > 0 } names)
        {
            var wanted = names.Select(name => name.Trim().ToLowerInvariant()).Distinct().Take(ListObjects.PageSize).ToArray();
            filter = filter.And(Expression.Sql($$"""lower(btrim({alias}."Title")) IN ({{string.Join(", ", wanted.Select(_ => "?"))}})""",
                wanted, wanted.Select(_ => (NHibernate.Type.IType)NHibernateUtil.String).ToArray()));
        }
        var total = await filter.ToRowCountQuery().SingleOrDefaultAsync<int>(ct);
        var rows = total <= (page - 1) * ListObjects.PageSize ? [] : await filter
            .OrderBy(x => x.UpdatedAt).Desc.ThenBy(x => x.Id).Asc
            .Skip((page - 1) * ListObjects.PageSize).Take(ListObjects.PageSize)
            .ListAsync(ct);
        return new ObjectList(rows.Select(row => new ObjectSummaryDto(row.Id, row.TypeId, row.Scope, row.SpaceId, row.Title ?? "", row.Version, row.UpdatedAt,
                row.Data is { } data ? JsonNode.Parse(data)!.AsObject() : null)).ToArray(),
            total, page, ListObjects.PageSize, await CanCreateAsync(actor, query, wikiAdmin, roles, ct));
    }

    /// <summary>The decision of CreateObjectHandler for the one type and scope listed.</summary>
    private async Task<bool> CanCreateAsync(UserAccount actor, ListObjects query, bool wikiAdmin, IReadOnlyDictionary<Guid, SpaceRole> roles, CancellationToken ct)
    {
        if (query.Type is null || m_types.Find(query.Type) is not { } type || type is not IObjectChangeHandler)
        {
            return false;
        }
        return query.Scope switch
        {
            ObjectScopes.Wiki => await m_scopes.MayWriteWikiAsync(new ObjectActor(actor), type, ct),
            ObjectScopes.Private => true,
            ObjectScopes.Space when query.SpaceId is { } spaceId => wikiAdmin || roles.GetValueOrDefault(spaceId) >= SpaceRole.Writer,
            _ => false
        };
    }

    // PostgreSQL's ILIKE escapes with a backslash by default.
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", @"\%").Replace("_", @"\_");
}
