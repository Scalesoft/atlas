namespace Atlas.Server.Features.Auth.Contracts;

public sealed record RecoverAdministrator(string Token, string NewPassword)
{
    public override string ToString() => "RecoverAdministrator { secrets redacted }";
}
