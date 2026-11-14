using System.Text.Json.Nodes;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Features.PersonalItems.Queries;
using NHibernate;
using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using NHibernate.Criterion;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Objects.Queries;

/// <param name="Page">From 1; <see cref="ListObjects.PageSize"/> objects each.</param>
public sealed record ListObjectTrash(string? Type, string? Scope, Guid? SpaceId, int Page = 1);

/// <summary>
/// The trash of the objects the platform stores, newest first: only what the reader may restore (ObjectScopeAccess.MayManageTrashAsync,
/// here in SQL), with who trashed it and when. On MCP a space switched out of AI is left out (G5). Constant queries: the account, its
/// roles, a count, a page and the names of who trashed. A row carries its data without the long text (a note's Markdown, up to 100 000
/// characters), which becomes the excerpt Můj prostor shows (PERF3-1): the database sends no more than a row needs.
/// </summary>
public sealed class ListObjectTrashHandler
{
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly SpaceRoles m_roles;
    private readonly ObjectScopeAccess m_scopes;
    private readonly ObjectTypes m_types;
    private readonly IRequestChannel m_channel;

    public ListObjectTrashHandler(ISession session, IContentAccess access, SpaceRoles roles, ObjectScopeAccess scopes, ObjectTypes types, IRequestChannel channel)
    {
        m_session = session;
        m_access = access;
        m_roles = roles;
        m_scopes = scopes;
        m_types = types;
        m_channel = channel;
    }

    public async Task<ObjectTrashList> Handle(ListObjectTrash query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var types = m_types.StoredTypeIds().Where(type => query.Type is null || type == query.Type).ToList();
        var actor = await m_access.LockActorAsync(null, ct);
        if (types.Count == 0 || !actor.IsActive)
        {
            return new ObjectTrashList([], 0, page, ListObjects.PageSize);
        }
        var wikiAdmin = await m_roles.IsWikiAdminAsync(actor, ct);
        var wikiTypes = new List<string>();
        foreach (var type in types)
        {
            if (await m_scopes.MayWriteWikiAsync(new ObjectActor(actor), m_types.Find(type)!, ct))
            {
                wikiTypes.Add(type);
            }
        }
        var manageable = Restrictions.Disjunction()
            .Add(Restrictions.Where<ContentResource>(x => x.Scope == ObjectScopes.Private && x.OwnerId == actor.Id));
        if (wikiTypes.Count > 0)
        {
            manageable.Add(Restrictions.And(Restrictions.Where<ContentResource>(x => x.Scope == ObjectScopes.Wiki),
                Restrictions.In(Projections.Property<ContentResource>(x => x.TypeId), wikiTypes)));
        }
        var inSpace = Restrictions.Where<ContentResource>(x => x.Scope == ObjectScopes.Space);
        if (wikiAdmin)
        {
            manageable.Add(Restrictions.And(inSpace, Subqueries.WhereProperty<ContentResource>(x => x.SpaceId).In(LiveSpace.Ids())));
        }
        else if ((await m_roles.EffectiveAsync(actor, ct)).Where(role => role.Value >= SpaceRole.Admin).Select(role => role.Key).ToList() is { Count: > 0 } spaces)
        {
            // The roles cover only spaces outside the trash.
            manageable.Add(Restrictions.And(inSpace, Restrictions.In(Projections.Property<ContentResource>(x => x.SpaceId), spaces)));
        }
        var filter = m_session.QueryOver<ContentResource>()
            .Where(x => x.TrashedAt != null)
            .AndRestrictionOn(x => x.TypeId).IsIn(types)
            .And(manageable);
        if (query.Scope is { } scope)
        {
            filter = filter.And(x => x.Scope == scope);
        }
        if (query.SpaceId is { } spaceId)
        {
            filter = filter.And(x => x.SpaceId == spaceId);
        }
        if (m_channel.Current.IsAiBound)
        {
            filter = filter.And(Expression.Sql($$"""({alias}."SpaceId" IS NULL OR {{AccessSql.SpaceInAi("{alias}.\"SpaceId\"")}})"""));
        }
        var total = await filter.ToRowCountQuery().SingleOrDefaultAsync<int>(ct);
        IProjection[] columns =
        [
            Projections.Property<ContentResource>(x => x.Id), Projections.Property<ContentResource>(x => x.TypeId),
            Projections.Property<ContentResource>(x => x.Scope), Projections.Property<ContentResource>(x => x.SpaceId),
            Projections.Property<ContentResource>(x => x.Title), Projections.Property<ContentResource>(x => x.Version),
            Projections.Property<ContentResource>(x => x.TrashedAt), Projections.Property<ContentResource>(x => x.TrashedById),
            Projections.SqlProjection($$"""CAST({alias}."Data" - 'text' AS text) AS data, left({alias}."Data" ->> 'text', {{PersonalItemsQueryHandler.ExcerptSource + 1}}) AS excerpt""",
                ["data", "excerpt"], [NHibernateUtil.String, NHibernateUtil.String]),
        ];
        var rows = total <= (page - 1) * ListObjects.PageSize ? [] : await filter.Select(columns)
            .OrderBy(x => x.TrashedAt).Desc.ThenBy(x => x.Id).Asc
            .Skip((page - 1) * ListObjects.PageSize).Take(ListObjects.PageSize)
            .ListAsync<object[]>(ct);
        var trashers = rows.Select(row => (Guid?)row[7]).OfType<Guid>().Distinct().ToList();
        var names = trashers.Count == 0 ? new Dictionary<Guid, string>() : (await m_session.QueryOver<UserAccount>()
            .WhereRestrictionOn(x => x.Id).IsIn(trashers)
            .Select(x => x.Id, x => x.DisplayName)
            .ListAsync<object[]>(ct)).ToDictionary(x => (Guid)x[0], x => (string)x[1]);
        return new ObjectTrashList(rows.Select(row => new TrashedObjectDto((Guid)row[0], (string)row[1], (string)row[2], (Guid?)row[3], (string?)row[4] ?? "",
                Convert.ToInt64(row[5]), DateTime.SpecifyKind((DateTime)row[6], DateTimeKind.Utc), (Guid?)row[7], row[7] is Guid by ? names.GetValueOrDefault(by) : null,
                row[8] is string data ? JsonNode.Parse(data)!.AsObject() : null, PersonalItemsQueryHandler.Excerpt(row[9]))).ToArray(),
            total, page, ListObjects.PageSize);
    }
}
