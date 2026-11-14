using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.AiIndex.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.AiIndex.Commands;

public sealed class ClaimIndexTasksHandler(ISession session, IndexStore store, AiAvailability availability)
{
    private static readonly IReadOnlySet<string> NoHashes = new HashSet<string>();

    /// <summary>
    /// Nothing happens while the master switch is off or no live generation has an enabled profile: the tasks wait, unspent.
    /// The page content is read here, in the claim transaction, so the model works on one consistent snapshot; the write later
    /// checks that the snapshot is still current.
    /// </summary>
    public async Task<ClaimedIndexWork> Handle(ClaimIndexTasks command, CancellationToken ct)
    {
        var (profiles, eligibleGenerations, _) = await GateAsync(ct);
        if (eligibleGenerations.Length == 0) return ClaimedIndexWork.None;
        var now = DateTime.SpecifyKind(command.Now, DateTimeKind.Utc);
        var claimed = await store.ClaimAsync(eligibleGenerations, command.Max, now, now + command.Lease, ct);
        if (claimed.Count == 0)
        {
            return ClaimedIndexWork.None;
        }
        var pages = await store.PagesAsync(claimed.Select(x => x.PageId).Distinct().ToArray(), ct);
        var indexed = await store.IndexedAsync(claimed.Select(x => (x.GenerationId, x.PageId)).ToArray(), ct);
        var sources = new List<IndexSource>();
        var settled = 0;
        foreach (var (generationId, pageId, token) in claimed)
        {
            if (!pages.TryGetValue(pageId, out var page) || !page.Eligible || page.PublishedRevisionId is not { } revisionId)
            {
                // Trash, no publication, an excluded space: the page leaves the generation without any model call.
                await store.RemoveAsync(generationId, pageId, ct);
                settled++;
                continue;
            }
            indexed.TryGetValue((generationId, pageId), out var existing);
            if (existing is not null && existing.RevisionId == revisionId && existing.IsTemplate == page.IsTemplate)
            {
                // A move between included spaces, a repeated enqueue: the row is current already.
                await store.DeleteTaskAsync(generationId, pageId, ct);
                settled++;
                continue;
            }
            sources.Add(new IndexSource(generationId, pageId, token, profiles[generationId], revisionId, page.IsTemplate, page.Title ?? "",
                page.Markdown ?? "", page.Headings, existing?.ChunkHashes ?? NoHashes, existing?.TitleHash));
        }
        return new ClaimedIndexWork(sources, settled);
    }

    /// <summary>When the claim next finds a task (IndexJob's data schedule): a waiting task at once, a backed-off or leased one at its
    /// time; one waiting for the embeddings profile no sooner than <see cref="NextIndexTaskDue.OutageRecheck"/>. Null when nothing can
    /// be claimed until the configuration changes: a generation's start, an AI settings save (AiSettingsWrite) and a space's inclusion ask again.</summary>
    public async Task<DateTime?> Handle(NextIndexTaskDue query, CancellationToken ct)
    {
        var (_, eligible, unavailable) = await GateAsync(ct);
        if (eligible.Length + unavailable.Length == 0) return null;
        var now = DateTime.SpecifyKind(query.Now, DateTimeKind.Utc);
        var dues = await store.NextDueAsync([.. eligible, .. unavailable], now, ct);
        return dues.Select(due => unavailable.Contains(due.GenerationId) && due.At < now + NextIndexTaskDue.OutageRecheck ? now + NextIndexTaskDue.OutageRecheck : due.At)
            .Order().Cast<DateTime?>().FirstOrDefault();
    }

    /// <summary>The live generations whose tasks may be claimed now, and those that wait for the embeddings profile to be available
    /// again; nothing while the master switch is off or no live generation has an enabled profile.</summary>
    private async Task<(IReadOnlyDictionary<Guid, AiProfileSnapshot> Profiles, Guid[] Eligible, Guid[] Unavailable)> GateAsync(CancellationToken ct)
    {
        if ((await CurrentAiSettings.LoadSwitchAsync(session, ct))?.Enabled != true)
        {
            return (new Dictionary<Guid, AiProfileSnapshot>(), [], []);
        }
        var generations = await store.GenerationsAsync([IndexGenerationState.Building, IndexGenerationState.Active], ct);
        if (generations.Count == 0)
        {
            return (new Dictionary<Guid, AiProfileSnapshot>(), [], []);
        }
        var profiles = await IndexProfiles.ResolveAsync(session, generations, enabledOnly: true, ct);
        // The health of the assigned model says nothing about a replacement generation's candidate revision.
        var health = availability.Health(AiProfileKind.Embeddings);
        var eligible = profiles.ToLookup(x => x.Value.RevisionId != health.RevisionId || health.State == AiProfileState.Available, x => x.Key);
        return (profiles, eligible[true].ToArray(), eligible[false].ToArray());
    }
}

public sealed class StoreIndexedPageHandler(IndexStore store)
{
    public async Task<StoredIndexPage> Handle(StoreIndexedPage command, CancellationToken ct)
    {
        // The page first, then the task (see IndexStore.LockPageAsync).
        var page = await store.LockPageAsync(command.PageId, ct);
        var task = await store.LockTaskAsync(command.GenerationId, command.PageId, ct);
        if (task is null)
        {
            return new StoredIndexPage(false);
        }
        if (task.Value.Token != command.Token || page is null || !page.Eligible || page.PublishedRevisionId != command.RevisionId
            || page.IsTemplate != command.IsTemplate)
        {
            // A newer change has enqueued the page again (it carries a new token); the next round indexes that state.
            await store.ReleaseLeaseAsync(command.GenerationId, command.PageId, ct);
            return new StoredIndexPage(false);
        }
        var reused = command.Chunks.Where(x => x.Vector is null).Select(x => x.TextHash).ToList();
        if (command.TitleVector is null)
        {
            reused.Add(command.TitleHash);
        }
        var vectors = await store.ReusableAsync(command.GenerationId, command.PageId, reused, ct);
        string? Vector(string? fresh, byte[] hash) => fresh ?? vectors.GetValueOrDefault(VectorText.Hex(hash));
        var title = Vector(command.TitleVector, command.TitleHash);
        var chunks = command.Chunks.Select(chunk => (Chunk: chunk, Vector: Vector(chunk.Vector, chunk.TextHash))).ToArray();
        if (title is null || chunks.Any(x => x.Vector is null))
        {
            // What the claim saw is gone; claim again and embed what is missing.
            await store.ReleaseLeaseAsync(command.GenerationId, command.PageId, ct);
            return new StoredIndexPage(false);
        }
        await store.WriteAsync(command.GenerationId, command.PageId, command.RevisionId, command.IsTemplate, title, command.TitleHash,
            chunks.Select(x => (x.Chunk, x.Vector!)).ToArray(), ct);
        // R1 (V4): the new centroid moves the page's neighbours and its place in theirs, in the same transaction.
        await store.RefreshNeighborsAsync(command.GenerationId, command.PageId, ct);
        await store.DeleteTaskAsync(command.GenerationId, command.PageId, ct);
        return new StoredIndexPage(true);
    }
}

public sealed class ReleaseIndexTaskHandler(IndexStore store)
{
    public async Task Handle(ReleaseIndexTask command, CancellationToken ct)
    {
        var task = await store.LockTaskAsync(command.GenerationId, command.PageId, ct);
        if (task is null)
        {
            return;
        }
        if (task.Value.Token != command.Token)
        {
            // Enqueued again meanwhile: a fresh task without this failure.
            await store.ReleaseLeaseAsync(command.GenerationId, command.PageId, ct);
            return;
        }
        DateTime? next = command.CountsAsAttempt
            ? DateTime.SpecifyKind(command.Now, DateTimeKind.Utc) + IndexBackoff.Delay(task.Value.Attempts, command.Jitter)
            : null;
        await store.ReleaseAsync(command.GenerationId, command.PageId, command.Code, next, ct);
    }
}
