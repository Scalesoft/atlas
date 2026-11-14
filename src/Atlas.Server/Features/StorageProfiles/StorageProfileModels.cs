namespace Atlas.Server.Features.StorageProfiles;

public class StorageProfile
{
    public virtual Guid Id { get; set; } = Guid.NewGuid();
    public virtual string Name { get; set; } = "";
    public virtual string Destination { get; set; } = "";
    public virtual string Username { get; set; } = "";
    public virtual string Domain { get; set; } = "";
    public virtual string? ProtectedPassword { get; set; }
    public virtual bool Enabled { get; set; } = true;
    public virtual bool AllowPageExports { get; set; } = true;
    public virtual bool AllowBackups { get; set; } = true;
    public virtual bool AllSpaces { get; set; }
    public virtual int Version { get; set; } = 1;
}

public class StorageProfileSpace
{
    public virtual Guid StorageProfileId { get; set; }
    public virtual Guid SpaceId { get; set; }
    public override bool Equals(object? obj) => obj is StorageProfileSpace other && StorageProfileId == other.StorageProfileId && SpaceId == other.SpaceId;
    public override int GetHashCode() => HashCode.Combine(StorageProfileId, SpaceId);
}

public sealed record StorageProfileRequest(string Name, string Destination, string Username, string Domain, string? Password,
    bool Enabled, bool AllowPageExports, bool AllowBackups, bool AllSpaces, Guid[] SpaceIds, int? ExpectedVersion = null)
{
    public override string ToString() => "Storage profile request (credentials omitted)";
}
public sealed record StorageProfileView(Guid Id, string Name, string Destination, string Username, string Domain, bool HasPassword,
    bool Enabled, bool AllowPageExports, bool AllowBackups, bool AllSpaces, Guid[] SpaceIds, int Version);
/// <param name="Location">The root as people see it (<c>\\server\share\folder</c> or a server path), for the full path of an export.</param>
public sealed record AvailableStorageProfile(Guid Id, string Name, string Location);
