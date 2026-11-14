using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Objects.Queries;
using Microsoft.AspNetCore.SignalR;
using Wolverine;

namespace Atlas.Server.Platform.Live;

/// <summary>
/// The view sessions of /hubs/live (objects spec §4.2, F1 plan §2.4): one per open view (<c>board:{id}</c>, <c>page:{id}</c>),
/// created by the first JoinView and gone with its last participant. A session knows its members from the view's data
/// (<see cref="Atlas.Server.Platform.Objects.IObjectView"/>) and reads them again after every change of the view's own object.
/// For each participant it holds an <c>obj:{id}</c> subscription per member the participant may read, in
/// <see cref="LiveSubscriptions"/>, so the decision is cached per (participant, object) and revalidated by the relay's sweeps
/// like any channel; a subscription is counted over the participant's views and kept <see cref="Linger"/> after the last one
/// (panning and switching views do not authorize again). An object → sessions index delivers a change or presence of an
/// object to every session containing it, in detail inside the recipient's viewport and as a summary outside it.
/// </summary>
/// <remarks>
/// A message reaches a participant only for an object it was told of (a member returned by JoinView or added by ViewMembers)
/// and whose subscription the newest decision still allows: no message, not even one with an id alone, reaches anyone else.
/// Membership work of one session is serialized by its gate; the maps are guarded by one lock held only for memory work.
/// In memory, like the subscriptions: one active host.
/// </remarks>
// shortcut: a member the participant could not read when it was added stays out of its session after access is granted, until it
// joins again, the view's membership changes or the member is restored (ReadmitAsync); re-resolve unreadable members in the sweep if
// that delay after a grant starts to matter.
// shortcut: a session created from a membership read that a concurrent batch overtook keeps the older membership until the view's
// next change; order membership reads by a ticket if that window matters.
// shortcut: presence shows the colleague's view (via) only to a recipient that already holds a decision on that view's object
// (both in one board, or the board shows the page the colleague edits), "elsewhere" otherwise even if it could read it; decide
// those views on demand and cache them if readers need the name more often.
public sealed class ViewSessions
{
    /// <summary>How long an object subscription outlives the last view of the participant that contains it.</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(30);

    /// <summary>The most objects a viewport names; a board shows far fewer at once.</summary>
    public const int MaxViewport = 2000;

    /// <summary>The largest presence payload relayed, as JSON text (a cursor, a selection, an active field).</summary>
    public const int MaxPresenceCharacters = 2048;

    // shortcut: one colour per account from this palette; the page editor's awareness colours are still the client's, so the web
    // (package W) should take this one everywhere to keep "the same colour per user".
    private static readonly string[] s_colors = ["#c34e62", "#228175", "#457b9d", "#9c683e", "#8556e7", "#ae5f05", "#027cbb", "#b63bc9", "#12863d", "#dc2626"];

    private readonly LiveSubscriptions m_subscriptions;
    private readonly IHubContext<LiveHub> m_hub;
    private readonly IServiceScopeFactory m_scopes;
    private readonly TimeProvider m_clock;
    private readonly Lock m_lock = new();
    private readonly Dictionary<string, Session> m_sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Participant> m_participants = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<Session>> m_index = [];

    public ViewSessions(LiveSubscriptions subscriptions, IHubContext<LiveHub> hub, IServiceScopeFactory scopes, TimeProvider clock)
    {
        m_subscriptions = subscriptions;
        m_hub = hub;
        m_scopes = scopes;
        m_clock = clock;
    }

    /// <summary>The channel of an object's subscriptions.</summary>
    public static string Channel(Guid objectId) => ObjectLiveChannel.Prefix + ":" + objectId.ToString("D");

    /// <summary>Joins the connection to the view and answers the members it may read, the view first. Joining again answers the
    /// same without counting twice. HubException <c>unsupported</c> (malformed key, no enabled module has the kind),
    /// <c>not_found</c> (the view is missing, trashed or not readable), <c>forbidden</c> (the session no longer proves the account).</summary>
    public async Task<ViewJoined> JoinAsync(string connectionId, string viewKey, CancellationToken ct)
    {
        var (key, kind, viewId) = Parse(viewKey) ?? throw new HubException("unsupported");
        var subscriber = m_subscriptions.Find(connectionId) ?? throw new HubException("forbidden");
        var session = await AcquireAsync(key, viewId, ct);
        try
        {
            if (!session.Loaded)
            {
                var list = await MembersAsync(kind, viewId, ct);
                if (!list.Supported || list.Members.Count == 0)
                {
                    throw new HubException(list.Supported ? "not_found" : "unsupported");
                }
                lock (m_lock)
                {
                    Load(session, list.Members);
                }
            }
            var decided = await ResolveAsync(subscriber, Snapshot(session).Keys, ct);
            lock (m_lock)
            {
                if (!decided.TryGetValue(viewId, out var self) || !self.Readable)
                {
                    Settle(connectionId, decided.Keys);
                    throw new HubException(self?.Outcome == LiveAccessOutcome.Forbidden ? "forbidden" : "not_found");
                }
                var participant = m_participants.TryGetValue(connectionId, out var known) ? known : m_participants[connectionId] = new Participant(subscriber);
                var readable = session.Members.Values.Where(member => decided.TryGetValue(member.ObjectId, out var access) && access.Readable).ToArray();
                if (!participant.Views.Contains(key))
                {
                    participant.Views.Add(key);
                    session.Viewports[connectionId] = null;
                    foreach (var member in readable)
                    {
                        Reference(participant, member.ObjectId);
                    }
                }
                Settle(connectionId, decided.Keys);
                return new ViewJoined(readable.Select(member => member with { Version = Math.Max(member.Version, decided[member.ObjectId].Version) }).ToArray());
            }
        }
        finally
        {
            Release(session);
        }
    }

    public async Task LeaveAsync(string connectionId, string viewKey)
    {
        if (Parse(viewKey) is not var (key, _, _) || Find(key) is not { } session)
        {
            return;
        }
        await session.Gate.WaitAsync();
        try
        {
            lock (m_lock)
            {
                if (!session.Closed)
                {
                    Remove(connectionId, session);
                }
            }
        }
        finally
        {
            Release(session);
        }
    }

    /// <summary>The members on the connection's screen get detail, the others a summary; until the first call everything is detail.</summary>
    public void SetViewport(string connectionId, string viewKey, IReadOnlyCollection<Guid> objectIds)
    {
        if (objectIds is null || objectIds.Count > MaxViewport)
        {
            throw new HubException("invalid_viewport");
        }
        lock (m_lock)
        {
            if (Parse(viewKey) is not var (key, _, _) || !m_sessions.TryGetValue(key, out var session) || !session.Viewports.ContainsKey(connectionId))
            {
                throw new HubException("forbidden");
            }
            session.Viewports[connectionId] = objectIds.ToHashSet();
        }
    }

    /// <summary>Presence at an object of a joined view; needs write access as last decided. Past the connection's presence budget a
    /// message is dropped. HubException <c>forbidden</c> or <c>invalid_presence</c> (not a JSON object, or too large).</summary>
    public async Task SendPresenceAsync(string connectionId, Guid objectId, JsonElement payload)
    {
        var channel = Channel(objectId);
        Participant sender;
        lock (m_lock)
        {
            if (!m_participants.TryGetValue(connectionId, out sender!) || !sender.References.ContainsKey(objectId) || m_subscriptions.CanWrite(connectionId, channel) != true)
            {
                throw new HubException("forbidden");
            }
        }
        if (!m_subscriptions.TryTakePresence(connectionId))
        {
            return;
        }
        if (payload.ValueKind != JsonValueKind.Object || payload.GetRawText().Length > MaxPresenceCharacters)
        {
            throw new HubException("invalid_presence");
        }
        string? via;
        List<(string Connection, bool Detail, bool SeesVia)> recipients;
        lock (m_lock)
        {
            // The view the sender joined first among those showing the object: where they work on it.
            via = sender.Views.FirstOrDefault(key => m_sessions.TryGetValue(key, out var session) && session.Members.ContainsKey(objectId));
            var viaChannel = via is null ? null : Channel(m_sessions[via].ViewObjectId);
            recipients = Recipients(objectId, connectionId, false)
                .Select(recipient => (recipient.Connection, recipient.Detail, viaChannel is not null && m_subscriptions.CanWrite(recipient.Connection, viaChannel) is not null))
                .ToList();
        }
        var from = sender.Subscriber;
        var color = s_colors[(int)(BitConverter.ToUInt32(from.UserId.ToByteArray(), 0) % (uint)s_colors.Length)];
        foreach (var group in recipients.GroupBy(recipient => (recipient.Detail, recipient.SeesVia)))
        {
            var message = new ObjectPresenceMessage(objectId, from.UserId, from.DisplayName, color, group.Key.Detail ? payload.Clone() : null, group.Key.SeesVia ? via : null);
            await m_hub.Clients.Clients(group.Select(recipient => recipient.Connection).ToArray()).SendAsync("ObjectPresence", message);
        }
    }

    /// <summary>
    /// A committed change of an object (after the commit that made it): to every participant of a session containing it who may
    /// read it (with <paramref name="writersOnly"/>: who may write it), the change in detail inside the viewport, else a summary.
    /// A change of a view's own object then reads that view's membership again. With <paramref name="readerChange"/> who may only read
    /// the object gets that as its detail instead (a board batch without what its readers may not see).
    /// </summary>
    public async Task ObjectChangedAsync(ObjectChangedMessage message, bool writersOnly = false, JsonNode? readerChange = null)
    {
        List<(string Connection, bool Detail)> recipients;
        HashSet<string> readers = [];
        List<Session> views;
        lock (m_lock)
        {
            if (!m_index.TryGetValue(message.ObjectId, out var sessions))
            {
                return;
            }
            foreach (var session in sessions)
            {
                var member = session.Members[message.ObjectId];
                session.Members[message.ObjectId] = member with { Version = Math.Max(member.Version, message.Version) };
            }
            views = sessions.Where(session => session.ViewObjectId == message.ObjectId).ToList();
            recipients = Recipients(message.ObjectId, null, writersOnly);
            if (readerChange is not null)
            {
                readers = recipients.Where(recipient => m_subscriptions.CanWrite(recipient.Connection, Channel(message.ObjectId)) != true)
                    .Select(recipient => recipient.Connection).ToHashSet(StringComparer.Ordinal);
            }
        }
        var detail = recipients.Where(recipient => recipient.Detail && !readers.Contains(recipient.Connection)).Select(recipient => recipient.Connection).ToArray();
        var readerDetail = recipients.Where(recipient => recipient.Detail && readers.Contains(recipient.Connection)).Select(recipient => recipient.Connection).ToArray();
        var summary = recipients.Where(recipient => !recipient.Detail).Select(recipient => recipient.Connection).ToArray();
        if (detail.Length > 0)
        {
            await m_hub.Clients.Clients(detail).SendAsync("ObjectChanged", message);
        }
        if (readerDetail.Length > 0)
        {
            await m_hub.Clients.Clients(readerDetail).SendAsync("ObjectChanged", message with { Change = readerChange });
        }
        if (summary.Length > 0)
        {
            await m_hub.Clients.Clients(summary).SendAsync("ObjectChanged", message with { Change = null });
        }
        foreach (var view in views)
        {
            await RefreshAsync(view);
        }
    }

    /// <summary>
    /// The object may be readable again (a restore): every participant of a session containing it that holds no subscription of it
    /// (revoked by the trash, or unreadable when it joined) is decided again, and who may read it now is told ViewMembers with it as
    /// an added member, at the version it has, and follows it again in each of its views that show it. Nobody else hears anything.
    /// </summary>
    public async Task ReadmitAsync(Guid objectId)
    {
        List<Session> sessions;
        lock (m_lock)
        {
            sessions = m_index.TryGetValue(objectId, out var found) ? [.. found] : [];
        }
        foreach (var session in sessions)
        {
            await session.Gate.WaitAsync();
            try
            {
                (string Connection, LiveSubscriber Subscriber)[] outside;
                lock (m_lock)
                {
                    outside = session.Closed || !session.Members.ContainsKey(objectId) ? []
                        : session.Viewports.Keys.Where(connection => !m_participants[connection].References.ContainsKey(objectId))
                            .Select(connection => (connection, m_participants[connection].Subscriber)).ToArray();
                }
                foreach (var (connection, subscriber) in outside)
                {
                    var decided = await ResolveAsync(subscriber, [objectId], CancellationToken.None);
                    ViewMembersMessage? message = null;
                    lock (m_lock)
                    {
                        if (decided[objectId].Readable && session.Members.TryGetValue(objectId, out var member) && session.Viewports.ContainsKey(connection)
                            && m_participants.TryGetValue(connection, out var participant) && !participant.References.ContainsKey(objectId))
                        {
                            // One reference per view showing it, as the joins would have counted.
                            participant.References[objectId] = participant.Views.Count(key => m_sessions.TryGetValue(key, out var view) && view.Members.ContainsKey(objectId));
                            participant.Lingering.Remove(objectId);
                            message = new ViewMembersMessage(session.Key, [member with { Version = Math.Max(member.Version, decided[objectId].Version) }], []);
                        }
                        Settle(connection, decided.Keys);
                    }
                    if (message is not null)
                    {
                        await m_hub.Clients.Client(connection).SendAsync("ViewMembers", message);
                    }
                }
            }
            finally
            {
                Release(session);
            }
        }
    }

    /// <summary>The relay's sweep removed the connection's subscription of the object: it is told once, forgets the object, and
    /// leaves the views that are the object.</summary>
    public async Task RevokedAsync(string connectionId, Guid objectId)
    {
        List<string> views;
        lock (m_lock)
        {
            if (!m_participants.TryGetValue(connectionId, out var participant))
            {
                return;
            }
            participant.References.Remove(objectId);
            participant.Lingering.Remove(objectId);
            views = participant.Views.Where(key => m_sessions.TryGetValue(key, out var session) && session.ViewObjectId == objectId).ToList();
        }
        await m_hub.Clients.Client(connectionId).SendAsync("ObjectRevoked", new ObjectRevokedMessage(objectId));
        foreach (var key in views)
        {
            await LeaveAsync(connectionId, key);
        }
    }

    public async Task DisconnectAsync(string connectionId)
    {
        string[] views;
        lock (m_lock)
        {
            views = m_participants.TryGetValue(connectionId, out var participant) ? participant.Views.ToArray() : [];
        }
        foreach (var key in views)
        {
            await LeaveAsync(connectionId, key);
        }
        lock (m_lock)
        {
            m_participants.Remove(connectionId);
        }
    }

    /// <summary>Ends the subscriptions that outlived their last view by <see cref="Linger"/> (the relay's refresh tick).</summary>
    public void ExpireLingering()
    {
        var now = m_clock.GetUtcNow();
        lock (m_lock)
        {
            foreach (var (connectionId, participant) in m_participants.ToArray())
            {
                foreach (var (objectId, until) in participant.Lingering.ToArray())
                {
                    if (until <= now)
                    {
                        participant.Lingering.Remove(objectId);
                        m_subscriptions.Unsubscribe(connectionId, Channel(objectId));
                    }
                }
                if (participant.Views.Count == 0 && participant.Lingering.Count == 0)
                {
                    m_participants.Remove(connectionId);
                }
            }
        }
    }

    /// <summary>Whether a live session contains the object (a change of it has someone to reach).</summary>
    public bool Contains(Guid objectId)
    {
        lock (m_lock)
        {
            return m_index.ContainsKey(objectId);
        }
    }

    /// <summary>Reads the view's membership again and tells each participant what was added that it may read and what it was
    /// told of that was removed.</summary>
    private async Task RefreshAsync(Session session)
    {
        await session.Gate.WaitAsync();
        try
        {
            if (session.Closed)
            {
                return;
            }
            var (kind, viewId) = (session.Key[..session.Key.IndexOf(':')], session.ViewObjectId);
            var list = await MembersAsync(kind, viewId, CancellationToken.None);
            if (list.Members.Count == 0)
            {
                // The view is gone: the sweep revokes it from its participants.
                return;
            }
            ViewMember[] added;
            Guid[] removed;
            (string Connection, LiveSubscriber Subscriber)[] participants;
            lock (m_lock)
            {
                var before = session.Members.Keys.ToHashSet();
                added = list.Members.Where(member => !before.Contains(member.ObjectId)).ToArray();
                removed = before.Except(list.Members.Select(member => member.ObjectId)).ToArray();
                Load(session, list.Members);
                participants = session.Viewports.Keys.Select(connection => (connection, m_participants[connection].Subscriber)).ToArray();
            }
            if (added.Length == 0 && removed.Length == 0)
            {
                return;
            }
            foreach (var (connection, subscriber) in participants)
            {
                var decided = await ResolveAsync(subscriber, added.Select(member => member.ObjectId).ToArray(), CancellationToken.None);
                ViewMembersMessage? message = null;
                lock (m_lock)
                {
                    if (session.Viewports.ContainsKey(connection) && m_participants.TryGetValue(connection, out var participant))
                    {
                        var told = removed.Where(participant.References.ContainsKey).ToArray();
                        foreach (var objectId in told)
                        {
                            Unreference(participant, objectId);
                        }
                        var readable = added.Where(member => decided[member.ObjectId].Readable).ToArray();
                        foreach (var member in readable)
                        {
                            Reference(participant, member.ObjectId);
                        }
                        if (readable.Length > 0 || told.Length > 0)
                        {
                            message = new ViewMembersMessage(session.Key,
                                readable.Select(member => member with { Version = Math.Max(member.Version, decided[member.ObjectId].Version) }).ToArray(), told);
                        }
                    }
                    Settle(connection, decided.Keys);
                }
                if (message is not null)
                {
                    await m_hub.Clients.Client(connection).SendAsync("ViewMembers", message);
                }
            }
        }
        finally
        {
            Release(session);
        }
    }

    /// <summary>
    /// The decision for each object: the subscription's when the connection holds one (the cache), else a new one. A new
    /// subscription is registered undecided before the authorization reads anything, so a sweep that starts meanwhile decides it
    /// too and the newer decision wins (<see cref="LiveSubscriptions.Decide"/>); until it is referenced it delivers nothing.
    /// </summary>
    private async Task<Dictionary<Guid, Decision>> ResolveAsync(LiveSubscriber subscriber, IReadOnlyCollection<Guid> objectIds, CancellationToken ct)
    {
        var connection = subscriber.ConnectionId;
        var needed = objectIds.Where(id => m_subscriptions.CanWrite(connection, Channel(id)) is null).ToArray();
        foreach (var id in needed)
        {
            m_subscriptions.Subscribe(connection, Channel(id), false);
        }
        var ticket = m_subscriptions.NextTicket();
        IReadOnlyDictionary<Guid, LiveAccess> decisions;
        try
        {
            decisions = needed.Length == 0 ? new Dictionary<Guid, LiveAccess>() : await ObjectLiveChannel.AuthorizeAsync(m_scopes, subscriber, needed, ct);
        }
        catch
        {
            lock (m_lock)
            {
                Settle(connection, needed, forget: true);
            }
            throw;
        }
        foreach (var id in needed)
        {
            var access = decisions[id];
            m_subscriptions.Decide(connection, Channel(id), ticket, access.Allowed ? access.CanWrite : null);
        }
        return objectIds.Distinct().ToDictionary(id => id, id =>
        {
            var outcome = decisions.TryGetValue(id, out var access) ? access.Outcome : LiveAccessOutcome.Allowed;
            return new Decision(m_subscriptions.CanWrite(connection, Channel(id)) is not null, outcome, access?.Sequence ?? 0);
        });
    }

    /// <summary>Subscriptions among <paramref name="objectIds"/> the connection holds but no view references start their linger
    /// (or end at once with <paramref name="forget"/>, or when the connection is no participant).</summary>
    private void Settle(string connectionId, IEnumerable<Guid> objectIds, bool forget = false)
    {
        m_participants.TryGetValue(connectionId, out var participant);
        foreach (var id in objectIds)
        {
            if (participant?.References.ContainsKey(id) == true || m_subscriptions.CanWrite(connectionId, Channel(id)) is null)
            {
                continue;
            }
            if (participant is null || forget)
            {
                m_subscriptions.Unsubscribe(connectionId, Channel(id));
            }
            else
            {
                participant.Lingering.TryAdd(id, m_clock.GetUtcNow() + Linger);
            }
        }
    }

    private List<(string Connection, bool Detail)> Recipients(Guid objectId, string? exceptConnectionId, bool writersOnly)
    {
        var detail = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var session in m_index.GetValueOrDefault(objectId) ?? [])
        {
            foreach (var (connection, viewport) in session.Viewports)
            {
                if (connection == exceptConnectionId || !m_participants[connection].References.ContainsKey(objectId)
                    || m_subscriptions.CanWrite(connection, Channel(objectId)) is not { } canWrite || writersOnly && !canWrite)
                {
                    continue;
                }
                detail[connection] = detail.GetValueOrDefault(connection) || viewport is null || viewport.Contains(objectId);
            }
        }
        return detail.Select(item => (item.Key, item.Value)).ToList();
    }

    private void Reference(Participant participant, Guid objectId)
    {
        participant.References[objectId] = participant.References.GetValueOrDefault(objectId) + 1;
        participant.Lingering.Remove(objectId);
    }

    private void Unreference(Participant participant, Guid objectId)
    {
        if (!participant.References.TryGetValue(objectId, out var count))
        {
            return;
        }
        if (count > 1)
        {
            participant.References[objectId] = count - 1;
            return;
        }
        participant.References.Remove(objectId);
        participant.Lingering[objectId] = m_clock.GetUtcNow() + Linger;
    }

    /// <summary>Takes the connection out of the session (under the session's gate and the lock); an empty session closes.</summary>
    private void Remove(string connectionId, Session session)
    {
        if (session.Viewports.Remove(connectionId) && m_participants.TryGetValue(connectionId, out var participant))
        {
            participant.Views.Remove(session.Key);
            foreach (var objectId in session.Members.Keys)
            {
                Unreference(participant, objectId);
            }
        }
    }

    private void Load(Session session, IReadOnlyList<ViewMember> members)
    {
        foreach (var objectId in session.Members.Keys)
        {
            Unindex(session, objectId);
        }
        session.Members.Clear();
        foreach (var member in members)
        {
            session.Members[member.ObjectId] = member;
            (m_index.TryGetValue(member.ObjectId, out var sessions) ? sessions : m_index[member.ObjectId] = []).Add(session);
        }
        session.Loaded = true;
    }

    private void Unindex(Session session, Guid objectId)
    {
        if (m_index.TryGetValue(objectId, out var sessions) && sessions.Remove(session) && sessions.Count == 0)
        {
            m_index.Remove(objectId);
        }
    }

    private Dictionary<Guid, ViewMember> Snapshot(Session session)
    {
        lock (m_lock)
        {
            return new Dictionary<Guid, ViewMember>(session.Members);
        }
    }

    private Session? Find(string key)
    {
        lock (m_lock)
        {
            return m_sessions.GetValueOrDefault(key);
        }
    }

    /// <summary>The session's gate, creating the session when there is none; a session closed while waiting is replaced.</summary>
    private async Task<Session> AcquireAsync(string key, Guid viewId, CancellationToken ct)
    {
        while (true)
        {
            Session session;
            lock (m_lock)
            {
                session = m_sessions.TryGetValue(key, out var open) ? open : m_sessions[key] = new Session(key, viewId);
            }
            await session.Gate.WaitAsync(ct);
            if (!session.Closed)
            {
                return session;
            }
            session.Gate.Release();
        }
    }

    /// <summary>Releases the gate; a session without participants closes first.</summary>
    private void Release(Session session)
    {
        lock (m_lock)
        {
            if (!session.Closed && session.Viewports.Count == 0)
            {
                session.Closed = true;
                m_sessions.Remove(session.Key);
                foreach (var objectId in session.Members.Keys)
                {
                    Unindex(session, objectId);
                }
            }
        }
        session.Gate.Release();
    }

    private async Task<ViewMemberList> MembersAsync(string kind, Guid viewId, CancellationToken ct)
    {
        await using var scope = m_scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<ViewMemberList>(new GetViewMembers(kind, viewId), ct);
    }

    /// <summary><c>{kind}:{viewId}</c> with any Guid format, canonical as the key (lowercase "D").</summary>
    private static (string Key, string Kind, Guid ViewId)? Parse(string? viewKey)
    {
        var separator = viewKey?.IndexOf(':') ?? -1;
        if (separator <= 0 || !Guid.TryParse(viewKey![(separator + 1)..], out var id))
        {
            return null;
        }
        var kind = viewKey[..separator];
        return (kind + ":" + id.ToString("D"), kind, id);
    }

    private sealed class Session(string key, Guid viewObjectId)
    {
        public string Key { get; } = key;
        public Guid ViewObjectId { get; } = viewObjectId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<Guid, ViewMember> Members { get; } = [];
        /// <summary>The participants and their viewports (null: everything in detail).</summary>
        public Dictionary<string, HashSet<Guid>?> Viewports { get; } = new(StringComparer.Ordinal);
        public bool Loaded { get; set; }
        public bool Closed { get; set; }
    }

    private sealed class Participant(LiveSubscriber subscriber)
    {
        public LiveSubscriber Subscriber { get; } = subscriber;
        /// <summary>Joined views in join order (the first one showing an object is the presence's <c>via</c>).</summary>
        public List<string> Views { get; } = [];
        /// <summary>Objects it was told of, with the number of its views that show them.</summary>
        public Dictionary<Guid, int> References { get; } = [];
        /// <summary>Subscriptions no view references any more, until when they are kept.</summary>
        public Dictionary<Guid, DateTimeOffset> Lingering { get; } = [];
    }

    private sealed record Decision(bool Readable, LiveAccessOutcome Outcome, long Version);
}
