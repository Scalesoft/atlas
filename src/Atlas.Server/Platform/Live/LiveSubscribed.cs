namespace Atlas.Server.Platform.Live;

/// <summary>The answer of Subscribe: the channel's sequence after the subscription took effect. Every later change arrives as a
/// message; what the client misses up to this sequence it reads from the channel's log.</summary>
public sealed record LiveSubscribed(long Sequence);
