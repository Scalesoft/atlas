namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>An empty password keeps the stored one; a local folder has no credentials.</summary>
public sealed record SaveVaultLocationRequest(string Name, string Kind, string Root, string? Username = null, string? Domain = null, string? Password = null)
{
    public override string ToString() => "Vault location request (credentials omitted)";
}
