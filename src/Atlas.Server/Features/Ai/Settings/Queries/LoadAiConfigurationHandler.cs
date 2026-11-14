using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Ai.Settings.Queries;

/// <summary>Every current AI configuration row in one read transaction, so the switch, the revisions, the task switches and the
/// active generation are one consistent snapshot (spec RAG §6 step 4).</summary>
public sealed class LoadAiConfigurationHandler(ISession session, IActiveGenerationReader generations)
{
    public async Task<AiConfigurationSnapshot> Handle(LoadAiConfiguration query, CancellationToken ct)
    {
        var master = await CurrentAiSettings.LoadSwitchAsync(session, ct);
        var assignments = await session.QueryOver<AiProfileAssignment>().Where(x => x.ActiveTo == null).ListAsync(ct);
        var profiles = new Dictionary<string, AiProfileSnapshot>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (await session.GetAsync<AiProfileRevision>(assignment.RevisionId, ct) is { } revision)
            {
                profiles[assignment.Kind] = AiProfileSnapshot.From(revision);
            }
        }
        var features = await CurrentAiSettings.LoadFeaturesAsync(session, ct);
        return new AiConfigurationSnapshot(master?.Enabled ?? false, profiles, CurrentAiSettings.ResolveTasks(features?.TasksJson),
            await generations.ReadAsync(ct));
    }
}
