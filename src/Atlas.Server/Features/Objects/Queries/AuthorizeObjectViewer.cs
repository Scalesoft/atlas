using Atlas.Server.Platform.Live;

namespace Atlas.Server.Features.Objects.Queries;

/// <summary>Live access of a handshake account to objects (view sessions and their revalidation), in one transaction.</summary>
public sealed record AuthorizeObjectViewer(Guid UserId, string SecurityStamp, Guid? SessionId, IReadOnlyList<Guid> ObjectIds);

/// <summary>A decision for every requested id; the sequence of an allowed one is the object's version.</summary>
public sealed record ObjectViewerAccess(IReadOnlyDictionary<Guid, LiveAccess> Access);
