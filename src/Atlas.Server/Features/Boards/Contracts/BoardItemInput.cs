using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>The new item of a <c>create</c> change. <c>page</c> needs TargetResourceId, <c>connector</c> From and To, the other
/// kinds neither; Props are the kind's full props (BoardProps), none meaning <c>{}</c>.</summary>
public sealed record BoardItemInput(
    Guid? Id,
    string? Kind,
    double? X,
    double? Y,
    double? Width,
    double? Height,
    long? Z,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Color = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? TargetResourceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? From = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? To = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Props = null);
