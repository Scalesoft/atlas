namespace Atlas.Server.Features.Vaults.Contracts;

public sealed record VaultPasswordRequest(string Password)
{
    public override string ToString() => "Vault password request (password omitted)";
}
