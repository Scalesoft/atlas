using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Atlas.Server.Features.Backups.Git;

/// <summary>
/// File operations on the local clone plus one commit per call of <see cref="CommitAsync"/>. Only the paths touched since the last
/// commit are staged, so a large tree is not rescanned for every revision.
/// </summary>
public sealed class GitWorkingTree
{
    private const int MaximumPathspecs = 200;
    private readonly GitCommand m_git;
    private readonly HashSet<string> m_touched = new(StringComparer.Ordinal);

    public GitWorkingTree(GitCommand git)
    {
        m_git = git;
    }

    public string Root => m_git.Directory;

    private string Full(string path)
    {
        BackupPathRules.Require(path);
        return Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
    }

    public bool Exists(string path) => File.Exists(Full(path)) || Directory.Exists(Full(path));

    public string? ReadText(string path) => File.Exists(Full(path)) ? File.ReadAllText(Full(path), Encoding.UTF8) : null;

    public async Task WriteTextAsync(string path, string content, CancellationToken ct)
    {
        var full = Full(path);
        if (File.Exists(full) && await File.ReadAllTextAsync(full, Encoding.UTF8, ct) == content)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, new UTF8Encoding(false), ct);
        m_touched.Add(path);
    }

    /// <summary>Copies an immutable attachment unless the file already has the same size and SHA-256. <paramref name="recorded"/>:
    /// the page's native document already lists this attachment under this name, so an equal size suffices.</summary>
    public async Task WriteFileAsync(string path, Func<Stream> open, long size, string sha256, bool recorded, CancellationToken ct)
    {
        var full = Full(path);
        if (File.Exists(full) && new FileInfo(full).Length == size)
        {
            if (recorded)
                return;
            await using var existing = File.OpenRead(full);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, ct)) == sha256)
                return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var source = open())
        await using (var target = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            await source.CopyToAsync(target, ct);
        m_touched.Add(path);
    }

    /// <summary>Writes a file of any size through a temporary copy unless the tree already holds the same bytes; returns its SHA-256.</summary>
    public async Task<string> WriteStreamAsync(string path, Stream content, CancellationToken ct)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".atlas-new";
        string sha256;
        await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            sha256 = (await Archive.BackupFormat.CopyAsync(content, target, path, long.MaxValue, ct)).Sha256;
        if (File.Exists(full) && new FileInfo(full).Length == new FileInfo(temporary).Length)
        {
            await using var existing = File.OpenRead(full);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, ct)) == sha256)
            {
                File.Delete(temporary);
                return sha256;
            }
        }
        File.Move(temporary, full, true);
        m_touched.Add(path);
        return sha256;
    }

    /// <summary>The files below a folder of the tree, as repository paths.</summary>
    public IEnumerable<string> FilesUnder(string folder)
    {
        var root = Path.GetFullPath(Root);
        var full = Full(folder);
        return Directory.Exists(full)
            ? Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(root, x).Replace(Path.DirectorySeparatorChar, '/'))
            : [];
    }

    /// <summary>Removes a file or a whole folder.</summary>
    public void Delete(string path)
    {
        var full = Full(path);
        if (File.Exists(full))
            File.Delete(full);
        else if (Directory.Exists(full))
            Directory.Delete(full, true);
        else
            return;
        m_touched.Add(path);
    }

    /// <summary>Keeps exactly <paramref name="names"/> in a folder (the page's attachments) and removes everything else.</summary>
    public void Retain(string folder, IReadOnlySet<string> names)
    {
        var full = Full(folder);
        if (!Directory.Exists(full))
            return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var name = Path.GetFileName(entry);
            if (!names.Contains(name))
                Delete(folder + "/" + name);
        }
        if (!Directory.EnumerateFileSystemEntries(full).Any())
            Delete(folder);
    }

    /// <summary>Moves a folder (a page's children) when the target does not exist yet; otherwise leaves both for the rewrite.</summary>
    public bool MoveFolder(string from, string to)
    {
        var source = Full(from);
        var target = Full(to);
        if (!Directory.Exists(source) || Directory.Exists(target) || File.Exists(target))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
        m_touched.Add(from);
        m_touched.Add(to);
        return true;
    }

    /// <summary>All files of the tree outside .git, as repository paths.</summary>
    public IEnumerable<string> Files()
    {
        var root = Path.GetFullPath(Root);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(root, x).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(x => x != ".git" && !x.StartsWith(".git/", StringComparison.Ordinal));
    }

    /// <summary>Stages the touched paths and commits them; false when they did not change anything.</summary>
    public async Task<bool> CommitAsync(string subject, GitIdentity author, DateTime authored, IReadOnlyList<string> trailers, CancellationToken ct)
    {
        if (m_touched.Count == 0)
            return false;
        // --force: a .gitignore anywhere in the tree must not hide a page's files.
        if (m_touched.Count > MaximumPathspecs)
            await m_git.GitAsync(ct, "add", "--all", "--force", "--", ".");
        else
        {
            // A path that is gone may never have been committed (written and removed within one step): unstage what exists
            // in the index, add the rest.
            var present = m_touched.Where(Exists).ToList();
            var missing = m_touched.Where(x => !Exists(x)).ToList();
            if (missing.Count > 0)
                await m_git.RunGitAsync(["rm", "-r", "--cached", "--quiet", "--ignore-unmatch", "--", .. missing], null, null, true, ct);
            if (present.Count > 0)
                await m_git.RunGitAsync(["add", "--all", "--force", "--", .. present], null, null, true, ct);
        }
        m_touched.Clear();
        var staged = await m_git.RunGitAsync(["diff", "--cached", "--quiet"], null, null, false, ct);
        if (staged.ExitCode == 0)
            return false;
        if (staged.ExitCode != 1)
            throw new GitException("git diff selhal: " + staged.Error.Trim());
        var message = new StringBuilder(subject.Length > 240 ? subject[..240] : subject).Append("\n\n");
        foreach (var trailer in trailers)
            message.Append(trailer).Append('\n');
        var date = new DateTimeOffset(DateTime.SpecifyKind(authored, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " +0000";
        await m_git.RunAsync("git", ["commit", "--quiet", "--no-verify", "--cleanup=verbatim", "--file=-"], message.ToString(), null, true, ct,
            new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = author.Name,
                ["GIT_AUTHOR_EMAIL"] = author.Email,
                ["GIT_AUTHOR_DATE"] = date,
                ["GIT_COMMITTER_NAME"] = GitIdentity.Committer.Name,
                ["GIT_COMMITTER_EMAIL"] = GitIdentity.Committer.Email
            });
        return true;
    }

    /// <summary>Forgets uncommitted edits, e.g. the partial writes of a run that failed or was interrupted.</summary>
    public async Task ResetAsync(bool hasCommits, CancellationToken ct)
    {
        m_touched.Clear();
        if (hasCommits)
            await m_git.GitAsync(ct, "reset", "--quiet", "--hard", "HEAD");
        else
            await m_git.RunGitAsync(["rm", "-r", "--cached", "--quiet", "--ignore-unmatch", "."], null, null, true, ct);
        await m_git.GitAsync(ct, "clean", "--quiet", "-d", "--force", "-x");
    }
}

/// <summary>Repository paths the sync writes: relative, forward slashes, no traversal.</summary>
internal static class BackupPathRules
{
    internal static void Require(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':')
            || path.Split('/').Any(x => x is "" or "." or ".." || x.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new GitException("Neplatná cesta v repozitáři zálohy: " + path);
    }
}
