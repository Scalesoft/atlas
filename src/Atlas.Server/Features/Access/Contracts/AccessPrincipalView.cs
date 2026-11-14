namespace Atlas.Server.Features.Access.Contracts;

/// <summary>Who the card is about. Kind user or group; Detail is the username or the member count. LastSignInAt and Groups are
/// wiki-wide facts and come only with scope wiki or self.</summary>
public sealed record AccessPrincipalView(string Kind, Guid Id, string Name, string? Detail, bool? IsActive = null, bool? IsWikiAdmin = null,
    DateTime? LastSignInAt = null, IReadOnlyList<AccessGroupRef>? Groups = null);
