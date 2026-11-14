namespace Atlas.Server.Features.Auth.Contracts;

public sealed record LoginRequest(string Username, string Password)
{
    public override string ToString() => "LoginRequest { sensitive values redacted }";
}
