namespace Atlas.Server.Platform.Objects;

/// <summary>
/// A kind of view (objects spec §4.2) whose view sessions the server keeps: <c>{Kind}:{objectId}</c>, the view being an object of
/// <see cref="TypeId"/> itself. Registered by its module as a scoped service; a disabled module registers none, so joining its
/// views fails with <c>unsupported</c>.
/// </summary>
public interface IObjectView
{
    /// <summary>The view key prefix, e.g. <c>board</c>.</summary>
    string Kind { get; }

    /// <summary>The type of the object the view is.</summary>
    string TypeId { get; }

    /// <summary>The objects the view shows besides itself, as its data says now; unknown ids are dropped by the caller. One
    /// query, in the caller's transaction.</summary>
    Task<IReadOnlyCollection<Guid>> MembersAsync(Guid viewId, CancellationToken ct);
}
