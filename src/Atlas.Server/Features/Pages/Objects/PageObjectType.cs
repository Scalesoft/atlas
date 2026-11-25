using System.Text.Json;
using Atlas.Server.Features.AiIndex.Domain;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Platform.Objects;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Objects;

/// <summary>
/// The page as an object (<c>atlas.page</c>, objects spec §2.4): one Yjs text edited through the page paths. Access is the page
/// decision of every point-of-use path (Read on the page; Write when it may edit it). Its projection is the published revision
/// as search reads it: secret references stripped, headings anchored as <c>SearchSqlText.Headings</c> reports them, chunked as
/// the AI index chunks pages.
/// </summary>
public sealed class PageObjectType : IObjectType
{
    private readonly ISession m_session;
    private readonly ResourceGrantSnapshots m_grants;
    private readonly SpaceRoles m_roles;
    private readonly IRequestChannel m_channel;

    public PageObjectType(ISession session, ResourceGrantSnapshots grants, SpaceRoles roles, IRequestChannel channel)
    {
        m_session = session;
        m_grants = grants;
        m_roles = roles;
        m_channel = channel;
    }

    public string TypeId => PagesAtlasModule.ContentType;

    public ObjectEditingModel EditingModel => ObjectEditingModel.YjsText;

    public async Task<ObjectAccess> AuthorizeAsync(ObjectActor actor, Guid objectId, CancellationToken ct)
    {
        var page = await m_session.GetAsync<Page>(objectId, ct);
        // G5 (D10), as ObjectScopeAccess: a page of a space excluded from AI and MCP is missing to a channel whose content may go to a model.
        if (page is null || page.IsTrashed || m_channel.Current.IsAiBound && !await m_roles.InAiAsync(page.SpaceId, ct))
        {
            return ObjectAccess.None;
        }
        var decision = await m_grants.AuthorizePageAsync(actor.Account, page, AccessOperation.ReadContent, ct);
        return !decision.Allowed ? ObjectAccess.None : decision.CanEdit ? ObjectAccess.Write : ObjectAccess.Read;
    }

    /// <summary>Null for a missing, trashed or never published page and for a template.</summary>
    public async Task<ObjectProjection?> ProjectAsync(Guid objectId, CancellationToken ct)
    {
        var row = await m_session.CreateSQLQuery(Search.SearchDocuments.NoJit + $"""
            SELECT r."Title" AS title, s.markdown, CAST({SearchSqlText.Headings("s.markdown", "r.\"ContentJson\"")} AS text) AS headings
            FROM "Page" p
            JOIN "PageRevision" r ON r."Id" = p."PublishedRevisionId"
            CROSS JOIN LATERAL (SELECT {SearchSqlText.StripSecrets("r.\"Markdown\"")} AS markdown) s
            WHERE p."Id" = :id AND NOT p."IsTrashed" AND NOT p."IsTemplate"
            """)
            .AddScalar("title", NHibernateUtil.String).AddScalar("markdown", NHibernateUtil.StringClob).AddScalar("headings", NHibernateUtil.StringClob)
            .SetParameter("id", objectId)
            .UniqueResultAsync<object[]?>(ct);
        if (row is null)
        {
            return null;
        }
        var (title, markdown) = ((string)row[0], (string)row[1]);
        var chunks = KnowledgeChunker.Chunk(title, markdown, Headings((string?)row[2]), "")
            .Select(chunk => new ObjectChunk(chunk.HeadingId, chunk.HeadingTitle ?? "", chunk.Text))
            .ToArray();
        return new ObjectProjection(title, markdown, Markdig.Markdown.ToPlainText(markdown).Trim(), chunks);
    }

    private static IReadOnlyList<IndexHeading> Headings(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
            .Select(item => new IndexHeading(item.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                item.TryGetProperty("headingId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null))
            .ToArray();
    }
}
