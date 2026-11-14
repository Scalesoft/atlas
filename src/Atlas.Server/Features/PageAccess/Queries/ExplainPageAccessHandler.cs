using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageAccess.Queries;

public sealed record ExplainPageAccess(Guid PageId, Guid UserId);

public sealed record GetPageAccessCandidates(Guid PageId, string Kind, string? Query);

/// <summary>Management reads of the access tab: "Zobrazit jako" and the user/group directory of the add wizard. Both need
/// ManageAccess on the page (a reader is told 403, as for the access history), so the directory is never disclosed to readers.</summary>
public sealed class ExplainPageAccessHandler(ISession session, PageReader reader, IContentAccess access, ResourceGrantSnapshots grants)
{
    private const int CandidateLimit = 20;

    /// <summary>The decision the policy makes for that account (ResourceGrantSnapshots, the path of the collaboration relay and the
    /// share link issuer), with the sources explaining it.</summary>
    public async Task<PageAccessExplanation> Handle(ExplainPageAccess query, CancellationToken ct)
    {
        var page = await RequireManagedAsync(query.PageId, ct);
        var subject = await session.GetAsync<UserAccount>(query.UserId, ct) ?? throw new ApiException(404, "Uživatel nebyl nalezen.");
        var person = (await PageAccessDirectory.PersonAsync(session, page, subject.Id, ct))!;
        var read = await grants.AuthorizePageAsync(subject, page, AccessOperation.ReadContent, ct);
        string? denial = null;
        if (!read.Allowed)
        {
            denial = !subject.IsActive ? "inactive"
                : !PageOrganization.Domain.PageVisibility.IsVisible(page, subject.Id, await EffectiveWikiAdmin.IsAdminAsync(session, subject, ct)) ? "privateDraft"
                : page.OwnAccess && await PageAccessDirectory.DefaultRoleAsync(session, page, subject.Id, ct) is not null ? "ownAccess"
                : "none";
        }
        return new PageAccessExplanation(subject.Id, subject.DisplayName, read.Allowed, read.Allowed && read.CanEdit,
            read.Allowed ? person.Role : null, person.Sources, denial);
    }

    public async Task<IReadOnlyList<AccessCandidate>> Handle(GetPageAccessCandidates query, CancellationToken ct)
    {
        if (query.Kind is not ("user" or "group") || query.Query?.Length > 200)
        {
            throw new ApiException(400, "Vyberte uživatele nebo skupinu a hledejte nejvýše 200 znaků.");
        }
        await RequireManagedAsync(query.PageId, ct);
        return await PageAccessDirectory.CandidatesAsync(session, query.Kind, query.Query?.Trim() ?? "", CandidateLimit, ct);
    }

    private Task<Page> RequireManagedAsync(Guid pageId, CancellationToken ct)
    {
        return PageAccessGate.RequireAsync(reader, session, access, pageId, AccessOperation.ManageAccess, ct);
    }
}
