using System.Text.RegularExpressions;
using NHibernate;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>
/// The live state of a page holds the Yjs snapshot (up to 4 MiB) and its projections. The writes that only lock the row, move
/// its checkpoint or reset it never read them (PRF-S-5): only a cold load reads the snapshot, only a live save its editor JSON.
/// </summary>
public sealed class CollaborationStateColumnsTests
{
    private static readonly Regex Snapshot = new("""^\s*select\b.*\."State"\s""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Projection = new("""^\s*select\b.*\."Projection(Json|Markdown|Text)"\s""", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    [PostgreSqlFact]
    public async Task Flush_checkpoints_and_resets_leave_the_snapshot_and_projections_unread()
    {
        await using var f = await CollaborationFixture.CreateAsync();
        var opened = await f.Store.OpenAsync(f.Page.Id, f.Writer);
        await f.Append(opened.DocumentId, Guid.NewGuid(), [1]);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(1), default);

        using var recording = SqlRecording.Start();
        await f.Append(opened.DocumentId, Guid.NewGuid(), [2]);
        await f.Store.FlushAsync(opened.DocumentId, CollaborationFixture.Snapshot(2), default);
        IReadOnlyList<string> validation;
        using (var session = f.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            var checkpoints = new CollaborationCheckpoints(session);
            var page = await session.GetAsync<Page>(f.Page.Id, LockMode.Upgrade);
            var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == f.Page.Id && x.ActiveTo == null).Select(x => x.Id).SingleOrDefaultAsync<Guid>();
            using (var validating = SqlRecording.Start())
            {
                Assert.Equal(2, (await checkpoints.ValidateCheckpointAsync(page, opened.Generation, current, 2, default)).Sequence);
                validation = validating.Statements;
            }
            var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync();
            await checkpoints.AdvanceCheckpointAsync(page, revision, default, markSaved: true);
            await checkpoints.ResetAllAsync([page.Id], default);
            await transaction.CommitAsync();
        }

        Assert.DoesNotContain(recording.Statements, Snapshot.IsMatch);
        Assert.DoesNotContain(recording.Statements.Except(validation), Projection.IsMatch);
        using var read = f.Host.OpenSession();
        var state = await read.GetAsync<PageCollaboration>(f.Page.Id);
        Assert.NotEqual(opened.Generation, state.Generation);
        Assert.Equal(0, state.Sequence);
        Assert.Equal(0, state.LastSavedSequence);
        Assert.Null(state.State);
        Assert.Null(state.ProjectionJson);
    }
}
