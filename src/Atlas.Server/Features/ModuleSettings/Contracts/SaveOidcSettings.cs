namespace Atlas.Server.Features.ModuleSettings.Contracts;

public sealed record SaveOidcSettings(Guid? ExpectedVersion, string PublicOrigin, SaveOidcProvider[] Providers)
{
    public override string ToString() => "SaveOidcSettings { sensitive values redacted }";
}
