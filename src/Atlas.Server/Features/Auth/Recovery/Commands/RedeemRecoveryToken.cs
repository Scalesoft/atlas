namespace Atlas.Server.Features.Auth.Recovery.Commands;

public sealed record RedeemRecoveryToken(string Token, string NewPassword)
{
    public override string ToString() => "RedeemRecoveryToken { secrets redacted }";
}
