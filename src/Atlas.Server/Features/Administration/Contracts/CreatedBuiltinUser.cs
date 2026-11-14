namespace Atlas.Server.Features.Administration.Contracts;

/// <summary>A new account with its invitation code (Purpose invitation, seven days).</summary>
public sealed record CreatedBuiltinUser(UserSummary User, string Token, DateTime ExpiresAt, string Purpose)
{
    public override string ToString() => $"CreatedBuiltinUser {{ UserId = {User.Id}, secret redacted }}";
}
