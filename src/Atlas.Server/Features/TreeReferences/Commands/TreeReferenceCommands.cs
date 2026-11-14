using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.TreeReferences.Contracts;

namespace Atlas.Server.Features.TreeReferences.Commands;

/// <summary>Places a reference node to <see cref="AddTreeReferenceRequest.TargetId"/> into the space tree; answers the node.</summary>
public sealed record AddTreeReference(Guid SpaceId, AddTreeReferenceRequest Request);

/// <summary>Removes the node, only the link: the target stays as it is.</summary>
public sealed record RemoveTreeReference(Guid ReferenceId);

/// <summary>Moves the node in its space with the page organization's request (parent, position, tree version).</summary>
public sealed record MoveTreeReference(Guid ReferenceId, MovePageRequest Request);
