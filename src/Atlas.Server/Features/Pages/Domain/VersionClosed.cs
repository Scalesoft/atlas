using Atlas.Server.Platform.Events;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Pages.Domain;

/// <summary>
/// A version of the page history closed (3.6-C): an outbox event, ordered per page, for a consumer such as automatic publication
/// (3.6-I). <paramref name="RevisionId"/> is the version's newest revision, which no later revision joins. Raised once per version,
/// and only while some consumer takes it (<see cref="Outbox.IsConsumed{TEvent}"/>). <paramref name="Reason"/> is one of
/// <see cref="VersionCloseReasons"/>.
/// </summary>
public sealed record VersionClosed(Guid PageId, Guid RevisionId, string Reason);

public static class VersionCloseReasons
{
    /// <summary>Nothing was saved for <see cref="RevisionVersions.Pause"/>; announced by <see cref="VersionPauseCheck"/>.</summary>
    public const string Pause = "pause";
    /// <summary>The version reached <see cref="RevisionVersions.Longest"/>; announced by the save that opened the next one.</summary>
    public const string Longest = "longest";
    /// <summary>Midnight in Europe/Prague; announced by the save that opened the next one.</summary>
    public const string Day = "day";
    /// <summary>A change with AI began or ended, or another AI source wrote.</summary>
    public const string AiBoundary = "ai";
    /// <summary>A restore, a source replacement or a large change: that revision is a version of its own, closed at once, and so is the version before it.</summary>
    public const string Explicit = "explicit";
    /// <summary>„Uložit verzi“ named the version.</summary>
    public const string Named = "named";
    /// <summary>The version was published (or imported); announced by the save that opened the next one.</summary>
    public const string Published = "published";
}

/// <summary>Whether the version that <paramref name="RevisionId"/> ended still ends with it <see cref="RevisionVersions.Pause"/> after it was
/// saved: then nothing followed and the version closed by the pause. Scheduled by <see cref="RevisionVersions.PlaceAsync"/>.</summary>
public sealed record VersionPauseCheck(Guid PageId, Guid RevisionId) : IOutboxMessage;

public static class VersionPauseCheckHandler
{
    public static async Task Handle(VersionPauseCheck check, ISession session, Outbox outbox, CancellationToken ct)
    {
        var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == check.PageId && x.ActiveTo == null)
            .Select(x => x.Id, x => x.IsExplicit).ListAsync<object[]>(ct);
        // A later revision continued or closed the version; an explicit one announced itself when it was saved.
        if (current is [[Guid id, false]] && id == check.RevisionId && outbox.IsConsumed<VersionClosed>())
        {
            outbox.Enqueue(new VersionClosed(check.PageId, check.RevisionId, VersionCloseReasons.Pause), check.PageId.ToString());
        }
    }
}
