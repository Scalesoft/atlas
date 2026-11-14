using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Objects;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Boards;

/// <summary>
/// The board as an object (<c>atlas.board</c>), changed by typed operations (BoardOps). Access is the space role, as BoardAccess
/// decides it: Reader reads, Writer writes, a WikiAdmin is Admin; a page's own board (Purpose) by its page: its editors write, its readers read the page's board (Board.PagePurpose); a board under a page with its own access also needs that page's read (its edit to write). Its projection is the title and the searchable text of its
/// live items (secret references stripped), one chunk per item anchored at the item.
/// </summary>
public sealed class BoardObjectType : IObjectType
{
    private readonly ISession m_session;
    private readonly SpaceRoles m_roles;
    private readonly ResourceGrantSnapshots m_grants;

    public BoardObjectType(ISession session, SpaceRoles roles, ResourceGrantSnapshots grants)
    {
        m_session = session;
        m_roles = roles;
        m_grants = grants;
    }

    public string TypeId => BoardsAtlasModule.ContentType;

    public ObjectEditingModel EditingModel => ObjectEditingModel.BoardOps;

    public async Task<ObjectAccess> AuthorizeAsync(ObjectActor actor, Guid objectId, CancellationToken ct)
    {
        var board = await m_session.GetAsync<Board>(objectId, ct);
        if (board is null || board.IsTrashed)
        {
            return ObjectAccess.None;
        }
        if (board.Purpose is not null)
        {
            // A page's own board is decided by its page, whatever the space role (BoardAccess).
            var page = await m_session.GetAsync<Page>(board.ParentPageId!.Value, ct);
            var decision = page is null || page.IsTrashed ? null : await m_grants.AuthorizePageAsync(actor.Account, page, AccessOperation.ReadContent, ct);
            return decision switch
            {
                { Allowed: true, CanEdit: true } => ObjectAccess.Write,
                { Allowed: true } when board.Purpose == Board.PagePurpose => ObjectAccess.Read,
                _ => ObjectAccess.None
            };
        }
        var role = await m_roles.IsWikiAdminAsync(actor.Account, ct) ? SpaceRole.Admin : await m_roles.EffectiveAsync(actor.Account, board.SpaceId, ct);
        var access = role switch
        {
            null => ObjectAccess.None,
            >= SpaceRole.Writer => ObjectAccess.Write,
            _ => ObjectAccess.Read
        };
        // Under a page with its own access, the page's read and edit as well (BoardAccess, M1).
        if (access != ObjectAccess.None && board.ParentPageId is { } parentId && await m_session.GetAsync<Page>(parentId, ct) is { OwnAccess: true } restricted)
        {
            var decision = await m_grants.AuthorizePageAsync(actor.Account, restricted, AccessOperation.ReadContent, ct);
            return !decision.Allowed ? ObjectAccess.None : decision.CanEdit ? access : ObjectAccess.Read;
        }
        return access;
    }

    /// <summary>Null for a missing or trashed board.</summary>
    public async Task<ObjectProjection?> ProjectAsync(Guid objectId, CancellationToken ct)
    {
        var rows = await m_session.CreateSQLQuery($"""
            SELECT b."Title" AS title, i."Id" AS item, {SearchSqlText.StripSecrets("i.\"SearchText\"")} AS text
            FROM "Board" b
            LEFT JOIN "BoardItem" i ON i."BoardId" = b."Id" AND NOT i."IsDeleted" AND i."SearchText" IS NOT NULL
            WHERE b."Id" = :id AND NOT b."IsTrashed"
            ORDER BY i."Z", i."Id"
            """)
            .AddScalar("title", NHibernateUtil.String).AddScalar("item", NHibernateUtil.Guid).AddScalar("text", NHibernateUtil.StringClob)
            .SetParameter("id", objectId)
            .ListAsync<object[]>(ct);
        if (rows.Count == 0)
        {
            return null;
        }
        var title = (string)rows[0][0];
        var items = rows.Where(row => row[1] is Guid).Select(row => (Id: (Guid)row[1], Markdown: (string)row[2])).ToArray();
        var chunks = items.Select(item => new ObjectChunk(item.Id.ToString("D"), title, Markdig.Markdown.ToPlainText(item.Markdown).Trim())).ToArray();
        return new ObjectProjection(title, string.Join("\n\n", items.Select(item => item.Markdown)), string.Join("\n\n", chunks.Select(chunk => chunk.Text)), chunks);
    }
}
