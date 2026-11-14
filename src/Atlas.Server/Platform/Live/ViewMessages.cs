using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atlas.Server.Platform.Live;

/// <summary>An object of a view: its type and the version the client catches up from.</summary>
public sealed record ViewMember(Guid ObjectId, string TypeId, long Version);

/// <summary>The answer of JoinView: the view's objects the account may read (the view itself first), never one it may not.</summary>
public sealed record ViewJoined(IReadOnlyList<ViewMember> Members);

/// <summary>ObjectChanged: a confirmed change of an object of a joined view; <see cref="Change"/> only while the object is in
/// the recipient's viewport (detail), null otherwise (summary: "it changed").</summary>
public sealed record ObjectChangedMessage(Guid ObjectId, long Version, JsonNode? Change);

/// <summary>ObjectPresence: a colleague at an object, as the handshake authenticated them. <see cref="Payload"/> only in the
/// recipient's viewport; <see cref="Via"/> the colleague's view only when the recipient may read it ("elsewhere" otherwise).</summary>
public sealed record ObjectPresenceMessage(Guid ObjectId, Guid UserId, string DisplayName, string Color, JsonElement? Payload, string? Via);

/// <summary>ViewMembers: objects added to or removed from a joined view, only those the recipient may read (or was told of).</summary>
public sealed record ViewMembersMessage(string ViewKey, IReadOnlyList<ViewMember> Added, IReadOnlyList<Guid> Removed);

/// <summary>ObjectRevoked: the account lost the object; nothing more of it arrives until it is joined again.</summary>
public sealed record ObjectRevokedMessage(Guid ObjectId);
