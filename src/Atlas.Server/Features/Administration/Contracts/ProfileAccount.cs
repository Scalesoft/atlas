namespace Atlas.Server.Features.Administration.Contracts;

public sealed record ProfileAccount(string DisplayName, string? Email, string Version, string Source,
    bool CanEditDisplayName, bool CanEditEmail,
    DateTimeOffset? CurrentSessionAuthenticatedAt, string? CurrentSessionAuthenticationMethod,
    DateTime? LastSignInAt, string? LastSignInMethod);
