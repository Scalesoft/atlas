using Atlas.Server.Features.Setup.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.ModuleSettings.Commands;

public sealed class CompleteModuleSetupHandler
{
    private readonly ISession m_session;
    private readonly ModuleSettingsAccess m_access;

    public CompleteModuleSetupHandler(ISession session, ModuleSettingsAccess access)
    {
        m_session = session;
        m_access = access;
    }

    public async Task Handle(CompleteModuleSetup command, CancellationToken ct)
    {
        var actor = await m_access.RequireWikiAdminAsync(ct);
        // Set based like the UPDATE it replaces: whatever is open gets closed, not just the first row.
        var pending = await m_session.QueryOver<ModuleOnboarding>().Where(x => x.ActiveTo == null).ListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var row in pending)
        {
            // The CHECK constraint binds the closing timestamp and the author together.
            row.ActiveTo = Temporal.NextBoundary(row.ActiveFrom, now);
            row.CompletedById = actor.Id;
        }
    }
}
