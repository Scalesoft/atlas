using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.AiIndex.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AiIndex;

/// <summary>
/// Which saved revision embeds for a generation. The current embeddings revision when it has the generation's fingerprint (a
/// rotated key, a new URL, a TLS proxy keep the index, R3), else the generation's own revision (a candidate of a model
/// exchange before its activation). So the index and the queries it is compared with always come from one model.
/// </summary>
public static class IndexProfiles
{
    public static async Task<IReadOnlyDictionary<Guid, AiProfileSnapshot>> ResolveAsync(ISession session, IEnumerable<IndexGenerationRow> generations,
        bool enabledOnly, CancellationToken ct)
    {
        var assignment = await CurrentAiSettings.LoadAssignmentAsync(session, AiProfileKind.Embeddings, ct);
        var current = assignment is null ? null : await session.GetAsync<AiProfileRevision>(assignment.RevisionId, ct);
        var result = new Dictionary<Guid, AiProfileSnapshot>();
        foreach (var generation in generations)
        {
            var revision = current is not null && string.Equals(current.Fingerprint, generation.Fingerprint, StringComparison.Ordinal)
                ? current
                : await session.GetAsync<AiProfileRevision>(generation.ProfileRevisionId, ct);
            if (revision is not null && (revision.Enabled || !enabledOnly))
            {
                result[generation.Id] = AiProfileSnapshot.From(revision);
            }
        }
        return result;
    }
}
