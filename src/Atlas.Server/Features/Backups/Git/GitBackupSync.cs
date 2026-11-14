using System.Data;
using System.Text.Json.Nodes;
using NHibernate.Linq;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.SpaceSettings.Domain;

namespace Atlas.Server.Features.Backups.Git;

/// <summary>
/// Turns the difference between the database and what the repository already holds (<see cref="GitBackupPage"/>) into commits:
/// renamed space folders, then for each page the newest revision of every version (<see cref="RevisionVersions"/>) not yet in the
/// repository as one commit by its author, then moves, trash, restores and publications. Autosaves within a version are not
/// commits of their own (owner, 2026-11-08): the readable tree shows versions, and every revision is still in .atlas. The
/// comparison is of states, not of events, so an interrupted or failed run is completed by the next one; files the local clone
/// already holds (commits not pushed yet) are adopted instead of being committed twice.
/// </summary>
public sealed class GitBackupSync(AtlasDatabase database, AttachmentFiles attachments, ILogger<GitBackupSync> logger)
{
    internal const int RevisionBatch = 200;

    private sealed record PendingRevision(Guid Id, Guid PageId, int Version, Guid AuthorId, DateTime ActiveFrom, Guid GroupId);
    private sealed record Snapshot(IReadOnlyList<GitSpace> Spaces, IReadOnlyDictionary<Guid, GitPage> Pages, Dictionary<Guid, string> Paths);

    /// <summary>Pages whose repository state differs from the database.</summary>
    public async Task<int> PendingAsync(CancellationToken ct)
    {
        var state = await LoadStateAsync(ct);
        var snapshot = await LoadSnapshotAsync(state, ct);
        return snapshot.Pages.Values.Count(page => Differs(page, state.GetValueOrDefault(page.Id), snapshot.Paths));
    }

    public async Task ResetStateAsync(CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        await session.CreateQuery("delete from GitBackupPage").ExecuteUpdateAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static bool Differs(GitPage page, GitBackupPage? state, IReadOnlyDictionary<Guid, string> paths)
        => state is null || page.Version > state.Version
           || (page.IsTrashed ? state.Path is not null : state.Path != paths[page.Id] || state.PublishedVersion != page.PublishedVersion);

    /// <summary>
    /// Commits everything pending into <paramref name="tree"/>. <paramref name="checkpoint"/> is called after every batch and at the
    /// end with the page states changed since the previous call: it pushes and then stores them, so the database never claims
    /// more than the remote holds. <paramref name="reconcile"/> also re-renders every page (links to moved pages, repairs).
    /// </summary>
    public async Task RunAsync(GitWorkingTree tree, Func<IReadOnlyCollection<GitBackupPage>, CancellationToken, Task> checkpoint,
        bool reconcile, Action<int, int> progress, CancellationToken ct)
    {
        var state = await LoadStateAsync(ct);
        var dirty = new Dictionary<Guid, GitBackupPage>();
        var users = await LoadUsersAsync(ct);
        var first = await LoadSnapshotAsync(state, ct);
        var total = first.Pages.Values.Sum(x => Math.Max(0, x.Version - (state.GetValueOrDefault(x.Id)?.Version ?? 0)));
        var done = 0;
        progress(done, total);

        // 0. A renamed space moves its folder first, so later commits are not attributed to whoever edits in it next.
        var moved = await CommitSpaceFoldersAsync(tree, first, state, dirty, users, ct);
        await checkpoint(Flush(dirty), ct);

        // 1. The newest revision of each version, oldest first, one commit each; pushed every RevisionBatch commits. Revisions saved
        // meanwhile are picked up by the next round.
        for (var snapshot = await LoadSnapshotAsync(state, ct); ; snapshot = await LoadSnapshotAsync(state, ct))
        {
            var pending = await LoadPendingAsync(snapshot, state, ct);
            if (pending.Count == 0)
                break;
            foreach (var batch in pending.Chunk(RevisionBatch))
            {
                foreach (var revision in batch)
                {
                    done += revision.Version - (state.GetValueOrDefault(revision.PageId)?.Version ?? 0);
                    moved |= await CommitRevisionAsync(tree, snapshot, snapshot.Pages[revision.PageId], revision, state, dirty, users, ct);
                    progress(done, Math.Max(total, done));
                }
                await checkpoint(Flush(dirty), ct);
                snapshot = await LoadSnapshotAsync(state, ct);
            }
        }

        // 2. Placement and publication without a new revision.
        moved |= await CommitOrganizationAsync(tree, await LoadSnapshotAsync(state, ct), state, dirty, users, ct);
        await checkpoint(Flush(dirty), ct);

        // 3. Everything else the tree should or should not contain.
        if (reconcile || moved)
        {
            await ReconcileAsync(tree, await LoadSnapshotAsync(state, ct), state, users, ct);
            await checkpoint(Flush(dirty), ct);
        }
    }

    private static IReadOnlyCollection<GitBackupPage> Flush(Dictionary<Guid, GitBackupPage> dirty)
    {
        var changed = dirty.Values.ToList();
        dirty.Clear();
        return changed;
    }

    private async Task<bool> CommitRevisionAsync(GitWorkingTree tree, Snapshot snapshot, GitPage page, PendingRevision pending,
        Dictionary<Guid, GitBackupPage> state, Dictionary<Guid, GitBackupPage> dirty, Dictionary<Guid, GitIdentity> users, CancellationToken ct)
    {
        state.TryGetValue(page.Id, out var previous);
        var path = RevisionPath(page, previous, snapshot, state);
        if (Adopted(tree, page, pending, previous, path, snapshot) is { } held)
        {
            // Committed locally by an earlier run whose push or state update did not complete.
            Set(state, dirty, new GitBackupPage { PageId = page.Id, Path = held, Version = pending.Version, PublishedVersion = previous?.PublishedVersion });
            return false;
        }
        var (revision, assets) = await LoadRevisionAsync(pending.Id, ct);
        var renamed = previous?.Path is { } old && old != path;
        if (renamed)
            Move(tree, previous!.Path!, path, page.Id, state, dirty);
        var current = pending.Version == page.Version;
        await WritePageAsync(tree, page, revision, assets, path, current ? page.PublishedVersion : null, snapshot.Paths, users, ct);
        var subject = previous is null ? $"docs: create {path}" : renamed ? $"docs: rename {previous.Path} to {path}" : $"docs: update {path}";
        await tree.CommitAsync(subject, Author(users, pending.AuthorId), pending.ActiveFrom,
            [$"Atlas-Page: {page.Id:D}", $"Atlas-Version: {pending.Version}"], ct);
        Set(state, dirty, new GitBackupPage
        {
            PageId = page.Id, Path = path, Version = pending.Version,
            PublishedVersion = current ? page.PublishedVersion : previous?.PublishedVersion
        });
        return renamed;
    }

    /// <summary>
    /// Where a revision is written: a renamed page stays in the folder the repository holds it in (a moved parent or space follows
    /// in its own commit), a new page goes to its place in the tree, a trashed one to its last path. Never onto another page's file.
    /// </summary>
    private static string RevisionPath(GitPage page, GitBackupPage? previous, Snapshot snapshot, Dictionary<Guid, GitBackupPage> state)
    {
        if (page.IsTrashed)
            return previous?.Path ?? GitBackupLayout.DetachedPath(page, snapshot.Paths, snapshot.Spaces);
        var desired = snapshot.Paths[page.Id];
        var candidates = previous?.Path is { } old
            ? new[] { old[..(old.LastIndexOf('/') + 1)] + desired[(desired.LastIndexOf('/') + 1)..], desired }
            : [desired];
        return candidates.FirstOrDefault(x => !Occupied(state, x, page.Id)) ?? GitBackupLayout.DetachedPath(page, snapshot.Paths, snapshot.Spaces);
    }

    /// <summary>The path at which the working tree already holds this or a later revision of the page, if any.</summary>
    private static string? Adopted(GitWorkingTree tree, GitPage page, PendingRevision pending, GitBackupPage? previous, string path, Snapshot snapshot)
    {
        if (GitBackupLayout.NativeRevision(tree.ReadText(GitBackupLayout.NativePath(page.Id))) is not { } held || held < pending.Version)
            return null;
        var identity = $"\nid: {page.Id:D}\n";
        return new[] { path, snapshot.Paths.GetValueOrDefault(page.Id), previous?.Path }.OfType<string>().Distinct()
            .FirstOrDefault(x => tree.ReadText(GitBackupLayout.MarkdownPath(x))?.Contains(identity, StringComparison.Ordinal) == true);
    }

    private static bool Occupied(Dictionary<Guid, GitBackupPage> state, string path, Guid page)
        => state.Values.Any(x => x.PageId != page && x.Path == path);

    /// <summary>A renamed space moves its whole folder in one commit.</summary>
    private async Task<bool> CommitSpaceFoldersAsync(GitWorkingTree tree, Snapshot snapshot, Dictionary<Guid, GitBackupPage> state,
        Dictionary<Guid, GitBackupPage> dirty, Dictionary<Guid, GitIdentity> users, CancellationToken ct)
    {
        var any = false;
        foreach (var space in snapshot.Spaces)
        {
            var pages = snapshot.Pages.Values.Where(x => x.SpaceId == space.Id && !x.IsTrashed && state.GetValueOrDefault(x.Id)?.Path is not null).ToList();
            var folders = pages.Select(x => Folder(state[x.Id].Path!)).Distinct(StringComparer.Ordinal).ToList();
            var target = pages.Select(x => Folder(snapshot.Paths[x.Id])).Distinct(StringComparer.Ordinal).ToList();
            if (folders.Count != 1 || target.Count != 1 || folders[0] == target[0]
                || snapshot.Pages.Values.Any(x => x.SpaceId != space.Id && state.GetValueOrDefault(x.Id)?.Path is { } other && Folder(other) == folders[0])
                || !tree.MoveFolder(folders[0], target[0]))
                continue;
            // Trashed pages still in the folder move along; their deletion commit follows.
            foreach (var moved in state.Values.Where(x => x.Path?.StartsWith(folders[0] + "/", StringComparison.Ordinal) == true).ToList())
                Set(state, dirty, Copy(moved, target[0] + moved.Path![folders[0].Length..]));
            await tree.CommitAsync($"docs: move {folders[0]} to {target[0]}", Author(users, space.ChangedBy), space.ChangedAt ?? DateTime.UtcNow,
                [$"Atlas-Space: {space.Id:D}"], ct);
            any = true;
        }
        return any;

        static string Folder(string path) => path.Split('/')[0];
    }

    private async Task<bool> CommitOrganizationAsync(GitWorkingTree tree, Snapshot snapshot, Dictionary<Guid, GitBackupPage> state,
        Dictionary<Guid, GitBackupPage> dirty, Dictionary<Guid, GitIdentity> users, CancellationToken ct)
    {
        var any = false;
        // One commit per trash batch (a page with its subtree), in trash order, named after the batch's top page. A batch trashed
        // inside an earlier one may be gone already.
        foreach (var batch in snapshot.Pages.Values.Where(x => x.IsTrashed && state.GetValueOrDefault(x.Id)?.Path is not null)
                     .GroupBy(x => x.TrashBatchId ?? x.Id).OrderBy(x => x.Min(page => page.TrashedAt)).ThenBy(x => x.Key).ToList())
        {
            var members = batch.Where(x => state[x.Id].Path is not null).ToList();
            if (members.Count == 0)
                continue;
            var ids = members.Select(x => x.Id).ToHashSet();
            var top = members.OrderBy(x => x.ParentId is { } parent && ids.Contains(parent) ? 1 : 0)
                .ThenBy(x => state[x.Id].Path!.Count(c => c == '/')).ThenBy(x => x.Id).First();
            var subject = $"docs: delete {state[top.Id].Path}";
            foreach (var page in members)
            {
                if (state[page.Id].Path is not { } removed)
                    continue;
                tree.Delete(GitBackupLayout.MarkdownPath(removed));
                tree.Delete(GitBackupLayout.AssetsPath(removed));
                tree.Delete(removed);
                tree.Delete(GitBackupLayout.NativePath(page.Id));
                // Trashed descendants went with the folder. A live one (moved out meanwhile) is rewritten at its place below.
                foreach (var descendant in state.Values.Where(x => x.Path?.StartsWith(removed + "/", StringComparison.Ordinal) == true
                             && snapshot.Pages.GetValueOrDefault(x.PageId)?.IsTrashed != false).ToList())
                {
                    tree.Delete(GitBackupLayout.NativePath(descendant.PageId));
                    Set(state, dirty, Copy(descendant, null));
                }
                Set(state, dirty, Copy(state[page.Id], null));
            }
            await tree.CommitAsync(subject, Author(users, top.OrganizerId ?? top.TrashedById), top.TrashedAt ?? top.OrganizedAt ?? DateTime.UtcNow,
                [$"Atlas-Page: {top.Id:D}"], ct);
            any = true;
        }
        // Parents first: moving a page takes its children folder along.
        var ordered = snapshot.Pages.Values.Where(x => !x.IsTrashed)
            .OrderBy(x => snapshot.Paths[x.Id].Count(c => c == '/')).ThenBy(x => x.Id);
        foreach (var page in ordered)
        {
            // A revision saved during this run is committed by its author in the next run, not folded in here.
            if (!state.TryGetValue(page.Id, out var previous) || previous.Version != page.Version)
                continue;
            var path = snapshot.Paths[page.Id];
            var restored = previous.Path is null;
            var relocated = !restored && previous.Path != path;
            if (!restored && !relocated && previous.PublishedVersion == page.PublishedVersion)
                continue;
            // The target still holds another page that moves away later in this run or in the next one.
            if ((restored || relocated) && Occupied(state, path, page.Id))
                continue;
            if (relocated)
                Move(tree, previous.Path!, path, page.Id, state, dirty);
            var (revision, assets) = await LoadRevisionAsync(page.CurrentRevisionId, ct);
            await WritePageAsync(tree, page, revision, assets, path, page.PublishedVersion, snapshot.Paths, users, ct);
            var (subject, actor, at) = restored ? ($"docs: restore {path}", page.OrganizerId, page.OrganizedAt)
                : relocated ? ($"docs: move {previous.Path} to {path}", page.OrganizerId, page.OrganizedAt)
                : ($"docs: publish {path}", page.PublisherId, page.PublishedAt);
            await tree.CommitAsync(subject, Author(users, actor), at ?? DateTime.UtcNow, [$"Atlas-Page: {page.Id:D}"], ct);
            Set(state, dirty, new GitBackupPage { PageId = page.Id, Path = path, Version = revision.Version, PublishedVersion = page.PublishedVersion });
            any |= restored || relocated;
        }
        return any;
    }

    /// <summary>Re-renders every page and removes files no page owns; one commit by Atlas when anything differed.</summary>
    private async Task ReconcileAsync(GitWorkingTree tree, Snapshot snapshot, Dictionary<Guid, GitBackupPage> state,
        Dictionary<Guid, GitIdentity> users, CancellationToken ct)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal) { GitBackupLayout.ReadmePath, GitBackupLayout.MarkerPath };
        var untouched = new List<string>();
        foreach (var page in snapshot.Pages.Values.Where(x => state.GetValueOrDefault(x.Id)?.Path is not null))
        {
            var path = state[page.Id].Path!;
            expected.Add(GitBackupLayout.MarkdownPath(path));
            expected.Add(GitBackupLayout.NativePath(page.Id));
            if (page.IsTrashed || page.Version != state[page.Id].Version)
            {
                // Changed during this run: the next run commits it by its author; leave its files alone.
                untouched.Add(GitBackupLayout.AssetsPath(path) + "/");
                continue;
            }
            var (revision, assets) = await LoadRevisionAsync(page.CurrentRevisionId, ct);
            var names = await WritePageAsync(tree, page, revision, assets, path, state[page.Id].PublishedVersion, snapshot.Paths, users, ct);
            foreach (var name in names)
                expected.Add(GitBackupLayout.AssetsPath(path) + "/" + name);
        }
        foreach (var file in tree.Files().Where(x => !expected.Contains(x) && !GitBackupLayout.IsInstallationData(x)
                     && !untouched.Any(prefix => x.StartsWith(prefix, StringComparison.Ordinal))).ToList())
            tree.Delete(file);
        await tree.CommitAsync("docs: resync", GitIdentity.Committer, DateTime.UtcNow, [], ct);
    }

    /// <summary>Moves a page's files; a path another page already took over keeps that page's files.</summary>
    private static void Move(GitWorkingTree tree, string from, string to, Guid page, Dictionary<Guid, GitBackupPage> state, Dictionary<Guid, GitBackupPage> dirty)
    {
        if (Occupied(state, from, page))
            return;
        tree.Delete(GitBackupLayout.MarkdownPath(from));
        tree.Delete(GitBackupLayout.AssetsPath(from));
        if (!tree.MoveFolder(from, to))
            return;
        foreach (var descendant in state.Values.Where(x => x.Path?.StartsWith(from + "/", StringComparison.Ordinal) == true).ToList())
            Set(state, dirty, Copy(descendant, to + descendant.Path![from.Length..]));
    }

    private static GitBackupPage Copy(GitBackupPage page, string? path)
        => new() { PageId = page.PageId, Path = path, Version = page.Version, PublishedVersion = page.PublishedVersion };

    private static void Set(Dictionary<Guid, GitBackupPage> state, Dictionary<Guid, GitBackupPage> dirty, GitBackupPage page)
    {
        state[page.PageId] = page;
        dirty[page.PageId] = page;
    }

    /// <summary>Writes the Markdown, the native document and the attachments of one revision; returns the attachment file names.</summary>
    private async Task<IReadOnlyCollection<string>> WritePageAsync(GitWorkingTree tree, GitPage page, PageRevision revision, IList<Attachment> assets,
        string path, int? published, IReadOnlyDictionary<Guid, string> paths, Dictionary<Guid, GitIdentity> users, CancellationToken ct)
    {
        var names = GitBackupLayout.AssetNames(assets);
        var folder = GitBackupLayout.AssetsPath(path);
        var files = names.ToDictionary(x => x.Key, x => folder + "/" + x.Value);
        var directory = path[..path.LastIndexOf('/')];
        var body = BackupFormat.PortableMarkdown(revision.Markdown, directory,
            id => paths.TryGetValue(id, out var target) ? GitBackupLayout.MarkdownPath(target) : null, files);
        await tree.WriteTextAsync(GitBackupLayout.MarkdownPath(path),
            GitBackupLayout.Markdown(page.Id, revision.Title, revision.Version, published, revision.ActiveFrom, Author(users, revision.AuthorId).Name, body), ct);
        var recorded = GitBackupLayout.NativeAttachments(tree.ReadText(GitBackupLayout.NativePath(page.Id)));
        var nativeAssets = assets.OrderBy(x => names[x.Id], StringComparer.Ordinal).Select(x => new JsonObject
        {
            ["id"] = x.Id.ToString("D"), ["file"] = names[x.Id], ["name"] = x.Name, ["mimeType"] = x.MimeType, ["size"] = x.Size, ["sha256"] = x.Sha256
        }).ToList<JsonNode?>();
        await tree.WriteTextAsync(GitBackupLayout.NativePath(page.Id),
            GitBackupLayout.Native(page.Id, page.ParentId, revision.Title, revision.Version, revision.ContentJson, nativeAssets), ct);
        foreach (var asset in assets)
        {
            try
            {
                await tree.WriteFileAsync(files[asset.Id], () => attachments.Open(asset.Id), asset.Size, asset.Sha256,
                    recorded.TryGetValue(names[asset.Id], out var known) && known.Id == asset.Id.ToString("D") && known.Sha256 == asset.Sha256, ct);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // One lost blob must not stop the backup of the whole wiki; the native document still lists it.
                logger.LogWarning("Attachment {AttachmentId} of page {PageId} has no file; the git backup leaves it out", asset.Id, page.Id);
            }
        }
        tree.Retain(folder, names.Values.ToHashSet(StringComparer.Ordinal));
        return names.Values;
    }

    private static GitIdentity Author(Dictionary<Guid, GitIdentity> users, Guid? id)
        => id is { } user && users.TryGetValue(user, out var identity) ? identity : GitIdentity.Committer;

    private async Task<Dictionary<Guid, GitIdentity>> LoadUsersAsync(CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        var users = await session.Query<UserAccount>().ToListAsync(ct);
        await tx.CommitAsync(ct);
        return users.ToDictionary(x => x.Id, x => new GitIdentity(
            string.IsNullOrWhiteSpace(x.DisplayName) ? x.Username : x.DisplayName.Trim(),
            string.IsNullOrWhiteSpace(x.Email) ? GitBackupLayout.Slug(x.Username, "user") + "@atlas.invalid" : x.Email.Trim()));
    }

    private async Task<Dictionary<Guid, GitBackupPage>> LoadStateAsync(CancellationToken ct)
    {
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        var rows = await session.Query<GitBackupPage>().ToListAsync(ct);
        await tx.CommitAsync(ct);
        return rows.ToDictionary(x => x.PageId);
    }

    /// <summary>Pages, spaces and their current revision, publication and organization in one repeatable-read snapshot.</summary>
    private async Task<Snapshot> LoadSnapshotAsync(IReadOnlyDictionary<Guid, GitBackupPage> state, CancellationToken ct)
    {
        using var session = database.OpenSession();
        session.DefaultReadOnly = true;
        using var tx = session.BeginTransaction(IsolationLevel.RepeatableRead);
        var pages = await session.Query<Page>().ToListAsync(ct);
        var current = (await session.CreateQuery("select r.PageId, r.Id, r.Title from PageRevision r where r.ActiveTo is null")
                .ListAsync<object[]>(ct)).ToDictionary(x => (Guid)x[0], x => (Id: (Guid)x[1], Title: (string)x[2]));
        var published = (await session.CreateQuery("select p.Id, r.Version from Page p, PageRevision r where r.Id = p.PublishedRevisionId")
                .ListAsync<object[]>(ct)).ToDictionary(x => (Guid)x[0], x => (int)x[1]);
        var publications = (await session.Query<PagePublication>().Where(x => x.ActiveTo == null).ToListAsync(ct))
            .GroupBy(x => x.PageId).ToDictionary(x => x.Key, x => x.First());
        var organization = (await session.Query<PageOrganizationRevision>().Where(x => x.ActiveTo == null).ToListAsync(ct))
            .GroupBy(x => x.PageId).ToDictionary(x => x.Key, x => x.First());
        var spaceChanges = (await session.Query<SpaceMetadataRevision>().Where(x => x.ActiveTo == null).ToListAsync(ct))
            .GroupBy(x => x.SpaceId).ToDictionary(x => x.Key, x => x.First());
        var spaces = (await session.Query<Space>().ToListAsync(ct))
            .Select(x => new GitSpace(x.Id, x.Name, spaceChanges.GetValueOrDefault(x.Id)?.ChangedBy, Utc(spaceChanges.GetValueOrDefault(x.Id)?.ActiveFrom)))
            .ToList();
        await tx.CommitAsync(ct);
        var snapshot = pages.Where(x => current.ContainsKey(x.Id)).Select(x =>
        {
            var publication = publications.GetValueOrDefault(x.Id);
            var organized = organization.GetValueOrDefault(x.Id);
            return new GitPage(x.Id, x.SpaceId, x.ParentId, x.Version, x.IsTrashed, current[x.Id].Title, current[x.Id].Id,
                published.TryGetValue(x.Id, out var version) ? version : null, publication?.PublisherId, Utc(publication?.ActiveFrom),
                organized?.ActorId, Utc(organized?.ActiveFrom), x.TrashedById, Utc(x.TrashedAt), x.TrashBatchId, x.PurgedAt is not null);
        }).ToDictionary(x => x.Id);
        return new Snapshot(spaces, snapshot, GitBackupLayout.Paths(spaces, snapshot.Values, state));
    }

    private static DateTime? Utc(DateTime? value) => value is { } time ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : null;

    /// <summary>The revisions the repository is behind on, oldest first: of each version only its newest one, the page's newest revision
    /// included even while its version is still open (the next run commits the rest of that version again as one commit).</summary>
    private async Task<IReadOnlyList<PendingRevision>> LoadPendingAsync(Snapshot snapshot, Dictionary<Guid, GitBackupPage> state, CancellationToken ct)
    {
        // A purged page's revisions are emptied: never written. Being trashed, its file (if any) gets the batch's deletion commit.
        var behind = snapshot.Pages.Values.Where(x => !x.IsPurged && x.Version > (state.GetValueOrDefault(x.Id)?.Version ?? 0)).Select(x => x.Id).ToList();
        var result = new List<PendingRevision>();
        using var session = database.OpenSession();
        using var tx = session.BeginTransaction();
        foreach (var chunk in behind.Chunk(500))
        {
            var rows = await session.CreateQuery("select r.Id, r.PageId, r.Version, r.AuthorId, r.ActiveFrom, r.GroupId from PageRevision r where r.PageId in (:pages)")
                .SetParameterList("pages", chunk).ListAsync<object[]>(ct);
            // The snapshot may be older than a revision saved since: never ahead of the page as the snapshot sees it.
            // A revision from before 3.6 has no group: it is a version of its own (PageRevision.GroupId).
            result.AddRange(rows.Select(x => new PendingRevision((Guid)x[0], (Guid)x[1], (int)x[2], (Guid)x[3], Utc((DateTime)x[4])!.Value,
                    x[5] is Guid group && group != Guid.Empty ? group : (Guid)x[0]))
                .Where(x => x.Version > (state.GetValueOrDefault(x.PageId)?.Version ?? 0) && x.Version <= snapshot.Pages[x.PageId].Version));
        }
        await tx.CommitAsync(ct);
        var newest = result.GroupBy(x => x.PageId).SelectMany(page =>
        {
            var revisions = page.OrderBy(x => x.Version).ToList();
            return revisions.Where((x, i) => i == revisions.Count - 1 || revisions[i + 1].GroupId != x.GroupId);
        });
        return newest.OrderBy(x => x.ActiveFrom).ThenBy(x => x.PageId).ThenBy(x => x.Version).ToList();
    }

    private async Task<(PageRevision Revision, IList<Attachment> Assets)> LoadRevisionAsync(Guid id, CancellationToken ct)
    {
        using var session = database.OpenSession();
        session.DefaultReadOnly = true;
        using var tx = session.BeginTransaction();
        var revision = await session.GetAsync<PageRevision>(id, ct) ?? throw new GitException("Revize stránky nebyla nalezena.");
        var assets = await session.CreateQuery(
                "from Attachment a where a.Id in (select b.AttachmentId from PageRevisionAttachment b where b.RevisionId = :revision)")
            .SetParameter("revision", id).ListAsync<Attachment>(ct);
        await tx.CommitAsync(ct);
        return (revision, assets);
    }
}
