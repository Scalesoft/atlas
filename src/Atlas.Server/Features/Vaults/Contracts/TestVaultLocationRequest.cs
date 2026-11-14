namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>Tests a location form before or after saving; with a LocationId an empty password means the stored one.</summary>
public sealed record TestVaultLocationRequest(string Kind, string Root, string? Username = null, string? Domain = null, string? Password = null, Guid? LocationId = null)
{
    public override string ToString() => "Vault location test (credentials omitted)";
}
