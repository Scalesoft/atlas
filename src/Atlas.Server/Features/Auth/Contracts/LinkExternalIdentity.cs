namespace Atlas.Server.Features.Auth.Contracts;

public sealed record LinkExternalIdentity(string? Password, string? ReturnUrl)
{
    public override string ToString() => "LinkExternalIdentity { redacted }";
}
