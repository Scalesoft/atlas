namespace Atlas.Server.Platform.Live;

/// <summary>
/// Ephemeral presence of SendPresence: a cursor and the items being dragged (a preview until the gesture's batch commits).
/// Typed, so only these numbers are relayed, never what else a client put in the payload; no history, may be dropped.
/// </summary>
public sealed record LivePresence(LivePoint? Cursor, IReadOnlyList<LiveDrag>? Dragging)
{
    public const int MaxDragging = 50;

    public bool IsValid()
    {
        return (Cursor is null || double.IsFinite(Cursor.X) && double.IsFinite(Cursor.Y))
            && (Dragging is null || Dragging.Count <= MaxDragging && Dragging.All(drag => drag is not null
                && double.IsFinite(drag.X) && double.IsFinite(drag.Y) && double.IsFinite(drag.Width) && double.IsFinite(drag.Height)));
    }
}

public sealed record LivePoint(double X, double Y);

public sealed record LiveDrag(Guid ItemId, double X, double Y, double Width, double Height);

/// <summary>Server → the other subscribers: whose presence it is (the handshake account, never a client claim).</summary>
public sealed record LivePresenceMessage(string Channel, Guid UserId, string DisplayName, LivePresence Payload);
