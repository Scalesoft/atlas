using System.Text.Json.Serialization;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.Search.Contracts;

/// <summary>A hit of another content type (Type <c>board</c>, <c>comment</c>): where it is, its title and a plain-text snippet
/// around the match. A comment's Title is its object's; <see cref="Target"/> (written only for such hits) is the object it leads to.</summary>
public sealed record SearchResourceHit(string Type, Guid Id, Guid SpaceId, string SpaceName, string Title, string Snippet)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContentSearchTarget? Target { get; init; }
}
