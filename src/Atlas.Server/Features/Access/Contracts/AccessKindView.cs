namespace Atlas.Server.Features.Access.Contracts;

/// <summary>An enabled access kind (GET /api/access/kinds). Parent: the kind its resources live in (space for page, vault and
/// youtrack; vault for secret). Plugin names the module of a plugin kind. Levels: lowest first. Grantable: the kind has a writer
/// and the caller may grant something of it.</summary>
public sealed record AccessKindView(string Id, string One, string Many, string Icon, string? Parent, string? Plugin,
    IReadOnlyList<AccessLevelInfo> Levels, bool Grantable);
