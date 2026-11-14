namespace Atlas.Server.Features.Administration.Domain;

/// <summary>Everything the endpoint needs to refresh the sign-in cookie. Handlers must not return UserAccount: Wolverine
/// recurses on return types it also resolves, and the account is not a wire contract.</summary>
public sealed record ChangedPassword(Guid Id, string Username, string DisplayName, bool IsWikiAdmin, string SecurityStamp)
{
    public override string ToString() => $"ChangedPassword {{ Id = {Id}, stamp redacted }}";
}
