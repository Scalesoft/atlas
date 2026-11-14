namespace Atlas.Server.Features.Auth.Recovery;

public sealed record RecoveryTokenFile(string Token, Guid UserId, string Username, DateTime ExpiresAt)
{
    public override string ToString() => "RecoveryTokenFile { secret redacted }";
}
