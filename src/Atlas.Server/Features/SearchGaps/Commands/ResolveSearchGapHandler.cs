using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.SearchGaps.Commands;

/// <summary>"Označit jako vyřešené": an idempotent upsert; from then on only later days count towards the gap again.</summary>
public sealed class ResolveSearchGapHandler(IContentAccess access, SearchGapStore store)
{
    public async Task Handle(ResolveSearchGap command, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(command.SpaceId, ct);
        await access.RequireSpaceAsync(command.SpaceId, SpaceRole.Admin, ct);
        var normalized = command.NormalizedQuery is { Length: <= 200 } query ? SearchText.NormalizeContent(query) : "";
        if (normalized.Length == 0)
        {
            throw new ApiException(400, "Zadejte nenalezený dotaz o nejvýše 200 znacích.");
        }
        await store.ResolveAsync(command.SpaceId, normalized, actor.Id, ct);
    }
}
