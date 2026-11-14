using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.Ai.Settings.SavedProfiles;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.AiIndex.Domain;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AiIndex.Commands;

/// <summary>
/// A new Building generation beside the active index. <c>build</c> and <c>rebuild</c> take the saved embeddings revision;
/// <c>replace</c> saves the request as a candidate revision without an assignment (the key rules of a profile save apply:
/// a stored key stays only for the same provider and origin), so search keeps embedding with the old model until activation.
/// Every eligible page gets a task at the generation's boundary. One Building generation at a time (409).
/// </summary>
public sealed class StartIndexGenerationHandler(ISession session, AiSettingsWrite write, IndexStore store, IndexViews views,
    IDataProtectionProvider protection, SavedProfileStore savedProfiles, IJobRequests jobs)
{
    public async Task<GenerationView> Handle(StartIndexGeneration command, CancellationToken ct)
    {
        var actor = await write.BeginAsync(ct);
        var mode = command.Request.Mode?.Trim();
        if (mode is not ("build" or "rebuild" or "replace"))
        {
            throw new ApiException(400, "Zvolte stavbu, přestavbu, nebo výměnu modelu.");
        }
        var live = await store.GenerationsAsync([IndexGenerationState.Building, IndexGenerationState.Active], ct);
        if (live.Any(x => x.State == IndexGenerationState.Building))
        {
            throw new ApiException(409, "Stavba indexu už běží. Dokončete ji, nebo ji zrušte.");
        }
        if (mode == "build" && live.Any(x => x.State == IndexGenerationState.Active))
        {
            throw new ApiException(409, "Index už existuje. Použijte Přestavět, nebo Vyměnit model.");
        }
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, AiProfileKind.Embeddings, ct);
        var current = assignment is null ? null : await session.GetAsync<AiProfileRevision>(assignment.RevisionId, ct);
        AiProfileRevision revision;
        if (mode == "replace")
        {
            if ((command.Request.Profile is null) == (command.Request.SavedProfileId is null))
                throw new ApiException(400, "Vyberte právě jeden uložený profil nebo vyplňte nový model.");
            if (command.Request.SavedProfileId is { } savedId)
            {
                AdministrationVersion.Expect(assignment?.ActiveFrom, command.Request.ExpectedProfileVersion);
                var saved = await savedProfiles.RequireAsync(AiProfileKind.Embeddings, savedId, ct);
                AdministrationVersion.Expect(saved.ActiveFrom, command.Request.ExpectedSavedVersion);
                revision = await savedProfiles.RevisionAsync(saved, ct);
                if (!revision.Enabled || revision.Fingerprint is null)
                    throw new ApiException(400, "Nový model musí být zapnutý.");
                AiProfileRules.RequireUsable(revision);
            }
            else
            {
                var request = command.Request.Profile!;
                AdministrationVersion.Expect(assignment?.ActiveFrom, request.ExpectedVersion);
                revision = await CandidateAsync(request, current, actor, ct);
            }
        }
        else
        {
            revision = current is { Enabled: true, Fingerprint: not null }
                ? current
                : throw new ApiException(400, "Nejdřív uložte a zapněte profil Embeddingy.");
        }
        var id = await store.StartAsync(revision.Id, revision.Fingerprint!, KnowledgeChunker.Version, actor.Id, ct);
        await jobs.RescheduleAsync(IndexJob.JobId, ct);
        return await views.ViewAsync((await store.GenerationAsync(id, false, ct))!, ct);
    }

    private async Task<AiProfileRevision> CandidateAsync(SaveAiProfileRequest request, AiProfileRevision? current,
        UserAccount actor, CancellationToken ct)
    {
        var revision = AiProfileRules.Draft(AiProfileKind.Embeddings, request);
        var newKey = AiProfileRules.NewApiKey(request);
        if (newKey is not null)
        {
            revision.ProtectedApiKey = protection.CreateProtector(SaveAiProfileHandler.ApiKeyPurpose).Protect(newKey);
        }
        else if (!request.ClearApiKey && current is not null
            && string.Equals(AiProfileRules.KeyOrigin(current), AiProfileRules.KeyOrigin(revision), StringComparison.Ordinal))
        {
            revision.ProtectedApiKey = current.ProtectedApiKey;
        }
        if (!revision.Enabled)
        {
            throw new ApiException(400, "Nový model musí být zapnutý.");
        }
        AiProfileRules.RequireUsable(revision);
        revision.Id = Guid.NewGuid();
        revision.CreatedById = actor.Id;
        revision.CreatedAt = DateTime.UtcNow;
        await session.SaveAsync(revision, ct);
        // The generation references the revision; the insert must reach the database before the raw SQL that does.
        await session.FlushAsync(ct);
        return revision;
    }
}

public sealed class VerifyIndexGenerationHandler(ISession session, ModuleSettingsAccess access, IndexStore store, IJobRequests jobs)
{
    public const int Samples = 50;

    public async Task<IndexVerificationPlan> Handle(VerifyIndexGeneration command, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        if ((await CurrentAiSettings.LoadSwitchAsync(session, ct))?.Enabled != true)
            throw AiGate.Unavailable();
        var generation = await store.GenerationAsync(command.GenerationId, false, ct) ?? throw NotFound();
        if (generation.State is not (IndexGenerationState.Building or IndexGenerationState.Active))
        {
            throw new ApiException(409, "Generace už skončila.");
        }
        // A page the generation missed (published while the build started, say) gets its task now, so the build catches up.
        await store.EnqueueInconsistentAsync(generation.Id, ct);
        await jobs.RescheduleAsync(IndexJob.JobId, ct);
        var (boundary, missing, stale) = await store.CompletenessAsync(generation.Id, ct);
        var profiles = await IndexProfiles.ResolveAsync(session, [generation], enabledOnly: false, ct);
        var profile = profiles.GetValueOrDefault(generation.Id) ?? throw new ApiException(409, "Profil generace už neexistuje.");
        var samples = await store.SampleTitlesAsync(generation.Id, Samples, ct);
        return new IndexVerificationPlan(generation.Id, generation.State == IndexGenerationState.Building, profile, boundary, missing, stale,
            generation.Dimensions == AiProfileRules.SupportedDimensions && profile.Dimensions == generation.Dimensions,
            samples.Select(x => new IndexSample(x.PageId, x.Title)).ToArray());
    }

    internal static ApiException NotFound() => new(404, "Generace indexu neexistuje.");
}

/// <summary>
/// Activation in one transaction under the AI settings lock: completeness again, the previous Active generation → Retired,
/// this one → Active, and when its revision has another fingerprint than the current embeddings revision, the assignment
/// moves to it, so queries embed with the model the index was built with from the same commit on (spec RAG §6 step 4).
/// After the commit the configuration cache drops its snapshot (and with it the availability of the old revision, A3).
/// </summary>
public sealed class ActivateIndexGenerationHandler(ISession session, AiSettingsWrite write, IndexStore store, IndexViews views)
{
    public async Task<IndexStatus> Handle(ActivateIndexGeneration command, CancellationToken ct)
    {
        var actor = await write.BeginAsync(ct);
        var generation = await store.GenerationAsync(command.GenerationId, true, ct) ?? throw VerifyIndexGenerationHandler.NotFound();
        if (generation.State != IndexGenerationState.Building)
        {
            throw new ApiException(409, "Generace se nestaví; aktivovat jde jen rozestavěná generace.");
        }
        AdministrationVersion.Expect(generation.Version, command.Request.ExpectedVersion);
        var (boundary, missing, _) = await store.CompletenessAsync(generation.Id, ct);
        if (boundary > 0 || missing > 0)
        {
            throw new ApiException(409, "Index ještě není úplný. Počkejte na dokončení stavby a ověřte ho znovu.");
        }
        await store.ActivateAsync(generation.Id, actor.Id, ct);
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, AiProfileKind.Embeddings, ct);
        var current = assignment is null ? null : await session.GetAsync<AiProfileRevision>(assignment.RevisionId, ct);
        if (!string.Equals(current?.Fingerprint, generation.Fingerprint, StringComparison.Ordinal))
        {
            var now = Temporal.NextBoundary(assignment?.ActiveFrom ?? DateTime.UnixEpoch, DateTime.UtcNow);
            if (assignment is not null)
            {
                assignment.ActiveTo = now;
                // One current assignment per kind: close it before the replacement insert reaches the database.
                await session.FlushAsync(ct);
            }
            await session.SaveAsync(new AiProfileAssignment
            {
                Id = Guid.NewGuid(), Kind = AiProfileKind.Embeddings, RevisionId = generation.ProfileRevisionId, ChangedById = actor.Id, ActiveFrom = now,
            }, ct);
            await session.FlushAsync(ct);
        }
        write.Saved();
        return await views.StatusAsync(ct);
    }
}

public sealed class CancelIndexGenerationHandler(AiSettingsWrite write, IndexStore store, IndexViews views)
{
    public async Task<IndexStatus> Handle(CancelIndexGeneration command, CancellationToken ct)
    {
        await write.BeginAsync(ct);
        var generation = await store.GenerationAsync(command.GenerationId, true, ct) ?? throw VerifyIndexGenerationHandler.NotFound();
        if (generation.State != IndexGenerationState.Building)
        {
            throw new ApiException(409, "Zrušit jde jen rozestavěná generace.");
        }
        await store.CancelAsync(generation.Id, ct);
        return await views.StatusAsync(ct);
    }
}

public sealed class RunIndexMaintenanceHandler(IndexStore store, IJobRequests jobs)
{
    public async Task<IndexConsistency> Handle(RunIndexMaintenance command, CancellationToken ct)
    {
        var (missing, stale, orphaned) = await store.EnqueueInconsistentAsync(null, ct);
        await jobs.RescheduleAsync(IndexJob.JobId, ct);
        return new IndexConsistency(DateTime.SpecifyKind(command.Now, DateTimeKind.Utc), missing, stale, orphaned);
    }
}

public sealed class PurgeIndexGenerationsHandler(IndexStore store)
{
    public async Task<PurgedIndexGenerations> Handle(PurgeIndexGenerations command, CancellationToken ct)
    {
        var (pages, generations) = await store.PurgeBatchAsync(DateTime.SpecifyKind(command.RetiredBefore, DateTimeKind.Utc), command.Batch, ct);
        return new PurgedIndexGenerations(pages, generations);
    }
}
