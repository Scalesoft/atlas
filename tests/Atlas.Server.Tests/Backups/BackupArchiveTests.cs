using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Server.Features.SpaceCovers.Domain;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.SpaceCovers;
using Xunit;

namespace Atlas.Server.Tests.Backups;

public sealed class BackupArchiveTests
{
    [PostgreSqlFact]
    public async Task Template_flag_changes_token_and_roundtrips_with_page_content()
    {
        foreach (var mode in new[] { "full", "latest" })
        {
        await using var fixture = await BackupFixture.CreateAsync();
        var before = await fixture.Archive.GetContentTokenAsync(mode, default);
        await fixture.Host.SeedAsync(async session =>
        {
            var page = await session.GetAsync<Page>(fixture.PageId);
            page.IsTemplate = true;
        });
        Assert.NotEqual(before, await fixture.Archive.GetContentTokenAsync(mode, default));
        using var archive = await fixture.Archive.CreateAsync(mode, default);
        using (var zip = ZipFile.OpenRead(archive.Path))
        using (var json = System.Text.Json.JsonDocument.Parse(zip.GetEntry($"pages/{fixture.PageId}/page.json")!.Open()))
        {
            Assert.Equal(3, System.Text.Json.JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()).RootElement.GetProperty("version").GetInt32());
            Assert.True(json.RootElement.GetProperty("isTemplate").GetBoolean());
        }
        await using var input = File.OpenRead(archive.Path);
        await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        using var check = fixture.Host.OpenSession();
        var added = Assert.Single(await check.QueryOver<Page>().Where(x => x.IsTemplate && x.Id != fixture.PageId).ListAsync());
        var revision = await check.QueryOver<PageRevision>().Where(x => x.PageId == added.Id && x.ActiveTo == null).SingleOrDefaultAsync();
        Assert.Contains("/api/attachments/", revision.ContentJson);
        Assert.StartsWith("Current-restore-", revision.Title);
        }
    }

    [PostgreSqlFact]
    public async Task Portable_copy_links_remove_origin_and_preserve_heading_without_changing_native_content()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var markdown = $"[Self](https://wiki.example/#/pages/{fixture.PageId}?heading=Overview) [File](https://wiki.example/api/attachments/{fixture.AttachmentId}/download)";
        var content = BackupFixture.Rich("{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Self\",\"marks\":[{\"type\":\"link\",\"attrs\":{\"href\":\"#/pages/" + fixture.PageId.ToString().ToUpperInvariant() + "\"}}]}]}");
        await fixture.Host.SeedAsync(async session =>
        {
            var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == fixture.PageId && x.ActiveTo == null).SingleOrDefaultAsync();
            current.Markdown = markdown;
            current.ContentJson = content;
        });
        using var archive = await fixture.Archive.CreateAsync("full", default);
        using (var zip = ZipFile.OpenRead(archive.Path))
        {
            using var reader = new StreamReader(zip.GetEntry($"pages/{fixture.PageId}/content.md")!.Open());
            var portable = await reader.ReadToEndAsync();
            Assert.Contains($"../../pages/{fixture.PageId}/content.md#Overview", portable);
            Assert.DoesNotContain("https://wiki.example", portable);
        }
        await using var input = File.OpenRead(archive.Path);
        await fixture.Import.ImportAsync(input, "restore", fixture.AdminId, default);
        using var check = fixture.Host.OpenSession();
        var restored = await check.QueryOver<PageRevision>().Where(x => x.PageId == fixture.PageId && x.ActiveTo == null).SingleOrDefaultAsync();
        Assert.Equal(content, restored.ContentJson);
    }

    [PostgreSqlFact]
    public async Task Full_archive_roundtrips_history_publication_trash_and_binary()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await using var input = File.OpenRead(archive.Path);
        using (var zip = new ZipArchive(input, ZipArchiveMode.Read, true))
        {
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.NotNull(zip.GetEntry("README.md"));
            Assert.NotNull(zip.GetEntry($"pages/{fixture.PageId}/history/1/content.json"));
            Assert.NotNull(zip.GetEntry($"pages/{fixture.TrashedPageId}/page.json"));
            // Format 1 page members only: 1.0 readers reject unknown members, so item access (OwnAccess) is not archived.
            using (var pageJson = System.Text.Json.JsonDocument.Parse(zip.GetEntry($"pages/{fixture.PageId}/page.json")!.Open()))
            {
                Assert.Equal(
                    ["id", "spaceId", "parentId", "ownerId", "version", "publishedRevisionId", "sortOrder", "isTrashed", "trashedAt", "trashedById", "trashBatchId"],
                    pageJson.RootElement.GetProperty("page").EnumerateObject().Select(x => x.Name).ToArray());
            }
            using var markdown = new StreamReader(zip.GetEntry($"pages/{fixture.PageId}/content.md")!.Open());
            Assert.Contains($"../../attachments/{fixture.AttachmentId}/manual.txt", await markdown.ReadToEndAsync());
        }
        input.Position = 0;
        var preview = await fixture.Import.PreviewAsync(input, "add", default);
        Assert.Equal(2, preview.Pages);
        Assert.Equal(1, preview.Attachments);
        input.Position = 0;
        var result = await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        Assert.Equal(2, result.Pages);
        using var session = fixture.Host.OpenSession();
        var pages = await session.QueryOver<Page>().ListAsync();
        var added = Assert.Single(pages, x => x.Id != fixture.PageId && !x.IsTrashed);
        var revisions = await session.QueryOver<PageRevision>().Where(x => x.PageId == added.Id).ListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.All(revisions, x => Assert.Contains("-restore-", x.Title));
        Assert.NotNull(added.PublishedRevisionId);
        Assert.DoesNotContain(fixture.AttachmentId.ToString(), revisions.Single(x => x.ActiveTo == null).ContentJson);
        var attachment = await session.QueryOver<Atlas.Server.Features.Attachments.Domain.Attachment>().Where(x => x.PageId == added.Id).SingleOrDefaultAsync();
        Assert.Equal("original bytes", await File.ReadAllTextAsync(fixture.Files.PathFor(attachment.Id)));
    }

    [PostgreSqlFact]
    public async Task Change_summaries_round_trip_and_raise_the_format_only_when_present()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        static int FormatOf(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            return System.Text.Json.JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()).RootElement.GetProperty("version").GetInt32();
        }
        // Without a summary the archive keeps the format it had before (here 1, or 3 for a template page), so 1.5 can still read it.
        using (var plain = await fixture.Archive.CreateAsync("full", default))
        {
            Assert.Equal(1, FormatOf(plain.Path));
            using var zip = ZipFile.OpenRead(plain.Path);
            Assert.DoesNotContain("changeSummaries", await new StreamReader(zip.GetEntry($"pages/{fixture.PageId}/page.json")!.Open()).ReadToEndAsync());
        }
        await fixture.Host.SeedAsync(async session => (await session.GetAsync<Page>(fixture.PageId)).IsTemplate = true);
        using (var template = await fixture.Archive.CreateAsync("full", default))
        {
            Assert.Equal(3, FormatOf(template.Path));
            await using var input = File.OpenRead(template.Path);
            await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        }
        using (var check = fixture.Host.OpenSession())
        {
            Assert.All(await check.QueryOver<PagePublication>().ListAsync(), x => Assert.Null(x.ChangeSummary));
        }

        await fixture.Host.ExecuteAsync("UPDATE \"PagePublication\" SET \"ChangeSummary\"='Doplněn postup' WHERE \"PageId\"=:id", fixture.PageId);
        foreach (var mode in new[] { "full", "latest" })
        {
            using var archive = await fixture.Archive.CreateAsync(mode, default);
            Assert.Equal(4, FormatOf(archive.Path));
            using (var zip = ZipFile.OpenRead(archive.Path))
            using (var json = System.Text.Json.JsonDocument.Parse(zip.GetEntry($"pages/{fixture.PageId}/page.json")!.Open()))
            {
                var publication = Assert.Single(json.RootElement.GetProperty("publications").EnumerateArray());
                // The entity itself stays free of the new member: 1.5 readers reject unknown members.
                Assert.False(publication.TryGetProperty("changeSummary", out _));
                var summaries = json.RootElement.GetProperty("changeSummaries");
                Assert.Equal("Doplněn postup", summaries.GetProperty(publication.GetProperty("id").GetString()!).GetString());
            }
            if (mode == "latest")
            {
                continue;
            }
            await using var input = File.OpenRead(archive.Path);
            await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        }
        using var read = fixture.Host.OpenSession();
        var restored = await read.QueryOver<PagePublication>().Where(x => x.PageId != fixture.PageId && x.ChangeSummary != null).ListAsync();
        Assert.Single(restored);
        Assert.All(restored, x => Assert.Equal("Doplněn postup", x.ChangeSummary));
    }

    [PostgreSqlFact]
    public async Task Space_cover_changes_the_token_travels_in_format_5_and_a_restore_makes_the_archived_image_current()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        Guid spaceId;
        using (var session = fixture.Host.OpenSession())
        {
            spaceId = (await session.GetAsync<Page>(fixture.PageId)).SpaceId;
        }
        var before = await fixture.Archive.GetContentTokenAsync("latest", default);
        var png = await SeedCoverAsync(fixture, spaceId, SpaceCoverHandlerTests.Png, "image/png", false);
        Assert.NotEqual(before, await fixture.Archive.GetContentTokenAsync("latest", default));
        using var archive = await fixture.Archive.CreateAsync("latest", default);
        using (var zip = ZipFile.OpenRead(archive.Path))
        {
            Assert.Equal(5, System.Text.Json.JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()).RootElement.GetProperty("version").GetInt32());
            using var cover = System.Text.Json.JsonDocument.Parse(zip.GetEntry($"spaces/{spaceId}/cover.json")!.Open());
            Assert.False(cover.RootElement.GetProperty("tinted").GetBoolean());
            using var bytes = new MemoryStream();
            await zip.GetEntry($"spaces/{spaceId}/cover.png")!.Open().CopyToAsync(bytes);
            Assert.Equal(SpaceCoverHandlerTests.Png, bytes.ToArray());
        }

        // A copy of the space gets its own copy of the image.
        await using (var input = File.OpenRead(archive.Path))
        {
            await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        }
        // Restoring over a newer image makes the archived one current as the restorer's change and deletes the replaced file.
        var jpeg = await SeedCoverAsync(fixture, spaceId, SpaceCoverHandlerTests.Jpeg, "image/jpeg", true);
        await using (var input = File.OpenRead(archive.Path))
        {
            await fixture.Import.ImportAsync(input, "restore", fixture.AdminId, default);
        }
        using var check = fixture.Host.OpenSession();
        var current = await check.QueryOver<SpaceCover>().Where(x => x.ActiveTo == null).ListAsync();
        Assert.Equal(2, current.Count);
        Assert.All(current, x => Assert.Equal((png.Sha256, false, "image/png"), (x.Sha256, x.Tinted, x.MimeType)));
        Assert.Equal(2, current.Select(x => x.FileId).Distinct().Count());
        Assert.All(current, x => Assert.True(File.Exists(fixture.Files.PathFor(x.FileId!.Value))));
        Assert.Equal(fixture.AdminId, current.Single(x => x.SpaceId == spaceId).ChangedBy);
        Assert.False(File.Exists(fixture.Files.PathFor(jpeg.FileId!.Value)));
    }

    [PostgreSqlFact]
    public async Task A_chosen_drawing_travels_as_format_6_and_an_unknown_one_restores_as_automatic()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        Guid spaceId;
        using (var session = fixture.Host.OpenSession())
        {
            spaceId = (await session.GetAsync<Page>(fixture.PageId)).SpaceId;
        }
        Task ChooseAsync(SpaceCoverSource source, string? catalogKey) =>
            fixture.Host.SeedAsync(session => SpaceCoverHistory.ChooseAsync(session, spaceId, source, catalogKey, null, default));
        async Task<SpaceCover> RestoreAsync(BackupArchiveFile file)
        {
            await using (var input = File.OpenRead(file.Path))
            {
                await fixture.Import.ImportAsync(input, "restore", fixture.AdminId, default);
            }
            using var check = fixture.Host.OpenSession();
            return (await SpaceCoverHistory.CurrentAsync(check, spaceId, default))!;
        }

        var before = await fixture.Archive.GetContentTokenAsync("latest", default);
        await ChooseAsync(SpaceCoverSource.Catalog, "it-backup");
        Assert.NotEqual(before, await fixture.Archive.GetContentTokenAsync("latest", default));
        using var archive = await fixture.Archive.CreateAsync("latest", default);
        using (var zip = ZipFile.OpenRead(archive.Path))
        {
            Assert.Equal(6, System.Text.Json.JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()).RootElement.GetProperty("version").GetInt32());
            using var cover = new StreamReader(zip.GetEntry($"spaces/{spaceId}/cover.json")!.Open());
            Assert.Equal("""{"source":"Catalog","catalogKey":"it-backup"}""", string.Concat((await cover.ReadToEndAsync()).Where(c => !char.IsWhiteSpace(c))));
            // A drawing is shipped with the application: no image travels with the choice.
            Assert.Equal([$"spaces/{spaceId}/cover.json", $"spaces/{spaceId}/space.json"],
                zip.Entries.Select(x => x.FullName).Where(x => x.StartsWith($"spaces/{spaceId}/", StringComparison.Ordinal)).Order());
        }

        // Restoring over a later choice makes the archived one current as the restorer's change.
        await ChooseAsync(SpaceCoverSource.None, null);
        var restored = await RestoreAsync(archive);
        Assert.Equal((SpaceCoverSource.Catalog, "it-backup", fixture.AdminId), (restored.Source, restored.CatalogKey, restored.ChangedBy));

        // An archive of a later release with more drawings: the database accepts any well-formed key, this catalog does not know it.
        await ChooseAsync(SpaceCoverSource.Catalog, "drawing-of-a-later-release");
        using var later = await fixture.Archive.CreateAsync("latest", default);
        var automatic = await RestoreAsync(later);
        Assert.Equal((SpaceCoverSource.Auto, null, fixture.AdminId), (automatic.Source, automatic.CatalogKey, automatic.ChangedBy));
    }

    /// <summary>The reader refuses what a writer never writes, before anything is restored: an image choice (a member <c>source</c>)
    /// in an archive older than format 6, a drawing without its key, and a space description beyond the 2000 characters a space
    /// allows (it feeds the automatic pick of the image and the memo key of the matcher).</summary>
    [PostgreSqlFact]
    public async Task The_reader_refuses_a_malformed_image_choice_and_an_oversized_space_description()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        Guid spaceId;
        using (var session = fixture.Host.OpenSession())
        {
            spaceId = (await session.GetAsync<Page>(fixture.PageId)).SpaceId;
        }
        await fixture.Host.SeedAsync(session => SpaceCoverHistory.ChooseAsync(session, spaceId, SpaceCoverSource.Catalog, "it-backup", null, default));
        async Task<string> RefusalAsync(Func<string, Task> tamper)
        {
            using var archive = await fixture.Archive.CreateAsync("latest", default);
            await tamper(archive.Path);
            await using var input = File.OpenRead(archive.Path);
            return (await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Import.PreviewAsync(input, "restore", default))).Message;
        }

        using (var intact = await fixture.Archive.CreateAsync("latest", default))
        await using (var input = File.OpenRead(intact.Path))
        {
            Assert.Equal(1, (await fixture.Import.PreviewAsync(input, "restore", default)).Pages);
        }
        // The manifest says 5, which has no image choices.
        Assert.Equal("Neplatná volba obrázku prostoru.", await RefusalAsync(async path =>
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            var manifest = zip.GetEntry("manifest.json")!;
            System.Text.Json.Nodes.JsonNode node;
            using (var reader = new StreamReader(manifest.Open()))
            {
                node = System.Text.Json.Nodes.JsonNode.Parse(await reader.ReadToEndAsync())!;
            }
            node["version"] = 5;
            manifest.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            await writer.WriteAsync(node.ToJsonString());
        }));
        // A catalog choice without a key.
        Assert.Equal("Neplatná volba obrázku prostoru.", await RefusalAsync(path =>
            BackupImportTests.RewriteMetadataAsync(path, $"spaces/{spaceId:D}/cover.json", node => node.AsObject().Remove("catalogKey"))));
        // One character more than a space description can have.
        Assert.Equal("Neplatná metadata prostoru.", await RefusalAsync(path =>
            BackupImportTests.RewriteMetadataAsync(path, $"spaces/{spaceId:D}/space.json", node => node["description"] = new string('x', 2001))));
    }

    /// <summary>Makes an image current for the space directly in the store, closing the current one; returns the new row.</summary>
    private static async Task<SpaceCover> SeedCoverAsync(BackupFixture fixture, Guid spaceId, byte[] image, string mimeType, bool tinted)
    {
        var fileId = Guid.NewGuid();
        using var bytes = new MemoryStream(image);
        var written = await fixture.Files.WriteAsync(fileId, bytes, true, default);
        var cover = new SpaceCover
        {
            Id = Guid.NewGuid(), SpaceId = spaceId, Source = SpaceCoverSource.Upload, FileId = fileId, MimeType = mimeType, Size = written.Size,
            Sha256 = written.Hash, Tinted = tinted
        };
        await fixture.Host.SeedAsync(async session => await SpaceCoverHistory.AdvanceAsync(session,
            await SpaceCoverHistory.CurrentAsync(session, spaceId, default), cover, default));
        return cover;
    }

    /// <summary>Format 7: a full archive carries a trashed space with trash.json and a restore that creates it puts it back in the
    /// trash with its pages (one batch); a latest archive leaves it out like trashed pages; restoring over the trashed space keeps it trashed.</summary>
    [PostgreSqlFact]
    public async Task A_trashed_space_travels_in_full_archives_as_format_7_and_returns_to_the_trash()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        Guid spaceId;
        using (var session = fixture.Host.OpenSession())
        {
            spaceId = (await session.GetAsync<Page>(fixture.PageId)).SpaceId;
        }
        var batch = Guid.NewGuid();
        await fixture.Host.ExecuteAsync($"""
            UPDATE "Page" SET "IsTrashed"=true, "TrashedAt"=now(), "TrashedById"='{fixture.AdminId}', "TrashBatchId"='{batch}' WHERE "SpaceId"=:id AND NOT "IsTrashed";
            UPDATE "Space" SET "TrashedAt"=now(), "TrashedById"='{fixture.AdminId}', "TrashBatchId"='{batch}' WHERE "Id"=:id;
            """, spaceId);

        using (var latest = await fixture.Archive.CreateAsync("latest", default))
        using (var zip = ZipFile.OpenRead(latest.Path))
        {
            Assert.DoesNotContain(zip.Entries, x => x.FullName.StartsWith($"spaces/{spaceId}/", StringComparison.Ordinal));
        }
        using var full = await fixture.Archive.CreateAsync("full", default);
        using (var zip = ZipFile.OpenRead(full.Path))
        {
            Assert.Equal(7, System.Text.Json.JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()).RootElement.GetProperty("version").GetInt32());
            var trash = System.Text.Json.JsonDocument.Parse(zip.GetEntry($"spaces/{spaceId}/trash.json")!.Open()).RootElement;
            Assert.Equal(batch, trash.GetProperty("trashBatchId").GetGuid());
        }

        await using (var input = File.OpenRead(full.Path))
        {
            await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        }
        using (var check = fixture.Host.OpenSession())
        {
            var copy = Assert.Single(await check.QueryOver<Space>().Where(x => x.Id != spaceId && x.TrashedAt != null).ListAsync());
            Assert.NotEqual(batch, copy.TrashBatchId);
            var copyId = copy.Id;
            var pages = await check.QueryOver<Page>().Where(x => x.SpaceId == copyId && x.TrashBatchId == copy.TrashBatchId).RowCountAsync();
            Assert.True(pages > 0);
            Assert.Equal(0, await check.QueryOver<Page>().Where(x => x.SpaceId == copyId && !x.IsTrashed).RowCountAsync());
        }
        await using (var input = File.OpenRead(full.Path))
        {
            await fixture.Import.ImportAsync(input, "restore", fixture.AdminId, default);
        }
        using (var check = fixture.Host.OpenSession())
        {
            Assert.Equal(batch, (await check.GetAsync<Space>(spaceId)).TrashBatchId);
        }
    }

    /// <summary>A purged page and a purged space (tombstones of the trash, ARCH1-1) are in no archive, so no restore brings them back.</summary>
    [PostgreSqlFact]
    public async Task A_full_archive_leaves_out_what_was_permanently_deleted()
    {
        await using var fixture = await BackupFixture.CreateAsync(sharedAs: nameof(BackupArchiveTests));
        var space = Guid.NewGuid();
        await fixture.Host.ExecuteAsync($"""
            UPDATE "Page" SET "PurgedAt"=now() WHERE "Id"=:id;
            INSERT INTO "Space"("Id","Name","Icon","Color","TrashedAt","TrashedById","TrashBatchId","PurgedAt")
              VALUES('{space}','Gone','book-open','blue',now(),'{fixture.AdminId}',gen_random_uuid(),now());
            """, fixture.TrashedPageId);
        using var archive = await fixture.Archive.CreateAsync("full", default);
        using var zip = ZipFile.OpenRead(archive.Path);
        Assert.DoesNotContain(zip.Entries, x => x.FullName.StartsWith($"pages/{fixture.TrashedPageId}/", StringComparison.Ordinal));
        Assert.DoesNotContain(zip.Entries, x => x.FullName.StartsWith($"spaces/{space}/", StringComparison.Ordinal));
        Assert.NotNull(zip.GetEntry($"pages/{fixture.PageId}/page.json"));
    }

    [PostgreSqlFact]
    public async Task Latest_keeps_published_and_current_but_not_trash_and_token_ignores_placement()
    {
        await using var fixture = await BackupFixture.CreateAsync(sharedAs: nameof(BackupArchiveTests));
        var token = await fixture.Archive.GetContentTokenAsync("latest", default);
        await fixture.Host.ExecuteAsync("UPDATE \"Page\" SET \"SortOrder\"=99 WHERE \"Id\"=:id", fixture.PageId);
        Assert.Equal(token, await fixture.Archive.GetContentTokenAsync("latest", default));
        using var archive = await fixture.Archive.CreateAsync("latest", default);
        using var zip = ZipFile.OpenRead(archive.Path);
        Assert.Null(zip.GetEntry($"pages/{fixture.TrashedPageId}/page.json"));
        Assert.NotNull(zip.GetEntry($"pages/{fixture.PageId}/history/1/content.json"));
        Assert.NotNull(zip.GetEntry($"pages/{fixture.PageId}/history/2/content.json"));
    }
}

internal sealed class BackupFixture : IAsyncDisposable
{
    private string? m_directory;
    public AtlasTestHost Host { get; private set; } = null!;
    public AttachmentFiles Files => Host.Services.GetRequiredService<AttachmentFiles>();
    public WikiBackupArchive Archive => new(Host.Database, Files);
    public WikiBackupImport Import => new(Host.Database, Files, Host.Services.GetRequiredService<IServiceScopeFactory>());
    public Guid AdminId { get; private set; }
    public Guid PageId { get; } = Guid.NewGuid();
    public Guid TrashedPageId { get; } = Guid.NewGuid();
    public Guid AttachmentId { get; } = Guid.NewGuid();

    /// <param name="outbox">A host whose outbox delivers (AtlasTestHost.CreateAsync).</param>
    /// <param name="sharedAs">The test class whose tests share one host (nameof): they run one at a time, so its whole-wiki archives,
    /// tokens and imports see only that class's data, each test keeping to its own ids. Without it the host is the test's own.</param>
    public static async Task<BackupFixture> CreateAsync(bool outbox = false, string? sharedAs = null)
    {
        var fixture = new BackupFixture();
        var directory = Path.Combine(Path.GetTempPath(), "atlas_backup_test_" + Guid.NewGuid().ToString("N"));
        // Own host unless a class shares one: an archive holds every space and page of the installation, and an import or a restore
        // takes the installation's exclusive content lock and writes the whole archive back into it.
        fixture.Host = await AtlasTestHost.CreateAsync("atlas_backup_", services =>
        {
            Directory.CreateDirectory(directory);
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
            services.AddSingleton(new AttachmentFiles(configuration, new AttachmentTests.EnvironmentStub(directory)));
        }, outbox: outbox, sharedAs: sharedAs is null ? null : "backup-fixture:" + sharedAs);
        // The folder of a shared host lives as long as the host, that is the test process.
        fixture.m_directory = sharedAs is null ? directory : null;
        fixture.AdminId = (await fixture.Host.SeedUserAsync("backupadmin", true)).Id;
        var space = await fixture.Host.SeedSpaceAsync("Original");
        using var bytes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("original bytes"));
        var file = await fixture.Files.WriteAsync(fixture.AttachmentId, bytes, false, default);
        var first = Guid.NewGuid();
        var current = Guid.NewGuid();
        var boundary = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var content = Rich("{\"type\":\"image\",\"attrs\":{\"src\":\"/api/attachments/" + fixture.AttachmentId + "/content\"}}");
        var published = Rich(Paragraph("published"));
        var trash = Rich(Paragraph("trash"));
        await fixture.Host.SeedAsync(async session =>
        {
            await session.SaveAsync(new Page { Id = fixture.PageId, SpaceId = space, OwnerId = fixture.AdminId, Version = 2 });
            await session.SaveAsync(new Page { Id = fixture.TrashedPageId, SpaceId = space, OwnerId = fixture.AdminId, IsTrashed = true, TrashedAt = boundary, TrashedById = fixture.AdminId, TrashBatchId = Guid.NewGuid() });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevision { Channel = ContentChannels.Web, Id = first, PageId = fixture.PageId, Version = 1, Title = "Published", ContentJson = published, Markdown = Markdown(published), AuthorId = fixture.AdminId, ActiveFrom = boundary.AddDays(-1), ActiveTo = boundary });
            await session.SaveAsync(new PageRevision { Channel = ContentChannels.Web, Id = current, PageId = fixture.PageId, Version = 2, Title = "Current", ContentJson = content, Markdown = Markdown(content), AuthorId = fixture.AdminId, ActiveFrom = boundary });
            await session.SaveAsync(new PageRevision { Channel = ContentChannels.Web, Id = Guid.NewGuid(), PageId = fixture.TrashedPageId, Version = 1, Title = "Trash", ContentJson = trash, Markdown = Markdown(trash), AuthorId = fixture.AdminId, ActiveFrom = boundary });
            await session.SaveAsync(new Atlas.Server.Features.Attachments.Domain.Attachment { Id = fixture.AttachmentId, PageId = fixture.PageId, UploadedBy = fixture.AdminId, Name = "manual.txt", Size = file.Size, Sha256 = file.Hash, ActiveFrom = boundary });
            await session.FlushAsync();
            await session.SaveAsync(new Atlas.Server.Features.Attachments.Domain.PageRevisionAttachment { PageId = fixture.PageId, RevisionId = current, AttachmentId = fixture.AttachmentId });
            await session.SaveAsync(new PagePublication { Channel = ContentChannels.Web, Id = Guid.NewGuid(), PageId = fixture.PageId, RevisionId = first, PublisherId = fixture.AdminId, ActiveFrom = boundary });
            var page = await session.GetAsync<Page>(fixture.PageId);
            page.PublishedRevisionId = first;
        });
        return fixture;
    }

    /// <summary>A rich envelope of <paramref name="blocks"/> (JSON) that every write accepts; <paramref name="plugins"/> is its plugins member with a trailing comma.</summary>
    public static string Rich(string blocks, string plugins = "") =>
        "{\"format\":\"atlas-rich\",\"version\":1," + plugins + "\"document\":{\"type\":\"doc\",\"content\":[" + blocks + "]}}";

    public static string Paragraph(string text) => "{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}";

    /// <summary>The Markdown the server stores for <paramref name="json"/>.</summary>
    public static string Markdown(string json) => Rules.ValidateDocument(json).Markdown;

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        if (m_directory is not null)
            Directory.Delete(m_directory, true);
    }
}
