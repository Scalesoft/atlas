namespace Atlas.Server.Features.Projector.Persistence;

/// <summary>
/// Projector reads as any reader makes them, for the async flow inside <see cref="Use"/>: the published rows of every page, an editor's
/// working rows, saves and never published pages left out. The assistant's tool session sets it for a <c>projector_*</c> read when the
/// gate „Čtení pracovních verzí asistentem“ (spec §7.1) refuses working text, as <c>atlas_get_page</c> refuses <c>version: "working"</c>.
/// Only server code sets it; no request can.
/// </summary>
public static class ProjectorPublishedReads
{
    private static readonly AsyncLocal<bool> s_active = new();

    public static bool Active => s_active.Value;

    /// <summary>Whether an editor reads the working rows here: <paramref name="canEdit"/>, unless published reads are on.</summary>
    public static bool Working(bool canEdit) => canEdit && !s_active.Value;

    /// <summary><see cref="Working"/> for a page read through its gate: a reader of a live page (3.6-I Živě) reads the working rows too.</summary>
    public static async Task<bool> WorkingAsync(NHibernate.ISession session, LockedPage page, CancellationToken ct)
        => Working(page.CanEdit || page.Page.PublishedRevisionId is not null
            && await PublicationModes.EffectiveAsync(session, page.Page, ct) == PublicationMode.Live);

    public static IDisposable Use()
    {
        var previous = s_active.Value;
        s_active.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => s_active.Value = previous;
    }
}
