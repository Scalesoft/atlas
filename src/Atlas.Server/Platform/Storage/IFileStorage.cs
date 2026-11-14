namespace Atlas.Server.Platform.Storage;

/// <summary>
/// A flat folder of files on local disk or SMB. Names are single portable filenames. Reads open files with
/// Read|Write|Delete sharing, so an application saving the file concurrently (KeePass) is never blocked.
/// </summary>
public interface IFileStorage : IDisposable
{
    /// <summary>Writes, reads back and deletes a random probe file.</summary>
    Task TestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes a temporary file and renames it to <paramref name="name"/> without replacing an existing file,
    /// so the name never exposes partial content. An existing name fails with <see cref="IOException"/>.
    /// </summary>
    Task PublishNewAsync(string name, Stream content, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit = null);

    /// <summary>Publishes a complete temporary file by replacing the destination in the same folder. Failure preserves the prior file.</summary>
    Task PublishReplaceAsync(string name, Stream content, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit = null);

    /// <summary>
    /// Opens a file for sequential reading. SMB content is buffered in a private scratch file. More than
    /// <paramref name="maximumBytes"/> fails with <see cref="InvalidDataException"/>.
    /// </summary>
    Task<Stream> OpenReadAsync(string name, CancellationToken cancellationToken, long maximumBytes = long.MaxValue);

    /// <summary>
    /// Reads a whole file into memory. The limit counts bytes actually read, not the reported length, and
    /// more than <paramref name="maximumBytes"/> fails with <see cref="InvalidDataException"/>.
    /// </summary>
    Task<byte[]> ReadBoundedAsync(string name, long maximumBytes, CancellationToken cancellationToken);

    /// <summary>Reads metadata without file content. A missing file fails with <see cref="FileNotFoundException"/>.</summary>
    Task<FileStorageMetadata> GetMetadataAsync(string name, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the files of the folder itself (no subfolders) whose name ends with <paramref name="extension"/> (case-insensitive),
    /// ordered by name, at most <paramref name="limit"/>. Names that are not portable filenames are skipped.
    /// </summary>
    Task<FileStorageListing> ListAsync(string extension, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the subfolders of the folder (Length 0), ordered by name, at most <paramref name="limit"/>. Links, hidden and system
    /// folders and names that are not portable are skipped. A missing folder fails with <see cref="DirectoryNotFoundException"/>.
    /// </summary>
    Task<FileStorageListing> ListFoldersAsync(int limit, CancellationToken cancellationToken);

    Task DeleteAsync(string name, CancellationToken cancellationToken);
}
