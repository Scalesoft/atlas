using System.Text.Json.Nodes;

namespace Atlas.Server.Platform.Objects;

/// <summary>
/// The strategy of the <see cref="ObjectEditingModel.Fields"/> and <see cref="ObjectEditingModel.DomainCommands"/> editing models:
/// validates and applies a change in the object path's transaction, after the path has locked the envelope, decided write access,
/// checked the expected version and the idempotency of the change id. A Fields type keeps its data in the envelope
/// (<see cref="ObjectWrite.Data"/>); a DomainCommands type may write its own tables instead and answer null data. Throws
/// <see cref="ObjectChangeRejected"/> for a change it refuses.
/// </summary>
public interface IObjectChangeHandler
{
    Task<ObjectWrite> CreateAsync(ObjectChangeContext context, JsonObject data, CancellationToken ct);

    Task<ObjectWrite> ApplyAsync(ObjectChangeContext context, JsonObject payload, CancellationToken ct);
}

/// <summary>The object being written: its id, the writing account, its stored data (null before the first write) and its scope
/// (<see cref="Atlas.Server.Platform.Resources.ObjectScopes"/>; the space of a space object).</summary>
public sealed record ObjectChangeContext(Guid ObjectId, ObjectActor Actor, JsonObject? Data, string Scope, Guid? SpaceId);

/// <summary>
/// A type whose wiki objects more accounts than the WikiAdmins create and change (the contact directory). The object path asks it
/// when an object is created in the wiki and the type's own AuthorizeAsync answers changes the same way; without it the wiki is
/// the WikiAdmins'.
/// </summary>
public interface IWikiWriters
{
    Task<bool> MayWriteWikiAsync(ObjectActor actor, CancellationToken ct);
}

/// <summary>The data to store (null: the type stores its own), the plain-text title for the library and what subscribers hear.</summary>
public sealed record ObjectWrite(JsonObject? Data, string Title, JsonNode Change);

/// <summary>A change the type refuses: answered 400 invalid_object_change, with the type's message.</summary>
public sealed class ObjectChangeRejected(string message) : Exception(message);
