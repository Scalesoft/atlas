using Atlas.Server.Platform.Events;
using Atlas.Server.Platform.Jobs;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Backups.Git;

/// <summary>The Git backup's own delivery of <see cref="ContentRevised"/>: a page's publication, trash or restore (owner decision O6).
/// The run compares states, and requests merge, so a burst of publications gives one more run.</summary>
public sealed record GitBackupRevision(ContentRevised Event) : IOutboxDelivery<ContentRevised>
{
    public static bool Takes(ContentRevised message) => message is { ContentType: "atlas.page", RevisionKind: "published" };
}

/// <summary>Asks for a run of the Git backup while it is on; requests merge, so a burst of publications gives one more run (plan §4.2).
/// Moves, space renames and saved drafts announce nothing here: the run after 10 minutes of quiet compares them.</summary>
public static class GitBackupRevisionHandler
{
    public static async Task Handle(GitBackupRevision delivery, ISession session, IJobRequests jobs, CancellationToken ct)
    {
        if (!GitBackupRevision.Takes(delivery.Event)
            || (await session.GetAsync<GitBackupSettings>(1, ct))?.Enabled != true)
        {
            return;
        }
        await jobs.RequestAsync(GitBackupJob.JobId, "Publikace stránky", ct: ct);
    }
}
