namespace Atlas.Server.Platform.Live;

/// <summary>Server → client: the subscription of <paramref name="Channel"/> ended because the account lost access.</summary>
public sealed record LiveRevoked(string Channel);
