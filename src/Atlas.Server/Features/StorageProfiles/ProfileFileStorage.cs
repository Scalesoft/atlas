using Atlas.Server.Platform.Storage;

namespace Atlas.Server.Features.StorageProfiles;

// The open instance keeps one destination/version. A changed profile cannot retarget an in-flight operation.
internal sealed class ProfileFileStorage(IFileStorage storage, string root, string? directory, Func<CancellationToken, Task> validateMutation) : IFileStorage
{
    private void Check(string? name = null)
    {
        if (directory is null) return;
        StorageProfilePaths.RequireLocalContainment(root, directory);
        if (name is null) return;
        FileStorages.ValidateName(name);
        var target = new FileInfo(Path.Combine(directory, name));
        if (target.LinkTarget is not null || target.Exists && (target.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ApiException(400, "Soubor úložiště nesmí být symbolický odkaz.");
    }
    public Task TestAsync(CancellationToken ct) { Check(); return storage.TestAsync(ct); }
    public async Task PublishNewAsync(string name, Stream content, CancellationToken ct, Func<CancellationToken, Task>? beforeCommit = null)
    {
        await validateMutation(ct);
        Check(name);
        await storage.PublishNewAsync(name, content, ct, token => CommitAsync(name, token, beforeCommit));
    }
    public async Task PublishReplaceAsync(string name, Stream content, CancellationToken ct, Func<CancellationToken, Task>? beforeCommit = null)
    {
        await validateMutation(ct);
        Check(name);
        await storage.PublishReplaceAsync(name, content, ct, token => CommitAsync(name, token, beforeCommit));
    }
    private async Task CommitAsync(string name, CancellationToken ct, Func<CancellationToken, Task>? beforeCommit)
    {
        if (beforeCommit is not null) await beforeCommit(ct);
        await validateMutation(ct);
        Check(name);
    }
    public Task<Stream> OpenReadAsync(string name, CancellationToken ct, long maximumBytes = long.MaxValue) { Check(name); return storage.OpenReadAsync(name, ct, maximumBytes); }
    public Task<byte[]> ReadBoundedAsync(string name, long maximumBytes, CancellationToken ct) { Check(name); return storage.ReadBoundedAsync(name, maximumBytes, ct); }
    public Task<FileStorageMetadata> GetMetadataAsync(string name, CancellationToken ct) { Check(name); return storage.GetMetadataAsync(name, ct); }
    public Task<bool> ExistsAsync(string name, CancellationToken ct) { Check(name); return storage.ExistsAsync(name, ct); }
    public Task<FileStorageListing> ListAsync(string extension, int limit, CancellationToken ct) { Check(); return storage.ListAsync(extension, limit, ct); }
    public Task<FileStorageListing> ListFoldersAsync(int limit, CancellationToken ct) { Check(); return storage.ListFoldersAsync(limit, ct); }
    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        await validateMutation(ct);
        Check(name);
        await storage.DeleteAsync(name, ct);
    }
    public void Dispose() => storage.Dispose();
}
