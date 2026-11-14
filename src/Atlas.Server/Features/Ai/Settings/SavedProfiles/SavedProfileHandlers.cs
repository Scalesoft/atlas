using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.ModuleSettings;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.SavedProfiles;

public sealed class SavedProfileHandler(ISession session, ModuleSettingsAccess access, AiSettingsWrite write,
    SavedProfileStore store, IEmbeddingChangeGuard index, IDataProtectionProvider protection)
{
    public async Task<SavedProfileList> Handle(ListSavedProfiles query, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        return await store.ListAsync(AiProfileRules.RequireKind(query.Kind), ct);
    }

    public async Task<SavedProfileView> Handle(GetSavedProfile query, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var row = await store.RequireAsync(AiProfileRules.RequireKind(query.Kind), query.Id, ct);
        return await ViewAsync(row, ct);
    }

    public async Task<SavedProfileView> Handle(SaveSavedProfile command, CancellationToken ct)
    {
        var actor = await write.BeginAsync(ct);
        var kind = AiProfileRules.RequireKind(command.Kind);
        var previous = command.Id is { } id ? await store.RequireAsync(kind, id, ct) : null;
        AdministrationVersion.Expect(previous?.ActiveFrom, command.Request.ExpectedVersion);
        var name = await store.RequireNameAsync(command.Request.Name, kind, command.Id, ct);
        var request = command.Request.Profile ?? throw new ApiException(400, "Vyplňte nastavení profilu.");
        var revision = AiProfileRules.Draft(kind, request);
        var previousRevision = previous is null ? null : await store.RevisionAsync(previous, ct);
        AiProfileRules.ApplyKey(revision, previousRevision, request.ApiKey, request.ClearApiKey,
            protection.CreateProtector(SaveAiProfileHandler.ApiKeyPurpose));
        AiProfileRules.RequireUsable(revision);
        revision.Id = Guid.CreateVersion7();
        revision.CreatedById = actor.Id;
        revision.CreatedAt = DateTime.UtcNow;
        await session.SaveAsync(revision, ct);
        var row = await store.AppendAsync(previous, kind, name, revision.Id, actor.Id, ct);
        // No configuration cache invalidation: the running assignments have not changed.
        return SavedProfileStore.View(row, revision, await CurrentAiSettings.LoadAssignmentAsync(session, kind, ct));
    }

    public async Task<SavedProfileView> Handle(SnapshotCurrentProfile command, CancellationToken ct)
    {
        var actor = await write.BeginAsync(ct);
        var kind = AiProfileRules.RequireKind(command.Kind);
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, kind, ct);
        AdministrationVersion.Expect(assignment?.ActiveFrom, command.Request.ExpectedVersion);
        if (assignment is null) throw new ApiException(400, "Aktuální připojení nejdřív nastavte.");
        var name = await store.RequireNameAsync(command.Request.Name, kind, null, ct);
        var row = await store.AppendAsync(null, kind, name, assignment.RevisionId, actor.Id, ct);
        return await ViewAsync(row, ct);
    }

    public async Task<bool> Handle(DeleteSavedProfile command, CancellationToken ct)
    {
        await write.BeginAsync(ct);
        var kind = AiProfileRules.RequireKind(command.Kind);
        var row = await store.RequireAsync(kind, command.Id, ct);
        AdministrationVersion.Expect(row.ActiveFrom, command.ExpectedVersion);
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, kind, ct);
        if (await store.IsActiveLineageAsync(row, assignment, ct))
            throw new ApiException(409, "Tento profil se právě používá. Nejdřív použijte jiné připojení.", "ai_profile_active");
        row.ActiveTo = Temporal.NextBoundary(row.ActiveFrom, DateTime.UtcNow);
        return true;
    }

    public async Task<AiProfileView> Handle(ActivateSavedProfile command, CancellationToken ct)
    {
        var actor = await write.BeginAsync(ct);
        var kind = AiProfileRules.RequireKind(command.Kind);
        var row = await store.RequireAsync(kind, command.Id, ct);
        AdministrationVersion.Expect(row.ActiveFrom, command.Request.ExpectedSavedVersion);
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, kind, ct);
        AdministrationVersion.Expect(assignment?.ActiveFrom, command.Request.ExpectedVersion);
        var revision = await store.RevisionAsync(row, ct);
        AiProfileRules.RequireUsable(revision);
        var fingerprint = kind == AiProfileKind.Embeddings ? await index.ActiveFingerprintAsync(ct) : null;
        if (fingerprint is not null && !string.Equals(fingerprint, revision.Fingerprint, StringComparison.Ordinal))
            throw new ApiException(409, "Změna modelu vyžaduje novou generaci indexu. Použijte Index → Vyměnit model.", "ai_generation_required");
        if (assignment?.RevisionId == revision.Id)
            return CurrentAiSettings.ToView(kind, assignment, revision, fingerprint is not null);
        var now = Temporal.NextBoundary(assignment?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (assignment is not null)
        {
            assignment.ActiveTo = now;
            await session.FlushAsync(ct);
        }
        var current = new AiProfileAssignment
        {
            Id = Guid.CreateVersion7(), Kind = kind, RevisionId = revision.Id, ChangedById = actor.Id, ActiveFrom = now,
        };
        await session.SaveAsync(current, ct);
        write.Saved();
        return CurrentAiSettings.ToView(kind, current, revision, fingerprint is not null);
    }

    private async Task<SavedProfileView> ViewAsync(AiSavedProfile row, CancellationToken ct)
        => SavedProfileStore.View(row, await store.RevisionAsync(row, ct),
            await CurrentAiSettings.LoadAssignmentAsync(session, row.Kind, ct));
}
