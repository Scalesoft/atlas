namespace Atlas.Server.Features.Access.Contracts;

/// <summary>One resource a person or group reaches. Level is the effective level the authorization checks give now (null: the
/// resource is listed, for example a page with own access, but none of its sources applies); Sources are strongest first and
/// include the not effective ones. ResourceId is "wiki" for the wiki kind.</summary>
public sealed record AccessEntry(string Kind, string ResourceId, string Title, string? ParentTitle, Guid? SpaceId, string? SpaceName,
    string? Level, IReadOnlyList<AccessEntrySource> Sources, string? Note = null, string? SpaceIcon = null, string? SpaceColor = null);
