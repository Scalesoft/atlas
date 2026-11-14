using System.Data;
using System.IO.Compression;
using System.Text;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Server.Platform.Backup;
using BackupManifest = Atlas.Server.Platform.Backup.BackupManifest;

namespace Atlas.Server.Features.Backups.Installation;

/// <summary>The "complete" ZIP backup: the format v7 tree of the whole installation in one archive, the same files the Git
/// backup holds under <c>.atlas</c>. Entries carry a fixed time, so the same data gives the same archive.</summary>
public sealed class InstallationArchive(AtlasDatabase database, InstallationExport export, BackupKeys keys)
{
    private static readonly DateTimeOffset EntryTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<string> GetContentTokenAsync(CancellationToken ct)
    {
        using var session = database.OpenSession();
        session.DefaultReadOnly = true;
        using var transaction = session.BeginTransaction(IsolationLevel.RepeatableRead);
        var token = await export.ContentTokenAsync(session, await keys.CurrentAsync(session, ct), ct);
        await transaction.CommitAsync(ct);
        return token;
    }

    public async Task<BackupArchiveFile> CreateAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "atlas-complete-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var session = database.OpenSession();
            session.DefaultReadOnly = true;
            using var transaction = session.BeginTransaction(IsolationLevel.RepeatableRead);
            var key = await keys.CurrentAsync(session, ct);
            var token = await export.ContentTokenAsync(session, key, ct);
            await using (var output = BackupFormat.CreatePrivateFile(path))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
            {
                await export.WriteAsync(session, new ZipSink(zip), key, null, ct);
            }
            await transaction.CommitAsync(ct);
            return new BackupArchiveFile(path, token);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private sealed class ZipSink(ZipArchive zip) : IBackupSink
    {
        public string? KeyId => null;

        public string? Held(string path) => null;

        public async Task<string> WriteAsync(string path, Stream content, CancellationToken ct)
        {
            BackupFormat.RequirePath(path);
            var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
            entry.LastWriteTime = EntryTime;
            await using var output = entry.Open();
            return (await BackupFormat.CopyAsync(content, output, path, long.MaxValue, ct)).Sha256;
        }

        public async Task CompleteAsync(BackupManifest manifest, CancellationToken ct)
        {
            await WriteAsync("README.md", new MemoryStream(Encoding.UTF8.GetBytes(InstallationExport.Readme)), ct);
            await WriteAsync(BackupManifest.Path, new MemoryStream(InstallationExport.Serialize(manifest)), ct);
        }
    }
}
