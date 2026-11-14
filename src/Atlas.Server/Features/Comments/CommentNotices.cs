using System.Text.Json.Nodes;
using Atlas.Server.Features.Comments.Queries;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Platform.Linking;
using Atlas.Server.Platform.Live;
using Atlas.Server.Platform.Resources;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Comments;

/// <summary>
/// Tells the object's view sessions, after the commit, that its comments changed (comments spec §3): <c>ObjectChanged</c> with
/// <c>{ kind: "comments", count, latestId }</c>, ids and the count only; the clients read the comments themselves. The view
/// sessions deliver it only to participants who may read the object. It also asks for a pass of the references (ContentReferenceJob),
/// which reads the comments' mentions.
/// </summary>
public sealed class CommentNotices
{
    private readonly ISession m_session;
    private readonly AccessChanges m_changes;
    private readonly ViewSessions m_views;
    private readonly IJobRequests m_jobs;

    public CommentNotices(ISession session, AccessChanges changes, ViewSessions views, IJobRequests jobs)
    {
        m_session = session;
        m_changes = changes;
        m_views = views;
        m_jobs = jobs;
    }

    public async Task AnnounceAsync(ContentResource envelope, CancellationToken ct)
    {
        var count = await CommentReads.CountAsync(m_session, envelope.Id, ct);
        var change = new JsonObject { ["kind"] = "comments", ["count"] = count.Count, ["latestId"] = count.LatestId?.ToString("D") };
        // The object's own version: a comment does not change the object, so nobody catches up because of it.
        var message = new ObjectChangedMessage(envelope.Id, envelope.Version, change);
        m_changes.AfterCommit(() => m_views.ObjectChangedAsync(message));
        await ContentReferenceJob.RequestAsync(m_jobs, "Komentáře", ct);
    }
}
