namespace Atlas.Server.Features.Administration.Contracts;

public sealed record ActivateAccount(string Token, string NewPassword)
{
    public override string ToString() => "ActivateAccount { secrets redacted }";
}
