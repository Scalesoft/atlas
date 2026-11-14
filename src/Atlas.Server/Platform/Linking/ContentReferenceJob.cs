using Atlas.Server.Features.Contacts;
using Atlas.Server.Features.Linking.Commands;
using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using Wolverine;

namespace Atlas.Server.Platform.Linking;

/// <summary>
/// Keeps <c>ContentReference</c> whole (3.3 plan §1 row 4): passes of <see cref="ContentReferenceStore.SweepAsync"/> until one finds
/// less than a full batch. At every start (a migration or a restore leaves it empty; 10 000 pages take a few minutes, once) and on a
/// request where what the sweep reads changed: the names of a place (<see cref="PlaceLexiconRevision"/>), comments (CommentNotices),
/// board text (BoardBatchWriter), pages moved to another space (MovePageToSpaceHandler). A page's own revisions come through
/// <see cref="ContentReferenceRevision"/>.
/// </summary>
/// <remarks>A board's trash or restore, a space grant and a contact's trash request no pass: the overview reads pages only, through the
/// reader's current names, so until the next pass (any request, the next start) they skew only the ranking count of PlaceNames.</remarks>
public sealed class ContentReferenceJob : IJobDefinition
{
    public const string JobId = "linking.references";

    /// <summary>Requests within this window merge into one pass: an import or a board being edited sweeps once.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    public string Id => JobId;
    public string Title => "Odkazy v obsahu";
    public string Description => "Dopočítá odkazy a zmínky stránek, nástěnek a komentářů po změně jmen v prostoru a po obnově ze zálohy.";
    public JobSchedule? Schedule => JobSchedule.AtStartup;

    public static Task RequestAsync(IJobRequests requests, string reason, CancellationToken ct)
        => requests.RequestAsync(JobId, reason, notBefore: DateTime.UtcNow + Settle, ct: ct);

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var bus = context.Services.GetRequiredService<IMessageBus>();
        var total = 0;
        int done;
        do
        {
            // One pass is one transaction (the sweep's advisory lock); a full batch means more is waiting.
            done = await bus.InvokeAsync<int>(new SweepContentReferences(), ct);
            total += done;
            context.Summarize(new { sources = total });
        }
        while (done >= ContentReferenceStore.SweepBatch);
        context.Log.LogInformation("Odkazy přečteny z {Count} zdrojů", total);
    }
}

/// <summary>What changes the names of a place (<see cref="PlaceNames"/>): a directory contact, a page's publication, trash or restore,
/// and the catalog's <c>rebuild</c> after a restore.</summary>
public sealed record PlaceLexiconRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message.ContentType == ContactObjectType.Id || message.RevisionKind != "working";
}

public static class PlaceLexiconRevisionHandler
{
    public static Task Handle(PlaceLexiconRevision delivery, IJobRequests requests, CancellationToken ct)
        => PlaceLexiconRevision.Takes(delivery.Event)
            ? ContentReferenceJob.RequestAsync(requests, "Změna jmen v prostoru", ct)
            : Task.CompletedTask;
}
