using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.Pages.Commands;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Queries;

/// <param name="Mode">The page's own „Publikace“ (Manual, Automatic, Live), null when it inherits <paramref name="SpaceMode"/>.</param>
/// <param name="Effective">The mode that applies.</param>
/// <param name="PublishedVersion">The version of the last publication, null when the page was never published (a private new page).</param>
/// <param name="PublishedAutomatically">Whether the last publication was automatic (<see cref="Commands.AutoPublishHandler"/>).</param>
/// <param name="UnpublishedChanges">Whether something was saved since the last publication.</param>
/// <param name="PublishedRevisionId">The revision readers read (the writer's „Publikovaná“ view opens it from the history), null when never published.</param>
public sealed record PagePublicationState(string? Mode, string SpaceMode, string Effective, int? PublishedVersion, DateTime? PublishedAt,
    string? PublishedBy, bool PublishedAutomatically, bool UnpublishedChanges, bool CanChange, Guid? PublishedRevisionId);

/// <param name="Mode">Manual, Automatic or Live; null to inherit the space's.</param>
public sealed record SetPagePublicationRequest(string? Mode);

public sealed record GetPagePublication(Guid PageId);

public sealed class PagePublicationSettingHandler(ISession session, PageGate gate, OrganizationLocks locks, AccessChanges accessChanges)
{
    public async Task<PagePublicationState> Handle(GetPagePublication query, CancellationToken ct)
    {
        var opened = (await gate.ReadAsync(query.PageId, AccessOperation.ReadContent, ct)).Require();
        return await ReadAsync(opened.Page, opened.CanEdit, ct);
    }

    /// <summary>Anyone who may edit the page may change it (a readable page without EditContent answers 403, anything else 404).</summary>
    public async Task<PagePublicationState> Handle(SetPagePublication command, CancellationToken ct)
    {
        var (page, _) = await locks.LockPageAsync(command.PageId, false, AccessOperation.EditContent, ct);
        var mode = PublicationModes.ParseOrInherit(command.Request.Mode);
        // Živě decides who reads the live document: its readers are re-authorized at the commit, not at the next periodic refresh.
        if (mode != page.PublicationMode) accessChanges.Changed();
        page.PublicationMode = mode;
        await session.FlushAsync(ct);
        return await ReadAsync(page, true, ct);
    }

    private async Task<PagePublicationState> ReadAsync(Page page, bool canChange, CancellationToken ct)
    {
        var spaceMode = (await session.GetAsync<Spaces.Domain.Space>(page.SpaceId, ct))?.PublicationMode ?? PublicationMode.Manual;
        var last = await session.QueryOver<PagePublication>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct);
        // Projections: a revision row holds the whole page.
        int? publishedVersion = last is null ? null
            : await session.QueryOver<PageRevision>().Where(x => x.Id == last.RevisionId).Select(x => x.Version).SingleOrDefaultAsync<int>(ct);
        var publisher = last?.PublisherId is { } publisherId ? await session.GetAsync<UserAccount>(publisherId, ct) : null;
        var latest = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).Select(x => x.Id)
            .SingleOrDefaultAsync<Guid>(ct);
        return new PagePublicationState(page.PublicationMode?.ToString(), spaceMode.ToString(), (page.PublicationMode ?? spaceMode).ToString(),
            publishedVersion, last?.ActiveFrom, publisher?.DisplayName, last?.Channel == ContentChannels.System, last?.RevisionId != latest, canChange, last?.RevisionId);
    }
}
