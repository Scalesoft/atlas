namespace Atlas.Server.Features.Auth.OpenIdConnect.Commands;

/// <summary>
/// Named Resolve…, not Link…, because the wire contract LinkExternalIdentity (the POST body) keeps its name.
/// LinkUserId/LinkSecurityStamp are set only for an explicit link started by a signed-in user. Email is the provider's verified address, kept on a
/// new account only: it finds the person's directory contact (AccountContacts), never an account.
/// </summary>
public sealed record ResolveExternalIdentity(string Issuer, bool AllowSignup, string Subject, string? DisplayName, Guid? LinkUserId, string? LinkSecurityStamp, bool GrantsWikiAdmin = false,
    string? Email = null);
