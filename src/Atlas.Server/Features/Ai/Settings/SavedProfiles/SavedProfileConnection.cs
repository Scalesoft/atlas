using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.ModuleSettings;
using Wolverine;

namespace Atlas.Server.Features.Ai.Settings.SavedProfiles;

/// <summary>Short authorized snapshots before provider calls, followed by a stale-result check after testing.</summary>
public sealed class SavedProfileConnectionHandler(ModuleSettingsAccess access, SavedProfileStore store,
    IDataProtectionProvider protection)
{
    public async Task<AiProfileSnapshot> Handle(PrepareSavedModelDiscovery command, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var kind = AiProfileRules.RequireKind(command.Kind);
        var request = command.Request;
        var row = command.Id is { } id ? await store.RequireAsync(kind, id, ct) : null;
        AdministrationVersion.Expect(row?.ActiveFrom, request.ExpectedSavedVersion);
        var previous = row is null ? null : await store.RevisionAsync(row, ct);
        var draft = AiProfileRules.ConnectionDraft(kind,
            new DiscoverAiModelsRequest(request.Provider, request.Location, request.BaseUrl, request.ApiKey, request.ClearApiKey));
        AiProfileRules.ApplyKey(draft, previous, request.ApiKey, request.ClearApiKey,
            protection.CreateProtector(SaveAiProfileHandler.ApiKeyPurpose));
        return AiProfileSnapshot.From(draft);
    }

    public async Task<AiProfileSnapshot> Handle(BeginSavedProfileTest command, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var row = await store.RequireAsync(AiProfileRules.RequireKind(command.Kind), command.Id, ct);
        AdministrationVersion.Expect(row.ActiveFrom, command.ExpectedVersion);
        var revision = await store.RevisionAsync(row, ct);
        TestAiProfileHandler.RequireTestable(revision);
        return AiProfileSnapshot.From(revision);
    }

    public async Task<bool> Handle(CompleteSavedProfileTest command, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var row = await store.RequireAsync(AiProfileRules.RequireKind(command.Kind), command.Id, ct);
        if (row.RevisionId != command.RevisionId)
            throw new ApiException(409, "Profil se během testu změnil. Spusťte test znovu.");
        return true;
    }
}

/// <summary>No transaction spans the external catalogue or synthetic test request.</summary>
public sealed class SavedProfileConnectionRunner(IMessageBus bus, IAiClientFactory factory, TestAiProfileRunner tests)
{
    public async Task<AiModelCatalogue> DiscoverAsync(string kind, Guid? id, SavedModelDiscoveryRequest request, CancellationToken ct)
    {
        var snapshot = await bus.InvokeAsync<AiProfileSnapshot>(new PrepareSavedModelDiscovery(kind, id, request), ct);
        return await factory.ListModelsAsync(snapshot, ct);
    }

    public async Task<AiTestResult> TestAsync(string kind, Guid id, string? expectedVersion, CancellationToken ct)
    {
        var snapshot = await bus.InvokeAsync<AiProfileSnapshot>(new BeginSavedProfileTest(kind, id, expectedVersion), ct);
        var result = await tests.TestSnapshotAsync(snapshot, ct);
        await bus.InvokeAsync<bool>(new CompleteSavedProfileTest(kind, id, snapshot.RevisionId), ct);
        return result;
    }
}
