using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Atlas.Server.Features.Backups.Git;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Attachments;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Backups;

/// <summary>The real git CLI against a local bare repository (file://); the SSH transport is covered by the Podman e2e.</summary>
public sealed class GitBackupTests
{
    [PostgreSqlFact]
    public async Task Every_saved_version_is_one_commit_by_its_author_with_attachments_and_readable_markdown()
    {
        await using var f = await GitFixture.CreateAsync();
        var created = await f.Pages.As<PageDto>(f.Pages.Writer, new CreatePage(new CreatePageRequest(f.Pages.SpaceId, null, "Nasazení v Revma",
            AttachmentFixture.Rich("first"), "first")));
        // A draft is its author's until published; the co-author edits the published page.
        await f.Pages.As<PageDto>(f.Pages.Writer, new PublishPage(created.Id, new PublishPageRequest(created.Version)));
        var upload = await f.Pages.UploadAsync(f.Pages.OtherWriter, created.Id, "notes.txt", new MemoryStream(Encoding.UTF8.GetBytes("vpn notes")));
        // An attachment git would read as its own configuration.
        var ignore = await f.Pages.UploadAsync(f.Pages.OtherWriter, created.Id, ".gitignore", new MemoryStream(Encoding.UTF8.GetBytes("*\n")));
        var saved = await f.Pages.As<PageDto>(f.Pages.OtherWriter, new SavePage(created.Id, new SavePageRequest("Nasazení v Revma",
            f.Attachment(upload.DownloadUrl, ignore.DownloadUrl), $"See [notes]({upload.DownloadUrl}) and [rules]({ignore.DownloadUrl}).", created.Version)));
        await f.Pages.As<PageDto>(f.Pages.Writer, new PublishPage(created.Id, new PublishPageRequest(saved.Version)));

        var status = await f.EnableAndSyncAsync();

        Assert.Null(status.LastError);
        Assert.Equal(0, status.PendingPages);
        Assert.Equal(await f.RemoteAsync("rev-parse", "main"), status.LastCommit);
        Assert.Equal([
            "Atlas|atlas@atlas.invalid|docs: initialize Atlas backup",
            "writer|writer@example.test|docs: create history/nasazeni-v-revma",
            "other|other@atlas.invalid|docs: update history/nasazeni-v-revma"
        ], await f.LogAsync());
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(saved.ActiveFrom, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(), await f.RemoteAsync("log", "-1", "--format=%at", "--invert-grep", "--grep=^chore: snapshot installation", "main"));
        Assert.Equal([
            ".atlas/pages/" + created.Id + ".json", ".atlas/repository.json", "README.md",
            "history/nasazeni-v-revma.assets/_.gitignore", "history/nasazeni-v-revma.assets/notes.txt", "history/nasazeni-v-revma.md"
        ], await f.FilesAsync());
        var markdown = await f.RemoteAsync("show", "main:history/nasazeni-v-revma.md");
        Assert.StartsWith("---\ntitle: \"Nasazení v Revma\"\nid: " + created.Id + "\nversion: 2\npublished: 2\n", markdown);
        // The stored Markdown is the server's projection of the attachment blocks, with portable paths.
        Assert.Contains("file · ../history/nasazeni-v-revma.assets/notes.txt\n\nfile · ../history/nasazeni-v-revma.assets/_.gitignore", markdown);
        Assert.Equal("vpn notes", await f.RemoteAsync("show", "main:history/nasazeni-v-revma.assets/notes.txt"));
        Assert.Contains("\"sha256\"", await f.RemoteAsync("show", $"main:.atlas/pages/{created.Id}.json"));

        // Nothing changed: a manual run reconciles without an empty commit; a run of the job picks up the next save.
        await f.Service.SyncAsync(true, default);
        Assert.Equal(3, (await f.LogAsync()).Count);
        await f.Pages.As<PageDto>(f.Pages.Writer, new SavePage(created.Id, new SavePageRequest("Nasazení v Revma", AttachmentFixture.Rich("third"), "third", saved.Version)));
        Assert.Null(await f.Service.SyncAsync(false, default));
        Assert.Equal("writer|writer@example.test|docs: update history/nasazeni-v-revma", (await f.LogAsync())[^1]);
        Assert.DoesNotContain("notes.txt", string.Join('\n', await f.FilesAsync()));
    }

    [PostgreSqlFact]
    public async Task Rename_move_trash_and_restore_follow_the_tree_with_their_actors()
    {
        await using var f = await GitFixture.CreateAsync();
        var parent = await f.CreateAsync("Parent", null);
        var child = await f.CreateAsync("Child", parent);
        await f.EnableAndSyncAsync();

        await f.Pages.As(f.Pages.Writer, new RenamePage(child, new RenamePageRequest("Renamed", 1)));
        await f.SyncAsync();
        Assert.Equal("writer|writer@example.test|docs: rename history/parent/child to history/parent/renamed", (await f.LogAsync())[^1]);

        await f.Pages.As<OrganizationChange>(f.Pages.OtherWriter, new MovePage(child, new MovePageRequest(null, null, await f.TreeVersionAsync())));
        await f.SyncAsync();
        Assert.Equal("other|other@atlas.invalid|docs: move history/parent/renamed to history/renamed", (await f.LogAsync())[^1]);
        Assert.Contains("history/renamed.md", await f.FilesAsync());

        // Moved back and trashed with its parent before the next run: one commit for the trashed subtree.
        await f.Pages.As<OrganizationChange>(f.Pages.Writer, new MovePage(child, new MovePageRequest(parent, null, await f.TreeVersionAsync())));
        await f.Pages.As<OrganizationChange>(f.Pages.Writer, new TrashPage(parent, new TrashPageRequest(true, await f.TreeVersionAsync(), 1)));
        var before = (await f.LogAsync()).Count;
        await f.SyncAsync();
        var log = await f.LogAsync();
        Assert.Equal(before + 1, log.Count);
        Assert.Equal("writer|writer@example.test|docs: delete history/parent", log[^1]);
        Assert.Equal([".atlas/repository.json", "README.md"], await f.FilesAsync());

        var admin = await f.Pages.Host.SeedUserAsync("restorer", isWikiAdmin: true);
        await f.Pages.As<OrganizationChange>(admin, new UntrashPage(parent, new UntrashPageRequest(null, true, await f.TreeVersionAsync())));
        await f.SyncAsync();
        Assert.Equal(["restorer|restorer@atlas.invalid|docs: restore history/parent", "restorer|restorer@atlas.invalid|docs: restore history/parent/renamed"],
            (await f.LogAsync())[^2..]);
        Assert.Contains("history/parent/renamed.md", await f.FilesAsync());

        // A renamed space moves its folder in one commit; the sibling names stay.
        await f.Pages.Execute("UPDATE \"Space\" SET \"Name\" = 'Provoz' WHERE \"Id\" = :id", f.Pages.SpaceId);
        await f.SyncAsync();
        Assert.Equal("Atlas|atlas@atlas.invalid|docs: move history to provoz", (await f.LogAsync())[^1]);
        Assert.Contains("provoz/parent/renamed.md", await f.FilesAsync());

        // The child goes to the trash first, then its parent: two batches, in trash order.
        await f.Pages.As<OrganizationChange>(f.Pages.Writer, new TrashPage(child, new TrashPageRequest(false, await f.TreeVersionAsync(), 2)));
        await f.Pages.As<OrganizationChange>(f.Pages.Writer, new TrashPage(parent, new TrashPageRequest(true, await f.TreeVersionAsync(), 1)));
        await f.SyncAsync();
        Assert.Equal(["writer|writer@example.test|docs: delete provoz/parent/renamed", "writer|writer@example.test|docs: delete provoz/parent"],
            (await f.LogAsync())[^2..]);
        Assert.Equal([".atlas/repository.json", "README.md"], await f.FilesAsync());
    }

    /// <summary>A permanent delete (ARCH1-1) leaves the repository as the trash did: a page it holds gets its deletion commit, and one purged
    /// before any run is never written (its emptied revisions included), so a restore from the repository never brings either back.</summary>
    [PostgreSqlFact]
    public async Task A_purged_page_gets_its_deletion_commit_and_one_never_backed_up_is_never_written()
    {
        await using var f = await GitFixture.CreateAsync();
        await f.CreateAsync("Runbook", null);
        var known = await f.CreateAsync("Known", null);
        await f.EnableAndSyncAsync();
        var fresh = await f.CreateAsync("Fresh", null);
        var admin = await f.Pages.Host.SeedUserAsync("purger", isWikiAdmin: true);
        foreach (var page in new[] { known, fresh })
        {
            var version = (await f.Pages.As<PageDto>(f.Pages.Writer, new Atlas.Server.Features.Pages.Queries.GetPage(page))).Version;
            await f.Pages.As<OrganizationChange>(f.Pages.Writer, new TrashPage(page, new TrashPageRequest(false, await f.TreeVersionAsync(), version)));
            await f.Pages.As(admin, new Atlas.Server.Features.Objects.Commands.PurgeObject(page));
        }

        await f.SyncAsync();
        Assert.Equal("writer|writer@example.test|docs: delete history/known", (await f.LogAsync())[^1]);
        Assert.DoesNotContain(await f.LogAsync(), x => x.Contains("fresh", StringComparison.Ordinal));
        var files = await f.FilesAsync();
        Assert.Contains("history/runbook.md", files);
        Assert.DoesNotContain(files, x => x.Contains("known", StringComparison.Ordinal) || x.Contains("fresh", StringComparison.Ordinal)
            || x.Contains(known.ToString("D"), StringComparison.Ordinal) || x.Contains(fresh.ToString("D"), StringComparison.Ordinal));
    }

    [PostgreSqlFact]
    public async Task Lost_state_a_rejected_push_and_a_stale_lock_never_duplicate_commits_and_a_foreign_repository_is_refused()
    {
        await using var f = await GitFixture.CreateAsync();
        var page = await f.CreateAsync("Runbook", null);
        await f.EnableAndSyncAsync();
        var commits = (await f.LogAsync()).Count;

        // A crash between push and storing the state: the replay writes identical files and adds no commit.
        await f.Pages.Execute("DELETE FROM \"GitBackupPage\" WHERE \"PageId\" = :id", page);
        await f.Service.SyncAsync(true, default);
        Assert.Equal(commits, (await f.LogAsync()).Count);

        // The remote refuses the push: two autosaves of one version are committed locally as one commit (its newest revision), the
        // failure is visible, and the retry pushes exactly that commit instead of replaying it again.
        var hook = Path.Combine(f.Remote, "hooks", "pre-receive");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\necho refused >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var v2 = await f.Pages.As<PageDto>(f.Pages.Writer, new SavePage(page, new SavePageRequest("Runbook", AttachmentFixture.Rich("v2"), "v2", 1)));
        await f.Pages.As<PageDto>(f.Pages.Writer, new SavePage(page, new SavePageRequest("Runbook", AttachmentFixture.Rich("v3"), "v3", v2.Version)));
        var failed = await f.Service.SyncAsync(true, default);
        Assert.Contains("refused", failed!.LastError);
        Assert.StartsWith("Záloha do Gitu selhala", await f.Service.WarningAsync(default));
        File.Delete(hook);
        await f.SyncAsync();
        Assert.Equal(commits + 1, (await f.LogAsync()).Count);
        Assert.Equal("writer|writer@example.test|docs: update history/runbook", (await f.LogAsync())[^1]);
        Assert.Contains("version: 3\n", await f.RemoteAsync("show", "main:history/runbook.md"));

        // A git process killed mid-write leaves its lock file; the next run is not stuck on it.
        await File.WriteAllTextAsync(Path.Combine(f.Root, "data", "repository", ".git", "index.lock"), "");
        await f.Pages.As<PageDto>(f.Pages.Writer, new SavePage(page, new SavePageRequest("Runbook", AttachmentFixture.Rich("v4"), "v4", v2.Version + 1)));
        await f.SyncAsync();
        Assert.Equal(commits + 2, (await f.LogAsync()).Count);

        // Another repository with content of its own is never written to.
        var foreign = await f.BareAsync("foreign.git", withCommit: true);
        await f.Service.SaveSettingsAsync(new GitBackupSettingsRequest(true, new Uri(foreign).AbsoluteUri, "main"), default);
        var refused = await f.Service.SyncAsync(true, default);
        Assert.Contains("jiný obsah", refused!.LastError);
        Assert.Equal("1", await f.GitAsync(foreign, "rev-list", "--count", "main"));
    }

    [PostgreSqlFact]
    public async Task Settings_accept_only_ssh_or_file_remotes_and_the_key_and_test_never_expose_or_write_anything()
    {
        await using var f = await GitFixture.CreateAsync();
        foreach (var url in new[] { "https://git.example.cz/a.git", "ext::sh -c touch% /tmp/x", "-oProxyCommand=x", "ssh://host", "git@host:/abs.git" })
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SaveSettingsAsync(new(false, url, "main"), default))).Status);
        foreach (var branch in new[] { "-x", "a..b", "x.lock", "a b" })
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SaveSettingsAsync(new(false, f.Url, branch), default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.SaveSettingsAsync(new(true, f.Url, "main"), default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.TestAsync(new(f.Url, "main"), default))).Status);
        await f.Service.SaveSettingsAsync(new(false, "git@git.example.test:Infrastructure/atlas-backup.git", "main"), default);

        var status = await f.Service.GenerateKeyAsync(default);
        Assert.StartsWith("ssh-ed25519 ", status.Settings.PublicKey);
        Assert.DoesNotContain("PRIVATE KEY", System.Text.Json.JsonSerializer.Serialize(status));
        Assert.StartsWith("Přístup ověřen", await f.Service.TestAsync(new(f.Url, "main"), default));
        Assert.Equal("", await f.GitAsync(f.Remote, "for-each-ref"));

        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Strict().SaveSettingsAsync(new(false, f.Url, "main"), default))).Status);
        var other = await f.BareAsync("other.git", withCommit: true, branch: "master");
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Service.TestAsync(new(new Uri(other).AbsoluteUri, "main"), default))).Status);
    }

    [PostgreSqlFact]
    public async Task An_idle_run_of_the_job_compares_but_never_reaches_the_remote_and_a_failed_one_retries_its_push()
    {
        // A run with nothing to compare or snapshot ends before the local clone and the network; only a failure since makes the next
        // run push whatever the clone still holds.
        await using var f = await GitFixture.CreateAsync();
        var page = await f.CreateAsync("Runbook", null);
        await f.EnableAndSyncAsync();
        var synced = (await f.Service.GetStatusAsync(default)).LastSyncAt;

        await f.TickAsync();

        Assert.Equal(synced, (await f.Service.GetStatusAsync(default)).LastSyncAt);
        var hook = Path.Combine(f.Remote, "hooks", "pre-receive");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\necho refused >&2\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await f.Pages.As<PageDto>(f.Pages.Writer, new SavePage(page, new SavePageRequest("Runbook", AttachmentFixture.Rich("v2"), "v2", 1)));
        // The job's run fails, so its JobRetry tries again; the page shows the reason meanwhile.
        Assert.Contains("refused", (await Assert.ThrowsAsync<JobFailedException>(() => f.Service.SyncAsync(false, default))).Message);
        Assert.Contains("refused", (await f.Service.GetStatusAsync(default)).LastError);
        File.Delete(hook);
        await f.TickAsync();
        Assert.Null((await f.Service.GetStatusAsync(default)).LastError);
        Assert.Equal("writer|writer@example.test|docs: update history/runbook", (await f.LogAsync())[^1]);
    }

    [PostgreSqlFact]
    public async Task A_run_of_the_job_exports_every_kind_of_change_including_those_no_publication_announces()
    {
        // A publication asks for a run at once (GitBackupRevision); everything else waits for the run after 10 minutes of quiet, which
        // compares the whole wiki. Each step below is a different kind of change, and the job's run, not a manual one, must export it.
        // A rename is one case of a new revision, trash and restore of the tree. The unpublish closes the publication and clears the
        // published pointer, and nothing else moves: no revision, no new publication row, no place.
        await using var f = await GitFixture.CreateAsync();
        var parent = await f.CreateAsync("Parent", null);
        var child = await f.CreateAsync("Child", parent);
        await f.EnableAndSyncAsync();
        var admin = await f.Pages.Host.SeedUserAsync("restorer", isWikiAdmin: true);

        async Task<string> ExportedAsync(Func<Task> change)
        {
            await f.TickAsync();
            await change();
            await f.TickAsync();
            return (await f.LogAsync())[^1];
        }

        Assert.Equal("writer|writer@example.test|docs: rename history/parent/child to history/parent/renamed",
            await ExportedAsync(() => f.Pages.As(f.Pages.Writer, new RenamePage(child, new RenamePageRequest("Renamed", 1)))));
        Assert.Equal("writer|writer@example.test|docs: publish history/parent/renamed",
            await ExportedAsync(() => f.Pages.As<PageDto>(f.Pages.Writer, new PublishPage(child, new PublishPageRequest(2)))));
        Assert.Equal("other|other@atlas.invalid|docs: move history/parent/renamed to history/renamed",
            await ExportedAsync(async () => await f.Pages.As<OrganizationChange>(f.Pages.OtherWriter, new MovePage(child, new MovePageRequest(null, null, await f.TreeVersionAsync())))));
        Assert.Equal("writer|writer@example.test|docs: delete history/renamed",
            await ExportedAsync(async () => await f.Pages.As<OrganizationChange>(f.Pages.Writer, new TrashPage(child, new TrashPageRequest(false, await f.TreeVersionAsync(), 2)))));
        Assert.Equal("restorer|restorer@atlas.invalid|docs: restore history/renamed",
            await ExportedAsync(async () => await f.Pages.As<OrganizationChange>(admin, new UntrashPage(child, new UntrashPageRequest(null, false, await f.TreeVersionAsync())))));
        Assert.Equal("Atlas|atlas@atlas.invalid|docs: publish history/renamed",
            await ExportedAsync(() => f.Pages.Execute("WITH closed AS (UPDATE \"PagePublication\" SET \"ActiveTo\" = clock_timestamp() WHERE \"PageId\" = :id AND \"ActiveTo\" IS NULL RETURNING 1) "
                + "UPDATE \"Page\" SET \"PublishedRevisionId\" = NULL WHERE \"Id\" = :id", child)));
        Assert.Equal("Atlas|atlas@atlas.invalid|docs: move history to provoz",
            await ExportedAsync(() => f.Pages.Execute("UPDATE \"Space\" SET \"Name\" = 'Provoz' WHERE \"Id\" = :id", f.Pages.SpaceId)));
    }

    [PostgreSqlFact]
    public async Task The_job_compares_again_after_ten_quiet_minutes_or_at_the_snapshot_and_finds_lost_state()
    {
        // The repository state (GitBackupPage) can lose a row (a crash between the push and storing it, a row removed by hand); no
        // event tells, so the run after quiet compares the whole wiki. It is planned 10 minutes after the last run, or at the snapshot
        // interval if that comes first, and not at all while the backup is off.
        await using var f = await GitFixture.CreateAsync();
        var page = await f.CreateAsync("Runbook", null);
        Assert.Null(await f.Service.NextDueAsync(default));
        await f.EnableAndSyncAsync();
        var synced = f.Clock.GetUtcNow().UtcDateTime;
        Assert.Equal(synced + GitBackupService.FullComparisonAfter, await f.Service.NextDueAsync(default));
        f.Clock.Advance(GitBackupService.FullComparisonAfter);
        Assert.Equal(synced + GitBackupService.SnapshotInterval, await f.Service.NextDueAsync(default));

        await f.Pages.Execute("DELETE FROM \"GitBackupPage\" WHERE \"PageId\" = :id", page);
        Assert.Equal(1, (await f.Service.GetStatusAsync(default)).PendingPages);
        await f.TickAsync();

        Assert.Equal(0, (await f.Service.GetStatusAsync(default)).PendingPages);
        await f.Service.SaveSettingsAsync(new GitBackupSettingsRequest(false, f.Url, "main"), default);
        Assert.Null(await f.Service.NextDueAsync(default));
    }

    [Fact]
    public void Paths_are_lowercase_ascii_and_the_incumbent_keeps_a_contested_name()
    {
        Assert.Equal("nasazeni-v-revma", GitBackupLayout.Slug("Nasazení v Revma"));
        Assert.Equal("prilis-zlutoucky-kun", GitBackupLayout.Slug("  Příliš ŽLUŤOUČKÝ kůň!! "));
        Assert.Equal("stranka", GitBackupLayout.Slug("🙂"));
        Assert.Equal("con-", GitBackupLayout.Slug("CON"));
        var space = new GitSpace(Guid.NewGuid(), "Projekty", null, null);
        GitPage Page(Guid id, string title, Guid? parent = null) => new(id, space.Id, parent, 1, false, title, Guid.NewGuid(), null, null, null, null, null, null, null);
        var first = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var child = Guid.NewGuid();
        var pages = new[] { Page(first, "Docker"), Page(second, "docker"), Page(child, "VPN", first) };
        var fresh = GitBackupLayout.Paths([space], pages, new Dictionary<Guid, GitBackupPage>());
        Assert.Equal("projekty/docker", fresh[second]);
        Assert.Equal("projekty/docker-00000000", fresh[first]);
        Assert.Equal("projekty/docker-00000000/vpn", fresh[child]);
        var held = GitBackupLayout.Paths([space], pages, new Dictionary<Guid, GitBackupPage> { [first] = new() { PageId = first, Path = "projekty/docker", Version = 1 } });
        Assert.Equal("projekty/docker", held[first]);
        Assert.StartsWith("projekty/docker-", held[second]);
        var renamed = new GitSpace(space.Id, "Provoz", null, null);
        var afterRename = GitBackupLayout.Paths([renamed], pages, new Dictionary<Guid, GitBackupPage> { [first] = new() { PageId = first, Path = "projekty/docker", Version = 1 } });
        Assert.Equal("provoz/docker", afterRename[first]);
        Assert.Contains("-i '/data/it'\\''s/key'", GitCommand.SshCommand("/data/it's/key", "/k"));
    }

    internal sealed class GitFixture : IAsyncDisposable
    {
        public required AttachmentFixture Pages { get; init; }
        public required string Root { get; init; }
        public required GitBackupService Service { get; init; }
        public required InstallationServices Installation { get; init; }
        public required FakeTimeProvider Clock { get; init; }
        public string Remote => Path.Combine(Root, "remote.git");
        public string Url => new Uri(Remote).AbsoluteUri;
        private Func<GitBackupService> m_strict = null!;

        private Func<GitBackupService> m_restarted = null!;

        /// <summary>The production configuration: no local file remotes.</summary>
        public GitBackupService Strict() => m_strict();

        /// <summary>The service of a new process: the same settings, nothing in memory.</summary>
        public GitBackupService Restarted() => m_restarted();

        public static async Task<GitFixture> CreateAsync()
        {
            var pages = await AttachmentFixture.CreateIsolatedAsync();
            await pages.Execute("UPDATE \"UserAccount\" SET \"Email\" = 'writer@example.test' WHERE \"Id\" = :id", pages.Writer.Id);
            var root = Path.Combine(Path.GetTempPath(), "atlas-git-backup-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var configuration = new ConfigurationBuilder()
                .AddConfiguration(pages.Host.Services.GetRequiredService<IConfiguration>())
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GitBackup:DataPath"] = Path.Combine(root, "data"),
                    ["GitBackup:AllowFileRemotes"] = "true"
                })
                .Build();
            var sync = new GitBackupSync(pages.Host.Database, pages.Files, NullLogger<GitBackupSync>.Instance);
            var installation = new InstallationServices(pages.Host, pages.Files, root);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var service = new GitBackupService(pages.Host.Database, sync, installation.Protection, new AttachmentTests.EnvironmentStub(root),
                configuration, NullLogger<GitBackupService>.Instance, installation.Export, installation.Keys, clock);
            var fixture = new GitFixture { Pages = pages, Root = root, Service = service, Installation = installation, Clock = clock };
            fixture.m_strict = () => new GitBackupService(pages.Host.Database, sync, installation.Protection,
                new AttachmentTests.EnvironmentStub(root), pages.Host.Services.GetRequiredService<IConfiguration>(), NullLogger<GitBackupService>.Instance,
                installation.Export, installation.Keys);
            fixture.m_restarted = () => new GitBackupService(pages.Host.Database, sync, installation.Protection, new AttachmentTests.EnvironmentStub(root),
                configuration, NullLogger<GitBackupService>.Instance, installation.Export, installation.Keys, clock);
            await fixture.BareAsync("remote.git", withCommit: false);
            return fixture;
        }

        public string Attachment(params string[] urls) => System.Text.Json.JsonSerializer.Serialize(new
        {
            format = "atlas-rich",
            version = 1,
            plugins = new[] { new { id = "attachments", version = 1 } },
            document = new { type = "doc", content = urls.Select(url => new { type = "attachment", attrs = new { data = new { version = 1, name = "file", url } } }).ToArray() }
        });

        /// <summary>A published page: drafts are visible to their author only.</summary>
        public async Task<Guid> CreateAsync(string title, Guid? parent)
        {
            var page = await Pages.As<PageDto>(Pages.Writer, new CreatePage(new CreatePageRequest(Pages.SpaceId, parent, title, AttachmentFixture.Rich(title), title)));
            await Pages.As<PageDto>(Pages.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
            return page.Id;
        }

        public async Task<long> TreeVersionAsync()
        {
            using var session = Pages.Host.OpenSession();
            return await TreeVersion.ReadAsync(session, Pages.SpaceId, default);
        }

        public async Task<GitBackupStatus> EnableAndSyncAsync()
        {
            await Service.GenerateKeyAsync(default);
            await Service.SaveSettingsAsync(new GitBackupSettingsRequest(true, Url, "main"), default);
            return (await Service.SyncAsync(true, default))!;
        }

        public async Task SyncAsync()
        {
            var status = await Service.SyncAsync(true, default);
            Assert.True(status!.LastError is null, status.LastError);
        }

        /// <summary>One good run of the job (what <see cref="Atlas.Server.Features.Backups.GitBackupJob"/> runs).</summary>
        public async Task TickAsync() => Assert.Null(await Service.SyncAsync(false, default));

        public async Task<string> BareAsync(string name, bool withCommit, string branch = "main")
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            await GitAsync(path, "init", "--quiet", "--bare", "--initial-branch=" + branch);
            if (withCommit)
            {
                var work = Path.Combine(Root, name + "-work");
                Directory.CreateDirectory(work);
                await GitAsync(work, "init", "--quiet", "--initial-branch=" + branch);
                await File.WriteAllTextAsync(Path.Combine(work, "README.md"), "someone else's wiki\n");
                await GitAsync(work, "add", "README.md");
                await GitAsync(work, "commit", "--quiet", "-m", "foreign");
                await GitAsync(work, "push", "--quiet", new Uri(path).AbsoluteUri, "HEAD:refs/heads/" + branch);
            }
            return path;
        }

        public async Task<string> GitAsync(string directory, params string[] arguments)
        {
            var git = new GitCommand(directory, GitCommand.Environment(Root, null));
            return (await git.RunGitAsync(arguments, null, null, true, default)).Output.TrimEnd('\n');
        }

        public Task<string> RemoteAsync(params string[] arguments) => GitAsync(Remote, arguments);

        /// <summary>The page commits; the installation snapshots (format v7) are asserted by InstallationBackupGitTests.</summary>
        public async Task<List<string>> LogAsync()
            => (await AllCommitsAsync()).Where(x => !x.EndsWith("|chore: snapshot installation", StringComparison.Ordinal)).ToList();

        public async Task<List<string>> AllCommitsAsync()
            => (await RemoteAsync("log", "--reverse", "--format=%an|%ae|%s", "main")).Split('\n').ToList();

        /// <summary>The readable tree and the 2.x page documents, without the format v7 files.</summary>
        public async Task<List<string>> FilesAsync()
            => (await AllFilesAsync()).Where(x => !GitBackupLayout.IsInstallationData(x)).ToList();

        public async Task<List<string>> AllFilesAsync()
            => (await RemoteAsync("ls-tree", "-r", "--name-only", "main")).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

        public async ValueTask DisposeAsync()
        {
            await Pages.DisposeAsync();
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
