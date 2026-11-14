using System.Globalization;
using SMBLibrary;
using SmbAttributes = SMBLibrary.FileAttributes;

namespace Atlas.Server.Platform.Storage;

internal sealed class SmbFileStorage : IFileStorage
{
    private const int ChunkBytes = 65536;
    // Readers never block a concurrent writer: KeePass replaces or rewrites the file while Atlas reads it.
    private const ShareAccess ReadSharing = ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete;

    private readonly FileStorageOptions m_options;
    private readonly string m_host;
    private readonly string m_share;
    private readonly string m_folder;
    private bool m_disposed;

    public SmbFileStorage(FileStorageOptions options)
    {
        m_options = options;
        var value = options.Location;
        if (!value.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Port is not (-1 or 445))
        {
            throw FileStorages.InvalidLocation();
        }

        // Validate the original segments: Uri normalizes dot segments before exposing AbsolutePath.
        var pathStart = value.IndexOf('/', "smb://".Length);
        if (pathStart < 0)
        {
            throw FileStorages.InvalidLocation();
        }
        var parts = value[(pathStart + 1)..].TrimEnd('/').Split('/');
        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = Uri.UnescapeDataString(parts[i]);
                FileStorages.ValidateName(parts[i]);
            }
        }
        catch (ArgumentException)
        {
            throw FileStorages.InvalidLocation();
        }
        m_host = uri.IdnHost;
        m_share = parts[0];
        m_folder = string.Join('\\', parts.Skip(1));
    }

    public Task TestAsync(CancellationToken cancellationToken) => FileStorages.TestAsync(this, cancellationToken);

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken)
    {
        Validate(name);
        using var connection = await ConnectAsync(cancellationToken);
        var handle = await OpenAttributesAsync(connection, name, cancellationToken);
        if (handle is null)
        {
            return false;
        }
        await connection.CloseAsync(handle);
        return true;
    }

    public async Task<FileStorageMetadata> GetMetadataAsync(string name, CancellationToken cancellationToken)
    {
        Validate(name);
        using var connection = await ConnectAsync(cancellationToken);
        var handle = await OpenAttributesAsync(connection, name, cancellationToken)
            ?? throw new FileNotFoundException("File does not exist on the SMB share.");
        try
        {
            var open = await connection.CallAsync(() =>
            {
                SmbConnection.Check(connection.Store.GetFileInformation(out var information, handle, FileInformationClass.FileNetworkOpenInformation), "read file metadata");
                return (FileNetworkOpenInformation)information;
            }, cancellationToken);
            // Not every server reports the file index; change detection then relies on times and length.
            var index = await connection.CallAsync(() =>
                connection.Store.GetFileInformation(out var information, handle, FileInformationClass.FileInternalInformation) == NTStatus.STATUS_SUCCESS
                && information is FileInternalInformation { IndexNumber: not 0 } internalInformation
                    ? internalInformation.IndexNumber.ToString(CultureInfo.InvariantCulture)
                    : null, cancellationToken);
            if (open.LastWriteTime is not { } lastWrite)
            {
                throw new IOException("SMB server did not report the file modification time.");
            }
            return new FileStorageMetadata(open.EndOfFile, Utc(lastWrite), open.ChangeTime is { } change ? Utc(change) : null, index);
        }
        finally
        {
            await connection.CloseAsync(handle);
        }
    }

    public Task<FileStorageListing> ListAsync(string extension, int limit, CancellationToken cancellationToken) =>
        ListAsync(false, extension, limit, cancellationToken);

    public Task<FileStorageListing> ListFoldersAsync(int limit, CancellationToken cancellationToken) =>
        ListAsync(true, "", limit, cancellationToken);

    private async Task<FileStorageListing> ListAsync(bool folders, string extension, int limit, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        using var connection = await ConnectAsync(cancellationToken);
        var folder = await connection.CallAsync(() =>
        {
            var status = connection.Store.CreateFile(out var handle, out _, m_folder, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
                SmbAttributes.Directory, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (SmbConnection.IsMissing(status))
            {
                throw new DirectoryNotFoundException("The storage folder does not exist.");
            }
            SmbConnection.Check(status, "open folder");
            return handle;
        }, cancellationToken);
        try
        {
            var entries = await connection.CallAsync(() =>
            {
                var status = connection.Store.QueryDirectory(out var found, folder, "*" + extension, FileInformationClass.FileDirectoryInformation);
                if (status != NTStatus.STATUS_NO_MORE_FILES && status != NTStatus.STATUS_NO_SUCH_FILE)
                {
                    SmbConnection.Check(status, "list folder");
                }
                return (found ?? []).OfType<FileDirectoryInformation>()
                    .Where(item => folders
                        ? (item.FileAttributes & (SmbAttributes.Directory | SmbAttributes.ReparsePoint | SmbAttributes.Hidden | SmbAttributes.System)) == SmbAttributes.Directory
                        : (item.FileAttributes & SmbAttributes.Directory) == 0)
                    .Select(item => new FileStorageEntry(item.FileName, item.EndOfFile, Utc(item.LastWriteTime)))
                    .ToList();
            }, cancellationToken);
            return FileStorages.Listing(entries, extension, limit);
        }
        finally
        {
            await connection.CloseAsync(folder);
        }
    }

    public Task PublishNewAsync(string name, Stream content, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit = null) => PublishAsync(name, content, false, cancellationToken, beforeCommit);

    public Task PublishReplaceAsync(string name, Stream content, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit = null) => PublishAsync(name, content, true, cancellationToken, beforeCommit);

    private async Task PublishAsync(string name, Stream content, bool replace, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit)
    {
        Validate(name);
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = $".atlas-{Guid.NewGuid():N}.partial";
        using var connection = await ConnectAsync(cancellationToken);
        var handle = await connection.OpenAsync(RemotePath(temporary), AccessMask.GENERIC_WRITE | AccessMask.DELETE, ShareAccess.Read,
            CreateDisposition.FILE_CREATE, cancellationToken);
        var published = false;
        try
        {
            try
            {
                var buffer = new byte[(int)Math.Min(ChunkBytes, connection.Store.MaxWriteSize)];
                if (buffer.Length == 0)
                {
                    throw new IOException("SMB server advertised an invalid write size.");
                }
                long offset = 0;
                int count;
                while ((count = await content.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    var data = buffer.AsSpan(0, count).ToArray();
                    var written = await connection.CallAsync(() =>
                    {
                        SmbConnection.Check(connection.Store.WriteFile(out var bytes, handle, offset, data), "write file data");
                        return bytes;
                    }, cancellationToken);
                    if (written != count)
                    {
                        throw new IOException("SMB server could not write the complete block. Check free space and server health.");
                    }
                    offset += written;
                }
                await connection.CallAsync(() => SmbConnection.Check(connection.Store.FlushFileBuffers(handle), "flush file data"), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (beforeCommit is not null) await beforeCommit(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await connection.CallAsync(() => SmbConnection.Check(connection.Store.SetFileInformation(handle,
                    new FileRenameInformationType2 { FileName = RemotePath(name), ReplaceIfExists = replace }), "publish file; ensure the filename is unused and rename is permitted"), cancellationToken);
                published = true;
            }
            finally
            {
                await connection.CloseAsync(handle);
            }
        }
        finally
        {
            if (!published)
            {
                await DeleteAsync(temporary, CancellationToken.None);
            }
        }
    }

    public async Task<Stream> OpenReadAsync(string name, CancellationToken cancellationToken, long maximumBytes = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        Validate(name);
        using var connection = await ConnectAsync(cancellationToken);
        // The returned stream owns this scratch file; memory stays bounded even for large archives.
        var output = new FileStream(Path.Combine(Path.GetTempPath(), $"atlas-read-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, ChunkBytes, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await ReadAllAsync(connection, name, maximumBytes, output, cancellationToken);
            output.Position = 0;
            return output;
        }
        catch
        {
            await output.DisposeAsync();
            throw;
        }
    }

    public async Task<byte[]> ReadBoundedAsync(string name, long maximumBytes, CancellationToken cancellationToken)
    {
        FileStorages.ValidateReadLimit(maximumBytes);
        Validate(name);
        using var connection = await ConnectAsync(cancellationToken);
        using var output = new MemoryStream();
        await ReadAllAsync(connection, name, maximumBytes, output, cancellationToken);
        return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        Validate(name);
        using var connection = await ConnectAsync(cancellationToken);
        var opened = await connection.CallAsync(() =>
        {
            var status = connection.Store.CreateFile(out var handle, out _, RemotePath(name), AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                SmbAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (SmbConnection.IsMissing(status))
            {
                return null;
            }
            SmbConnection.Check(status, "open file for deletion");
            return handle;
        }, cancellationToken);
        if (opened is null)
        {
            return;
        }
        try
        {
            await connection.CallAsync(() => SmbConnection.Check(connection.Store.SetFileInformation(opened,
                new FileDispositionInformation { DeletePending = true }), "delete file; check delete permission"), cancellationToken);
        }
        finally
        {
            await connection.CloseAsync(opened);
        }
    }

    public void Dispose() => m_disposed = true;

    /// <summary>Copies the remote file into <paramref name="output"/>, counting bytes actually received.</summary>
    private async Task ReadAllAsync(SmbConnection connection, string name, long maximumBytes, Stream output, CancellationToken cancellationToken)
    {
        var handle = await connection.OpenAsync(RemotePath(name), AccessMask.GENERIC_READ, ReadSharing, CreateDisposition.FILE_OPEN, cancellationToken);
        try
        {
            long offset = 0;
            var size = (int)Math.Min(ChunkBytes, connection.Store.MaxReadSize);
            if (size == 0)
            {
                throw new IOException("SMB server advertised an invalid read size.");
            }
            while (true)
            {
                var data = await connection.CallAsync(() =>
                {
                    var status = connection.Store.ReadFile(out var bytes, handle, offset, size);
                    if (status == NTStatus.STATUS_END_OF_FILE)
                    {
                        return [];
                    }
                    SmbConnection.Check(status, "read file data");
                    return bytes;
                }, cancellationToken);
                if (data.Length == 0)
                {
                    break;
                }
                if (data.Length > maximumBytes - offset)
                {
                    throw FileStorages.TooLarge();
                }
                await output.WriteAsync(data, cancellationToken);
                offset += data.Length;
            }
        }
        finally
        {
            await connection.CloseAsync(handle);
        }
    }

    /// <summary>Opens for metadata only (no content access); returns null when the file does not exist.</summary>
    private Task<object?> OpenAttributesAsync(SmbConnection connection, string name, CancellationToken cancellationToken) =>
        connection.CallAsync(() =>
        {
            var status = connection.Store.CreateFile(out var opened, out _, RemotePath(name), (AccessMask)FileAccessMask.FILE_READ_ATTRIBUTES | AccessMask.SYNCHRONIZE,
                SmbAttributes.Normal, ReadSharing, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT, null);
            if (SmbConnection.IsMissing(status) || status == NTStatus.STATUS_FILE_IS_A_DIRECTORY)
            {
                return null;
            }
            SmbConnection.Check(status, "check file metadata");
            return opened;
        }, cancellationToken);

    private async Task<SmbConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        var connection = new SmbConnection();
        try
        {
            await connection.ConnectAsync(m_host, m_share, m_options, cancellationToken);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Validate(string name)
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        FileStorages.ValidateName(name);
    }

    private string RemotePath(string name) => m_folder.Length == 0 ? name : $"{m_folder}\\{name}";

    private static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
