namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>The new vault; GeneratedPassword is returned only here and only when the server generated it.</summary>
public sealed record CreatedVault(VaultView Vault, string? GeneratedPassword)
{
    public override string ToString() => "Created vault (password omitted)";
}
