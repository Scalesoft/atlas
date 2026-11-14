using Microsoft.AspNetCore.DataProtection;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.ModuleSettings;
using Wolverine;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Commands;

public sealed record PrepareAiModelDiscovery(string Kind, DiscoverAiModelsRequest Request);

/// <summary>Authorize and snapshot a draft connection; no database transaction survives the provider request.</summary>
public sealed class PrepareAiModelDiscoveryHandler(ISession session, ModuleSettingsAccess access, IDataProtectionProvider protection)
{
    public async Task<AiProfileSnapshot> Handle(PrepareAiModelDiscovery command, CancellationToken ct)
    {
        await access.RequireWikiAdminAsync(ct);
        var revision = AiProfileRules.ConnectionDraft(command.Kind, command.Request);
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, revision.Kind, ct);
        var previous = assignment is null ? null : await session.GetAsync<AiProfileRevision>(assignment.RevisionId, ct);
        AiProfileRules.ApplyKey(revision, previous, command.Request.ApiKey, command.Request.ClearApiKey,
            protection.CreateProtector(SaveAiProfileHandler.ApiKeyPurpose));
        return AiProfileSnapshot.From(revision);
    }
}

public sealed class DiscoverAiModelsRunner(IMessageBus bus, IAiClientFactory factory)
{
    public async Task<AiModelCatalogue> DiscoverAsync(string kind, DiscoverAiModelsRequest request, CancellationToken ct)
    {
        var connection = await bus.InvokeAsync<AiProfileSnapshot>(new PrepareAiModelDiscovery(kind, request), ct);
        return await factory.ListModelsAsync(connection, ct);
    }
}
