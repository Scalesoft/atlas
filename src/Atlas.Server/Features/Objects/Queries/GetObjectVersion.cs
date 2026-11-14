namespace Atlas.Server.Features.Objects.Queries;

/// <summary>The version of an object for a change announced without one (the database's page notifications).</summary>
public sealed record GetObjectVersion(Guid ObjectId);

/// <summary>Null when the object is missing.</summary>
public sealed record ObjectVersion(long? Version);
