using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atlas.Server.Features.Pages;

namespace Atlas.Server.Features.Boards.Contracts;

/// <summary>
/// What a card shows of its target (interaction spec §4), as this reader may see it. A page card: its title, the space it is in
/// and the first ~300 characters of its text, of the revision this reader may see (published for a reader, the working one for who
/// may edit). An object card: the object's type, plain-text title, version, data and whether the reader may change it (through
/// the object path). A target the reader may not open, a trashed one and one that no longer exists are all the same: its type
/// only, every content member null, Available false.
/// </summary>
public sealed record BoardTargetDto(string Type, Guid ResourceId, string? Title, string? SpaceTitle, string? Excerpt, bool Available)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? Version { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CanEdit { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonObject? Data { get; init; }
    /// <summary>A page in the trash that the reader may read (Available false, no content): its card says so, with the space to restore it in.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Trashed { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? SpaceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CanRestore { get; init; }

    public static BoardTargetDto Unavailable(Guid resourceId, string type = PagesAtlasModule.ContentType) => new(type, resourceId, null, null, null, false);
}
