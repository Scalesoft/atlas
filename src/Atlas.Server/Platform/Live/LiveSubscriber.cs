namespace Atlas.Server.Platform.Live;

/// <summary>A connection of /hubs/live as its handshake authenticated it. The stamp is the session proof the channels compare
/// with the account's current one; it is never sent to anyone.</summary>
public sealed record LiveSubscriber(string ConnectionId, Guid UserId, string DisplayName, string SecurityStamp, Guid? SessionId);
