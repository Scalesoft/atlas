namespace Atlas.Server.Features.Access.Contracts;

/// <summary>Something inside a space whose access differs from the whole space. Kind page, vault or secret; Mode own (a page with
/// own access) or grants (grants of its own on top of the space); GrantCount counts its current grants.</summary>
public sealed record AccessExceptionItem(string Kind, Guid Id, string Title, string? ParentTitle, string Mode, int GrantCount);
