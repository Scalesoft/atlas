namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>A new empty KeePass database. With GeneratePassword the server chooses the master password and returns it once.</summary>
public sealed record CreateVaultRequest(Guid SpaceId, Guid LocationId, string Name, string FileName, bool GeneratePassword, string? Password = null)
{
    public override string ToString() => "Create vault request (password omitted)";
}
