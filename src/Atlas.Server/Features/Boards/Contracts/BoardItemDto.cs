using System.Text.Json;
using System.Text.Json.Serialization;
using Atlas.Server.Features.Boards.Domain;

namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>An item of BoardDto with its kind's props (BoardProps) and the group or frame it belongs to (Parent); the members a
/// kind does not have are left out. Color is a board colour (BoardEditing.Palette) or an appearance-palette id (<c>blue-4</c>, the
/// <c>GET /appearance</c> palette); on a <c>block</c> it is the editor component's background. Height of a <c>block</c> whose
/// content sets it (table, code, callout, contact, YouTrack issue) is its default until a person moves or resizes it,
/// not its drawn height: its Width binds, a client placing items around it leaves room below.</summary>
public sealed record BoardItemDto(
    Guid Id,
    string Kind,
    double X,
    double Y,
    double Width,
    double Height,
    int Z,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color,
    JsonElement Props,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? From,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? To,
    long Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BoardTargetDto? Target,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? Parent = null)
{
    public static BoardItemDto Of(BoardItemState item, BoardTargetDto? target)
    {
        return new BoardItemDto(item.Id, item.Kind, item.X, item.Y, item.Width, item.Height, item.Z, item.Text, item.Color, BoardJson.Element(item.Props),
            item.FromItemId, item.ToItemId, item.Version, target, item.ParentId);
    }
}
