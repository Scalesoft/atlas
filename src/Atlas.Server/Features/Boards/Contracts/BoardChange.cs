using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>
/// One change of a batch (boards spec §5, interaction spec §4), flat so one JSON shape serves every type:
/// <c>create</c> {item}, <c>move</c> {itemId, x, y, width, height}, <c>set</c> {itemId, text?, color?, props?}, <c>reorder</c>
/// {itemId, z}, <c>delete</c>/<c>restore</c> {itemId}, <c>parent</c> {itemId, parentId?} (none takes the item out of its group or
/// frame), <c>rename</c> {title}, <c>settings</c> {settings} (the board's, a part as for
/// props). An absent or null property is not changed:
/// <c>set</c> with <c>text: ""</c> empties the text, <c>color: ""</c> removes the colour (a colour is one of BoardEditing.Palette or an
/// appearance-palette id such as <c>blue-4</c>; on a <c>block</c> it is the component's background), and <c>props</c> names only the keys it changes
/// (<c>null</c> removes an optional one). Stored in the operation log and broadcast as applied (BoardEditing.Normalize), without
/// the null members.
/// </summary>
public sealed record BoardChange(
    string? Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BoardItemInput? Item = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ItemId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? X = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Y = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Width = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Height = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Z = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Props = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Settings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ParentId = null);
