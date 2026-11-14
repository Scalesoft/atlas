namespace Atlas.Server.Platform.Objects;

/// <summary>
/// A type of object (objects spec §2.1, F1 plan §2.2), registered by its module as a scoped service; the registry
/// (<see cref="ObjectTypes"/>) knows only what is registered, so a type of a disabled module or an unknown one has no access and
/// is not listed (fail closed). A new type needs nothing but this registration and the strategy of its editing model: the
/// object path, view sessions, search and AI work from these members alone.
/// </summary>
public interface IObjectType
{
    /// <summary>E.g. <c>atlas.page</c>; the ContentResource.TypeId of its objects.</summary>
    string TypeId { get; }

    /// <summary>Which strategy changes it: <see cref="ObjectEditingModel.Fields"/> and <see cref="ObjectEditingModel.DomainCommands"/>
    /// types implement <see cref="IObjectChangeHandler"/>, the others keep their own write paths.</summary>
    ObjectEditingModel EditingModel { get; }

    /// <summary>The account's access to one of its objects; None also for a missing or trashed one (no difference, as HTTP 404).
    /// Runs in the caller's transaction and scope.</summary>
    Task<ObjectAccess> AuthorizeAsync(ObjectActor actor, Guid objectId, CancellationToken ct);

    /// <summary>What search, AI and export read of the object (its readable public form, never a draft), null when it is
    /// missing. Indexed once per object, never per placement.</summary>
    Task<ObjectProjection?> ProjectAsync(Guid objectId, CancellationToken ct);
}

public enum ObjectEditingModel
{
    YjsText,
    YjsTable,
    Fields,
    BoardOps,
    DomainCommands,
    External
}

public enum ObjectAccess
{
    None,
    Read,
    Write
}

/// <summary>The account a decision is for, as its caller authenticated and locked or re-read it.</summary>
public sealed record ObjectActor(UserAccount Account);

/// <summary>
/// The object as text (spec §1a): its plain-text title, its Markdown and plain text, and the chunks an AI index embeds, each with
/// the anchor inside the object a citation points to (a heading id, a board item id; null for the object as a whole).
/// </summary>
public sealed record ObjectProjection(string Title, string Markdown, string PlainText, IReadOnlyList<ObjectChunk> Chunks);

/// <summary>One embeddable unit of an object: where it is (<see cref="Anchor"/>), its heading path and its plain text.</summary>
public sealed record ObjectChunk(string? Anchor, string Heading, string Text);
