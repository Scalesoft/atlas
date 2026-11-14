using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Pages.Domain;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Commands;

/// <summary>The generation the server seeds a page's live state into (LiveSeeds), fixed under the page row lock.</summary>
/// <param name="Skip">A revision the server could not seed (LiveSeeds): left alone, before any lock, until the page has another one.</param>
public sealed record PrepareLiveSeed(Guid PageId, Guid? Skip = null);

/// <summary>What to seed: the generation, its bound schema and the current revision's content; no content when there is nothing to seed
/// (<paramref name="Current"/>: the state is current already).</summary>
public sealed record LiveSeedTarget(bool Current, Guid Generation = default, string? SchemaJson = null, string? ContentJson = null, Guid RevisionId = default);

/// <summary>
/// Decides whether the page's current revision lacks a live state the server may seed, first without a lock (most deliveries and opens
/// find it current), then again under the page row lock, as every collaboration write (LiveDocument). Seeded is a page without a
/// collaboration row (a new one starts, as OpenCollaboration starts it), a generation without state and without updates on the current
/// revision, and a fully saved state OpenCollaboration would reset to its revision anyway (CollaborationSchema.StartsFromRevision) while
/// nobody is connected to it. Never: a page in the trash, a state with updates or on another revision. Content in the pre-3.6 requirement
/// shape is converted first (repair on open, as OpenCollaboration does); left unconverted, it is never seeded, so the old shape never freezes
/// into the live document. The seed binds the current host schema unless a writer bound one.
/// </summary>
public sealed class PrepareLiveSeedHandler(ISession session, LiveDocumentWriter writer, RequirementPageConversion conversion)
{
    private static readonly string HostSchemaJson = JsonSerializer.Serialize(CollaborationSchema.KnownHostSchemas[^1]);

    private enum Seed { None, Current, Create, Fill, Reset }

    public async Task<LiveSeedTarget> Handle(PrepareLiveSeed command, CancellationToken ct)
    {
        var current = await CurrentAsync(command.PageId, ct);
        if (current is not null && current.Id == command.Skip)
        {
            return new LiveSeedTarget(false);
        }
        if (current is not null && await conversion.RepairOnOpenAsync(command.PageId, current.Id, current.ContentJson, ct))
        {
            await session.RefreshAsync(current, ct);
        }
        var state = await session.GetAsync<PageCollaboration>(command.PageId, ct);
        var decision = Decide(command.PageId, state, current);
        if (decision is Seed.None or Seed.Current)
        {
            return new LiveSeedTarget(decision == Seed.Current);
        }
        if (await session.GetForUpdateAsync<Page>(command.PageId, ct) is not { IsTrashed: false } page)
        {
            return new LiveSeedTarget(false);
        }
        // Again under the lock: a save, a conversion in place or another seed may have come first.
        current = await CurrentAsync(command.PageId, ct);
        if (current is not null)
        {
            await session.RefreshAsync(current, ct);
        }
        // Read again, locked: the unlocked read above may be stale.
        if (state is not null)
        {
            session.Evict(state);
        }
        state = await session.GetAsync<PageCollaboration>(command.PageId, LockMode.Upgrade, ct);
        decision = Decide(command.PageId, state, current);
        switch (decision)
        {
            case Seed.None or Seed.Current:
                return new LiveSeedTarget(decision == Seed.Current);
            case Seed.Create:
                state = new PageCollaboration { PageId = command.PageId, Generation = Guid.NewGuid(), BaseRevisionId = current!.Id };
                await session.SaveAsync(state, ct);
                break;
            case Seed.Reset:
                await new CollaborationCheckpoints(session).ResetAsync(page, current!, ct);
                break;
        }
        // The seed binds it under the document gate (LiveDocumentWriter.SeedAsync), so a failed seed leaves the generation unbound.
        return new LiveSeedTarget(false, state!.Generation, state.SchemaBound ? state.SchemaJson ?? HostSchemaJson : HostSchemaJson, current!.ContentJson,
            current.Id);
    }

    private Task<PageRevision?> CurrentAsync(Guid pageId, CancellationToken ct)
        => session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null).SingleOrDefaultAsync(ct)!;

    private Seed Decide(Guid pageId, PageCollaboration? state, PageRevision? current)
    {
        if (current is null || current.ContentJson.Contains(RequirementPageConversion.Marker, StringComparison.Ordinal))
        {
            return Seed.None;
        }
        if (state is null)
        {
            return Seed.Create;
        }
        if (state.BaseRevisionId != current.Id)
        {
            return Seed.None;
        }
        if (state.StateSha256 is null)
        {
            return state.Sequence == 0 ? Seed.Fill : Seed.None;
        }
        if (!CollaborationSchema.StartsFromRevision(state, current))
        {
            return Seed.Current;
        }
        return writer.HasPeers(new WikiDocumentId(pageId, state.Generation).ToString()) ? Seed.None : Seed.Reset;
    }
}
