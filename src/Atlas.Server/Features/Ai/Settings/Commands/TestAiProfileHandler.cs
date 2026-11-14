using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.ModuleSettings;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Commands;

/// <summary>The two short transactions around "Test spojení" (the YouTrackReadRunner pattern, split so the model call
/// runs between them).</summary>
public sealed class TestAiProfileHandler(ISession session, ModuleSettingsAccess access)
{
    public async Task<AiProfileSnapshot> Handle(BeginAiProfileTest command, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var kind = AiProfileRules.RequireKind(command.Kind);
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, kind, ct);
        var revision = assignment is null ? null : await session.GetAsync<AiProfileRevision>(assignment.RevisionId, ct);
        if (revision is null)
        {
            throw new ApiException(400, "Profil nejdřív uložte, test spojení ověřuje uložené nastavení.");
        }
        RequireTestable(revision);
        return AiProfileSnapshot.From(revision);
    }

    internal static void RequireTestable(AiProfileRevision revision)
    {
        if (revision.UseChatProfile)
        {
            throw new ApiException(400, "Rychlé úlohy používají profil Chat; otestujte ho v jeho sekci.");
        }
        if (revision.Model.Length == 0 || (AiProvider.UsesOpenAiProtocol(revision.Provider) && revision.BaseUrl is null))
        {
            throw new ApiException(400, "Pro test spojení vyplňte a uložte model a adresu API.");
        }
    }

    public async Task Handle(CompleteAiProfileTest command, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, AiProfileRules.RequireKind(command.Kind), ct);
        if (assignment?.RevisionId != command.RevisionId)
        {
            throw new ApiException(409, "Profil se během testu změnil. Spusťte test znovu.");
        }
    }
}
