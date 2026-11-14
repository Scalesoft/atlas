namespace Atlas.Server.Features.Access.Contracts;

/// <summary>Who has access to one resource. Automatic: locked rows of people with access without a grant here. Entries, view direct:
/// the grants set on the resource; view effective: also everyone reaching it through a group or the space defaults, at most 200
/// (then HasMore). Exceptions: kind space only, the pages, vaults and secrets whose access differs from the whole space.</summary>
public sealed record ResourceAccess(AccessResource Resource, bool CanManage, IReadOnlyList<AutomaticAccess> Automatic,
    IReadOnlyList<ResourceAccessEntry> Entries, bool HasMore, IReadOnlyList<AccessExceptionItem>? Exceptions = null);
