using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Objects.Contracts;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;

namespace Atlas.Server.Features.Objects.Domain;

/// <summary>The rules of the envelope the object path writes: the shape of a scope, the bounds of stored JSON, and the DTO.</summary>
public static class ObjectEnvelopes
{
    /// <summary>A backstop for data and a change payload, serialized as stored: above every type's own limit (a note's 100 000
    /// characters, a line break counting two), so the type refuses with its own message (PER2-2). Never a document.</summary>
    public const int MaxJsonCharacters = 256 * 1024;

    // jsonb keeps a non-ASCII character as one; the default encoder's \uXXXX escapes would count six.
    private static readonly JsonSerializerOptions s_stored = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const int MaxTitleCharacters = 400;

    public static bool IsValidScope(string? scope, Guid? spaceId)
    {
        return scope switch
        {
            ObjectScopes.Space => spaceId is { } id && id != Guid.Empty,
            ObjectScopes.Wiki or ObjectScopes.Private => spaceId is null,
            _ => false
        };
    }

    /// <summary>The JSON as stored, or the refusal of one too large to store.</summary>
    public static string Serialize(JsonNode node)
    {
        var json = node.ToJsonString(s_stored);
        return json.Length <= MaxJsonCharacters ? json : throw ObjectErrors.InvalidChange("Objekt nebo změna je příliš velká.");
    }

    public static string Title(string title)
    {
        title = title.Trim();
        return title.Length <= MaxTitleCharacters ? title : title[..MaxTitleCharacters];
    }

    public static ObjectDto ToDto(ContentResource envelope, IObjectType type, bool canEdit, string title)
    {
        return new ObjectDto(envelope.Id, envelope.TypeId, envelope.Scope, envelope.SpaceId, envelope.OwnerId, envelope.Version, envelope.CreatedAt,
            envelope.CreatedBy, envelope.UpdatedAt, JsonNamingPolicy.CamelCase.ConvertName(type.EditingModel.ToString()), canEdit, title,
            envelope.Data is { } data ? JsonNode.Parse(data)!.AsObject() : null);
    }
}
