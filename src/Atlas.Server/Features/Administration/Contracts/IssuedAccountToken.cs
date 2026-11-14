namespace Atlas.Server.Features.Administration.Contracts;

/// <summary>A one-time code for the account. Purpose: invitation (the account has never set a password; seven days) or reset
/// (thirty minutes).</summary>
public sealed record IssuedAccountToken(string Token, DateTime ExpiresAt, string Purpose)
{
    public override string ToString() => "IssuedAccountToken { secret redacted }";
}
