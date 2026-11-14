namespace Atlas.Server.Features.Access.Domain;

/// <summary>Whose card is being built: a user (Account is its account row) or a group (Account null).</summary>
public sealed record AccessPrincipal(string Kind, Guid Id, UserAccount? Account)
{
    public const string User = "user";
    public const string Group = "group";

    public bool IsUser => Kind == User;

    public static AccessPrincipal Of(UserAccount account) => new(User, account.Id, account);
}
