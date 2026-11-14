using System.Text.Json.Serialization;

namespace Atlas.Server.Features.PageLinks.Contracts;

public sealed record PageReferencesRequest(IReadOnlyList<Guid>? Ids);

/// <summary>The requested pages the actor may open, with the title it would see; a missing id means unavailable.</summary>
public sealed record PageReferenceList(IReadOnlyList<PageReference> Items);

/// <param name="Type">Another content type's resource (<c>board</c>, IContentReferenceParticipant); null, and not written, for a page.</param>
public sealed record PageReference(Guid Id, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null);
