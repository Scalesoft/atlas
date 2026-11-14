using Atlas.Server.Features.Vaults.References;
using Atlas.Server.Features.PageHistory.Contracts;
using Atlas.Server.Features.PageHistory.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageHistory.Queries;

public sealed class GetPageRevisionHandler
{
    private readonly ISession m_session;
    private readonly PageHistoryAccess m_pages;
    private readonly SecretReferenceAccess m_references;

    public GetPageRevisionHandler(ISession session, PageHistoryAccess pages, SecretReferenceAccess references)
    {
        m_session = session;
        m_pages = pages;
        m_references = references;
    }

    public async Task<PageHistoryRevision> Handle(GetPageRevision query, CancellationToken ct)
    {
        var (page, actor) = await m_pages.RequirePageAsync(query.Id, false, ct);
        var revision = await m_pages.FindRevisionAsync(query.Id, query.RevisionId, ct);
        var author = await m_session.QueryOver<UserAccount>().Where(x => x.Id == revision.AuthorId)
            .Select(x => x.DisplayName).SingleOrDefaultAsync<string>(ct);
        var last = await m_pages.LastPublishedAsync(query.Id, query.RevisionId, ct);
        var tokens = await ApiTokenVisibility.VisibleAsync(m_session, actor, [revision.ApiTokenId, last?.ApiTokenId], ct);
        return new PageHistoryRevision(revision.Id, query.Id, revision.Version, revision.Title, revision.AuthorId, author,
            revision.ActiveFrom, revision.ActiveTo, revision.Version == page.Version,
            revision.Id == page.PublishedRevisionId, last?.At, (await m_references.ForReaderAsync(actor, revision.ContentJson, ct)).Json,
            // As today's writer reads it: an old revision stored in an older dialect shows no change against its successor.
            await m_references.MarkdownForReaderAsync(actor, RevisionMarkdown.Of(revision), ct))
        {
            ChangeSummary = last?.Summary,
            Channel = ContentChannels.ForRead(revision.Channel),
            ApiToken = tokens.Find(revision.ApiTokenId),
            PublicationChannel = ContentChannels.ForRead(last?.Channel),
            PublicationApiToken = tokens.Find(last?.ApiTokenId),
            GroupId = revision.GroupId,
            Label = revision.Label,
            IsAi = RevisionVersions.IsAi(revision)
        };
    }
}
