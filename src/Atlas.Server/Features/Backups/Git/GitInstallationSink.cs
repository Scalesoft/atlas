using System.Text;
using System.Text.Json;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Platform.Backup;

namespace Atlas.Server.Features.Backups.Git;

/// <summary>Writes the installation snapshot (format v7) into the working tree: only files whose bytes changed are touched, and
/// files the new manifest no longer lists are removed, so a snapshot commit is exactly the changed rows.</summary>
internal sealed class GitInstallationSink : IBackupSink
{
    private readonly GitWorkingTree m_tree;
    private readonly BackupManifest? m_previous;

    public GitInstallationSink(GitWorkingTree tree)
    {
        m_tree = tree;
        try
        {
            m_previous = tree.ReadText(BackupManifest.Path) is { } text ? JsonSerializer.Deserialize<BackupManifest>(text, InstallationExport.ManifestJson) : null;
        }
        catch (JsonException)
        {
            // A damaged manifest is rewritten from a complete export.
            m_previous = null;
        }
    }

    public string? KeyId => m_previous?.KeyId;

    public string? Held(string path)
        => m_previous is not null && m_previous.Files.TryGetValue(path, out var sha256) && m_tree.Exists(path) ? sha256 : null;

    public async Task<string> WriteAsync(string path, Stream content, CancellationToken ct)
    {
        // A blob is named by its content: one the tree holds is that content.
        if (path.StartsWith(AttachmentBlobsPart.Prefix, StringComparison.Ordinal) && m_tree.Exists(path))
        {
            return path[AttachmentBlobsPart.Prefix.Length..];
        }
        return await m_tree.WriteStreamAsync(path, content, ct);
    }

    public async Task CompleteAsync(BackupManifest manifest, CancellationToken ct)
    {
        foreach (var file in m_tree.FilesUnder(".atlas").Where(x => GitBackupLayout.IsInstallationData(x) && x is not (BackupManifest.Path or GitBackupLayout.AttributesPath)
                     && !manifest.Files.ContainsKey(x)).ToList())
        {
            m_tree.Delete(file);
        }
        await m_tree.WriteTextAsync(GitBackupLayout.AttributesPath, GitBackupLayout.Attributes, ct);
        await m_tree.WriteTextAsync(BackupManifest.Path, Encoding.UTF8.GetString(InstallationExport.Serialize(manifest)), ct);
    }
}
