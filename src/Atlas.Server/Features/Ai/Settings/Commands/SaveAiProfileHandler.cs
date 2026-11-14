using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Commands;

/// <summary>
/// A save inserts an immutable revision and moves the kind's assignment (R3). The key is write-only: protected with
/// <see cref="ApiKeyPurpose"/>, kept across saves, and dropped when the profile moves to another provider or origin
/// without a new key, so a stored credential never reaches a server it was not entered for.
/// </summary>
public sealed class SaveAiProfileHandler
{
    public const string ApiKeyPurpose = "ScaleAtlas.Ai.ApiKey.v1";
    private readonly ISession m_session;
    private readonly AiSettingsWrite m_write;
    private readonly IEmbeddingChangeGuard m_index;
    private readonly IDataProtector m_protector;

    public SaveAiProfileHandler(ISession session, AiSettingsWrite write, IEmbeddingChangeGuard index, IDataProtectionProvider protection)
    {
        m_session = session;
        m_write = write;
        m_index = index;
        m_protector = protection.CreateProtector(ApiKeyPurpose);
    }

    public async Task<AiProfileView> Handle(SaveAiProfile command, CancellationToken ct)
    {
        var actor = await m_write.BeginAsync(ct);
        var kind = AiProfileRules.RequireKind(command.Kind);
        var request = command.Request;
        var revision = AiProfileRules.Draft(kind, request);
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(m_session, kind, ct);
        AdministrationVersion.Expect(assignment?.ActiveFrom, request.ExpectedVersion);
        var previous = assignment is null ? null : await m_session.GetAsync<AiProfileRevision>(assignment.RevisionId, ct);
        AiProfileRules.ApplyKey(revision, previous, request.ApiKey, request.ClearApiKey, m_protector);
        AiProfileRules.RequireUsable(revision);
        var activeFingerprint = kind == AiProfileKind.Embeddings ? await m_index.ActiveFingerprintAsync(ct) : null;
        if (activeFingerprint is not null && !string.Equals(activeFingerprint, revision.Fingerprint, StringComparison.Ordinal))
        {
            throw new ApiException(409, "Změna modelu vyžaduje novou generaci indexu. Použijte Index → Vyměnit model.", "ai_generation_required");
        }
        var now = Temporal.NextBoundary(assignment?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
        if (assignment is not null)
        {
            assignment.ActiveTo = now;
            // One current assignment per kind: close it before the replacement insert reaches the database.
            await m_session.FlushAsync(ct);
        }
        revision.Id = Guid.NewGuid();
        revision.CreatedById = actor.Id;
        revision.CreatedAt = now;
        var current = new AiProfileAssignment { Id = Guid.NewGuid(), Kind = kind, RevisionId = revision.Id, ChangedById = actor.Id, ActiveFrom = now };
        await m_session.SaveAsync(revision, ct);
        await m_session.SaveAsync(current, ct);
        m_write.Saved();
        return CurrentAiSettings.ToView(kind, current, revision, activeFingerprint is not null);
    }
}
