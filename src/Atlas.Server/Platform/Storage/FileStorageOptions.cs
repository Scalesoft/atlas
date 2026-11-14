namespace Atlas.Server.Platform.Storage;

/// <summary>
/// Location of a file storage: an absolute local directory or <c>smb://host/share/folder</c> with separate
/// credentials. Shared by backups and vaults.
/// </summary>
public sealed record FileStorageOptions(string Location, string Username, string Domain, string? Password)
{
    public override string ToString() => "File storage options (credentials omitted)";
}
