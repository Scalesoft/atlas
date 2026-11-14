using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts.LinkRun.Queries;

/// <summary>The report of the link migration (<c>report</c>): what <c>apply</c> would link, create and leave, without a write.</summary>
public sealed record ReportContactLinks;

/// <summary>The queued or running run of the job contacts.link, else the newest; null before the first.</summary>
public sealed record GetContactLinkRun;

public sealed class ContactLinkRunQueryHandler(ISession session, IContentAccess access, SpaceRoles roles, ContactLinkMigration migration)
{
    public async Task<ContactLinkReport> Handle(ReportContactLinks query, CancellationToken ct)
    {
        await RequireWikiAdminAsync(ct);
        return await migration.ReportAsync(ct);
    }

    public async Task<ContactLinkRunView?> Handle(GetContactLinkRun query, CancellationToken ct)
    {
        await RequireWikiAdminAsync(ct);
        return await ContactLinkRuns.NewestAsync(session, ct);
    }

    private async Task RequireWikiAdminAsync(CancellationToken ct) => await roles.RequireWikiAdminAsync(await access.LockActorAsync(null, ct), ct);
}
