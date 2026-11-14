using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>A board as its reader sees it at Sequence: its settings (defaults included, BoardProps.WithDefaults), the visible
/// items (no connector with a deleted end) and whether the account may write. <see cref="OwnerPageId"/>: the page whose own board it is
/// (Board.Purpose), null for a board of the space tree.</summary>
public sealed record BoardDto(Guid Id, Guid SpaceId, string Title, long Sequence, bool CanEdit, JsonElement Settings, IReadOnlyList<BoardItemDto> Items)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OwnerPageId { get; init; }
}
