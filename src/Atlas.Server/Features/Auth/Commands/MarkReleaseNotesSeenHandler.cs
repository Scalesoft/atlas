using System.Text.RegularExpressions;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Commands;

public sealed partial class MarkReleaseNotesSeenHandler
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_actor;

    public MarkReleaseNotesSeenHandler(ISession session, ICurrentActor actor)
    {
        m_session = session;
        m_actor = actor;
    }

    public async Task Handle(MarkReleaseNotesSeen command, CancellationToken ct)
    {
        var user = await m_actor.RequireAsync(ct);
        // Only a released version has notes; a development build ("3.5.0-dev") never asks.
        if (command.Version is null || !Release().IsMatch(command.Version))
        {
            throw new ApiException(400, "Neplatná verze.");
        }
        await m_session.CreateSQLQuery("""
            INSERT INTO "UserReleaseNotes"("UserId","SeenVersion") VALUES (:user,:version)
            ON CONFLICT ("UserId") DO UPDATE SET "SeenVersion" = EXCLUDED."SeenVersion"
            """)
            .SetParameter("user", user.Id).SetParameter("version", command.Version)
            .ExecuteUpdateAsync(ct);
    }

    [GeneratedRegex(@"^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}\z")]
    private static partial Regex Release();
}
