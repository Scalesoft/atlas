namespace Atlas.Server.Features.PageLinks.Contracts;

/// <summary>Link findings of the working revision at Version, the revision Publish would publish; phase 2 adds Similar.</summary>
public sealed record PublicationCheck(int Version, IReadOnlyList<LinkFinding> Links);

/// <summary>A problematic link target. State "unavailable": missing, trashed or not readable by the actor, so Title is always
/// null. State "unpublished": a draft the actor sees but readers will not, with its working title.</summary>
public sealed record LinkFinding(Guid TargetPageId, string State, string? Title);
