namespace Atlas.Server.Features.Vaults.Contracts;

/// <summary>An existing KeePass file on a location, opened with its master password.</summary>
public sealed record AttachVaultRequest(Guid SpaceId, Guid LocationId, string Name, string FileName, string Password)
{
    public override string ToString() => "Attach vault request (password omitted)";
}
