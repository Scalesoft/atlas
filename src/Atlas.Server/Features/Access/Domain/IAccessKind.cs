using Atlas.Server.Features.Access.Contracts;

namespace Atlas.Server.Features.Access.Domain;

/// <summary>
/// One kind of resource in the security centre (docs/access-model.md §2): a read and write layer over the kind's own storage and
/// its existing evaluators, never a rule of its own. A feature or plugin registers its kind in its AddXxx() with
/// <c>services.AddScoped&lt;IAccessKind, …&gt;()</c>; the access feature collects them all. Levels come from the evaluators the
/// authorization checks use, so an entry never shows access a check would deny, nor hides access it allows.
/// </summary>
public interface IAccessKind
{
    AccessKindDescriptor Descriptor { get; }

    /// <summary>Everything <paramref name="who"/> can reach in this kind, as entries (one per resource, effective level, all
    /// sources). The caller has authorized the card; the kind keeps to <paramref name="scope"/>.</summary>
    Task<IReadOnlyList<AccessEntry>> ForPrincipalAsync(AccessPrincipal who, AccessScope scope, CancellationToken ct);

    /// <summary>Who can reach one resource of this kind, for a caller who may manage it (404 or 403 otherwise). Null when the kind
    /// does not list its resources this way. <paramref name="query"/> narrows the people and groups by name before a capped list is
    /// cut (the space does; a kind whose list is short may ignore it).</summary>
    Task<ResourceAccess?> ForResourceAsync(Guid resourceId, AccessView view, string? query, CancellationToken ct);

    /// <summary>Resources of this kind the caller may grant, for the "K čemu" step, at most <see cref="AccessQuery.Limit"/>. Empty
    /// when not grantable.</summary>
    Task<IReadOnlyList<AccessResource>> SearchAsync(string? query, CancellationToken ct);

    /// <summary>Null for read-only and derived kinds.</summary>
    IAccessGrantWriter? Writer { get; }

    /// <summary>Whether the kind is offered now: a plugin kind is off until its module is configured.</summary>
    Task<bool> IsEnabledAsync(CancellationToken ct) => Task.FromResult(true);
}
