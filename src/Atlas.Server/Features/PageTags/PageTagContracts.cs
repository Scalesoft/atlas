namespace Atlas.Server.Features.PageTags;

public sealed record TagDto(Guid Id, string Name);
public sealed record PageTagState(IReadOnlyList<TagDto> Tags);
public sealed record TagSuggestions(IReadOnlyList<TagDto> Items);
public sealed record SetPageTagsRequest(string[] Names, Guid[] ExpectedTagIds);
/// <summary>Adds and removes tags by name and keeps the others; ExpectedTagIds is optional (null = no compare-and-set).</summary>
public sealed record ChangePageTagsRequest(string[]? Add, string[]? Remove, Guid[]? ExpectedTagIds);
public sealed record ChangePageTags(Guid PageId, ChangePageTagsRequest Request);
public sealed record SetPageTags(Guid PageId, SetPageTagsRequest Request);
