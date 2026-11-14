namespace Atlas.Server.Platform.Storage;

internal sealed class LocalFileStorage : IFileStorage
{
    private const int BufferBytes = 65536;
    // Readers never block a concurrent writer: KeePass replaces or rewrites the file while Atlas reads it.
    private const FileShare ReadSharing = FileShare.ReadWrite | FileShare.Delete;
    // Extensions match regardless of case on Linux too (A.KDBX), like on Windows and SMB shares.
    private static readonly EnumerationOptions ListingOptions = new()
    {
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
    };

    private readonly string m_directory;
    private bool m_disposed;

    public LocalFileStorage(string directory)
    {
        m_directory = directory;
    }

    public Task TestAsync(CancellationToken cancellationToken) => FileStorages.TestAsync(this, cancellationToken);

    public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken)
    {
        var path = FilePath(name);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult((File.GetAttributes(path) & FileAttributes.Directory) == 0);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(false);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<FileStorageListing> ListAsync(string extension, int limit, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var folder = new DirectoryInfo(m_directory);
        if (!folder.Exists)
        {
            throw new DirectoryNotFoundException("The storage folder does not exist.");
        }
        var entries = folder.EnumerateFiles("*" + extension, ListingOptions)
            .Select(file => new FileStorageEntry(file.Name, file.Length, file.LastWriteTimeUtc));
        return Task.FromResult(FileStorages.Listing(entries, extension, limit));
    }

    public Task<FileStorageListing> ListFoldersAsync(int limit, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var folder = new DirectoryInfo(m_directory);
        if (!folder.Exists)
        {
            throw new DirectoryNotFoundException("The storage folder does not exist.");
        }
        var entries = folder.EnumerateDirectories("*", ListingOptions)
            .Where(item => (item.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Select(item => new FileStorageEntry(item.Name, 0, item.LastWriteTimeUtc));
        return Task.FromResult(FileStorages.Listing(entries, "", limit));
    }

    public Task PublishNewAsync(string name, Stream content, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit = null) => PublishAsync(name, content, false, cancellationToken, beforeCommit);

    public Task PublishReplaceAsync(string name, Stream content, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit = null) => PublishAsync(name, content, true, cancellationToken, beforeCommit);

    private async Task PublishAsync(string name, Stream content, bool replace, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeCommit)
    {
        var target = FilePath(name);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(m_directory);
        var temporary = Path.Combine(m_directory, $".atlas-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(output, BufferBytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (beforeCommit is not null) await beforeCommit(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, replace);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public Task<Stream> OpenReadAsync(string name, CancellationToken cancellationToken, long maximumBytes = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var path = FilePath(name);
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = OpenShared(path);
        try
        {
            if (stream.Length > maximumBytes)
            {
                throw FileStorages.TooLarge();
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        return Task.FromResult(stream);
    }

    public async Task<byte[]> ReadBoundedAsync(string name, long maximumBytes, CancellationToken cancellationToken)
    {
        FileStorages.ValidateReadLimit(maximumBytes);
        var path = FilePath(name);
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = OpenShared(path);
        var length = stream.Length;
        if (length > maximumBytes)
        {
            throw FileStorages.TooLarge();
        }
        // The reported length only sizes the buffer; a file growing during the read is still counted.
        return await FileStorages.ReadBoundedAsync(stream, maximumBytes, length, cancellationToken);
    }

    public Task<FileStorageMetadata> GetMetadataAsync(string name, CancellationToken cancellationToken)
    {
        var path = FilePath(name);
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("File does not exist.");
        }
        return Task.FromResult(new FileStorageMetadata(info.Length, info.LastWriteTimeUtc, null, null));
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        var path = FilePath(name);
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        return Task.CompletedTask;
    }

    public void Dispose() => m_disposed = true;

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, ReadSharing, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private string FilePath(string name)
    {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        FileStorages.ValidateName(name);
        return Path.Combine(m_directory, name);
    }
}
