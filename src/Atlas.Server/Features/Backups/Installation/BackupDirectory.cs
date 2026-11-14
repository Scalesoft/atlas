using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Server.Platform.Backup;
using BackupManifest = Atlas.Server.Platform.Backup.BackupManifest;

namespace Atlas.Server.Features.Backups.Installation;

/// <summary>
/// A format v7 backup on disk, checked before anything reads it: a Git clone of the backup repository, or a ZIP (the complete
/// archive, or the repository downloaded from the Git server, which puts it in one top folder) extracted to a private folder.
/// Every file the manifest lists must exist with its SHA-256, and no other file may sit in the restorable part.
/// </summary>
public sealed class BackupDirectory : IBackupReader, IDisposable
{
    private const int MaximumEntries = 2_000_000;
    private static readonly string[] RestorableFolders = [".atlas/modules/", ".atlas/blobs/", ".atlas/secure/"];
    private readonly string m_root;
    private readonly string? m_owned;
    private readonly List<string> m_paths;

    private BackupDirectory(string root, string? owned, BackupManifest manifest, BackupKey? key)
    {
        m_root = root;
        m_owned = owned;
        Manifest = manifest;
        Key = key;
        m_paths = manifest.Files.Keys.Order(StringComparer.Ordinal).ToList();
    }

    public BackupManifest Manifest { get; }
    public BackupKey? Key { get; }

    /// <summary>Checks the tree at <paramref name="root"/>; <paramref name="key"/> must be the backup's key when given.</summary>
    public static async Task<BackupDirectory> OpenAsync(string root, string? key, CancellationToken ct, string? owned = null)
    {
        var manifestFile = Path.Combine(root, ".atlas", "manifest.json");
        if (!File.Exists(manifestFile))
        {
            throw new InvalidDataException(Describe(root));
        }
        BackupManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllBytesAsync(manifestFile, ct), InstallationExport.ManifestJson)
                ?? throw new InvalidDataException("Prázdný manifest zálohy.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Manifest zálohy není platný JSON.", ex);
        }
        if (manifest.Format != BackupManifest.FormatName || manifest.Version != BackupManifest.CurrentVersion)
        {
            throw new InvalidDataException($"Nepodporovaný formát zálohy {manifest.Format} {manifest.Version}; tato verze Atlasu obnovuje formát 7.");
        }
        foreach (var (path, sha256) in manifest.Files)
        {
            BackupFormat.RequirePath(path);
            var file = Full(root, path);
            if (!RestorableFolders.Any(x => path.StartsWith(x, StringComparison.Ordinal)) || !File.Exists(file))
            {
                throw new InvalidDataException("V záloze chybí soubor: " + path);
            }
            await using var stream = File.OpenRead(file);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)) != sha256)
            {
                throw new InvalidDataException($"Kontrolní součet souboru {path} nesouhlasí. Repozitář klonujte bez převodu konců řádků (core.autocrlf=false).");
            }
        }
        foreach (var folder in RestorableFolders.Select(x => Full(root, x.TrimEnd('/'))).Where(Directory.Exists))
        {
            var unlisted = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(x => Path.GetRelativePath(root, x).Replace(Path.DirectorySeparatorChar, '/')).FirstOrDefault(x => !manifest.Files.ContainsKey(x));
            if (unlisted is not null)
            {
                throw new InvalidDataException("Záloha obsahuje soubor, který manifest neuvádí: " + unlisted);
            }
        }
        var parsed = string.IsNullOrWhiteSpace(key) ? null : BackupKey.Parse(key);
        if (parsed is not null && manifest.KeyId is not null && parsed.Id != manifest.KeyId)
        {
            throw new ApiException(400, "Zadaný klíč nepatří k této záloze (záloha je šifrovaná klíčem {keyId}, zadaný klíč má {id}).", args: new Dictionary<string, object?> { ["keyId"] = manifest.KeyId, ["id"] = parsed.Id });
        }
        return new BackupDirectory(root, owned, manifest, manifest.KeyId is null ? null : parsed);
    }

    /// <summary>Extracts a ZIP into a private folder (deleted with the reader) with the limits of the archive import.</summary>
    public static async Task<BackupDirectory> ExtractAsync(Stream zip, string? key, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "atlas-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var archive = Path.Combine(folder, "archive.zip");
            await using (var spool = BackupFormat.CreatePrivateFile(archive))
            {
                await BackupFormat.CopyAsync(zip, spool, "archive.zip", BackupFormat.MaximumZipBytes, ct);
                spool.Position = 0;
                using var entries = new ZipArchive(spool, ZipArchiveMode.Read, true);
                await ExtractEntriesAsync(entries, Path.Combine(folder, "tree"), ct);
            }
            File.Delete(archive);
            var root = Path.Combine(folder, "tree");
            // A repository downloaded from the Git server sits in one top folder.
            if (!File.Exists(Path.Combine(root, ".atlas", "manifest.json")) && Directory.GetDirectories(root) is [var top] && Directory.GetFiles(root).Length == 0)
            {
                root = top;
            }
            return await OpenAsync(root, key, ct, folder);
        }
        catch
        {
            Delete(folder);
            throw;
        }
    }

    private static async Task ExtractEntriesAsync(ZipArchive zip, string root, CancellationToken ct)
    {
        if (zip.Entries.Count > MaximumEntries)
        {
            throw new InvalidDataException("ZIP obsahuje příliš mnoho položek.");
        }
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }
            BackupFormat.RequirePath(entry.FullName);
            if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000 || expanded + entry.Length > BackupFormat.MaximumExpandedBytes)
            {
                throw new InvalidDataException("ZIP obsahuje symbolický odkaz nebo překračuje povolenou velikost.");
            }
            var target = Full(root, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var output = BackupFormat.CreatePrivateFile(target);
            await using var input = entry.Open();
            expanded += (await BackupFormat.CopyAsync(input, output, entry.FullName, BackupFormat.MaximumExpandedBytes - expanded, ct)).Size;
        }
    }

    public IEnumerable<string> Files(string prefix)
    {
        var index = m_paths.BinarySearch(prefix, StringComparer.Ordinal);
        for (var i = index < 0 ? ~index : index; i < m_paths.Count && m_paths[i].StartsWith(prefix, StringComparison.Ordinal); i++)
        {
            yield return m_paths[i];
        }
    }

    public Stream Open(string path)
        => Manifest.Files.ContainsKey(path) ? File.OpenRead(Full(m_root, path)) : throw new InvalidDataException("V záloze chybí soubor: " + path);

    public byte[] ReadSecure(string path)
    {
        var key = Key ?? throw new InvalidOperationException("The secure part needs the backup key.");
        using var stream = Open(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return key.Decrypt(path, buffer.ToArray());
    }

    public void Dispose()
    {
        if (m_owned is not null)
        {
            Delete(m_owned);
        }
    }

    /// <summary>Deletes a folder this reader owned; git marks pack files read-only, which Windows refuses to delete.</summary>
    public static void Delete(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(folder, true);
    }

    private static string Full(string root, string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>What a tree without a v7 manifest is, and where it is restored instead.</summary>
    private static string Describe(string root)
    {
        if (File.Exists(Path.Combine(root, "manifest.json")) && File.Exists(Path.Combine(root, "wiki.json")))
        {
            return "Toto je archiv obsahu z Atlasu 2.x (formát 1–6). Obnovte ho importem archivu (obnovit podle ID nebo přidat vedle).";
        }
        if (File.Exists(Path.Combine(root, ".atlas", "repository.json")))
        {
            return "Toto je repozitář zálohy do Gitu z Atlasu 2.x bez dat instalace. Stránky z něj načte import z Wiki.js; "
                + "úplnou zálohu do něj zapíše první synchronizace Atlasu 3.";
        }
        return "Záloha neobsahuje .atlas/manifest.json formátu 7.";
    }
}
