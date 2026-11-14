namespace Atlas.Server.Features.Auth.Builtin.Commands;

public sealed record BuiltinLogin(string Username, string Password)
{
    public override string ToString() => "BuiltinLogin { sensitive values redacted }";
}
