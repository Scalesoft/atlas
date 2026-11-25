using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Server.Platform.Events;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Backups;

public sealed class BackupImportTests
{
    [PostgreSqlFact]
    public async Task Preview_rejects_attachment_size_that_disagrees_with_checksummed_payload()
    {
        await using var fixture = await BackupFixture.CreateAsync(sharedAs: nameof(BackupImportTests));
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await RewriteMetadataAsync(archive.Path, $"attachments/{fixture.AttachmentId}/attachment.json",
            node => node["attachment"]!["size"] = 15);
        await using var input = File.OpenRead(archive.Path);
        var importer = new WikiBackupImport(null!, null!, null!);
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.PreviewAsync(input, "restore", default));
    }

    [PostgreSqlFact]
    public async Task Preview_rejects_image_metadata_that_does_not_match_validated_binary()
    {
        await using var fixture = await BackupFixture.CreateAsync(sharedAs: nameof(BackupImportTests));
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await RewriteMetadataAsync(archive.Path, $"attachments/{fixture.AttachmentId}/attachment.json", node =>
        {
            node["attachment"]!["isImage"] = true;
            node["attachment"]!["mimeType"] = "image/png";
        });
        await using var input = File.OpenRead(archive.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Import.PreviewAsync(input, "add", default));
    }

    [PostgreSqlFact]
    public async Task Add_rewrites_escaped_rich_document_attachment_urls()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        await fixture.Host.SeedAsync(async session =>
        {
            var current = await session.QueryOver<PageRevision>().Where(x => x.PageId == fixture.PageId && x.ActiveTo == null).SingleOrDefaultAsync();
            current.ContentJson = current.ContentJson.Replace("/", "\\/");
        });
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await using var input = File.OpenRead(archive.Path);
        await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        using var check = fixture.Host.OpenSession();
        var copies = await check.QueryOver<Page>().Where(x => !x.IsTrashed && x.Id != fixture.PageId).ListAsync();
        var copy = Assert.Single(copies);
        var currentCopy = await check.QueryOver<PageRevision>().Where(x => x.PageId == copy.Id && x.ActiveTo == null).SingleOrDefaultAsync();
        var reference = Assert.Single(Atlas.Server.Features.Attachments.Domain.AttachmentReferences.Extract(currentCopy.ContentJson));
        Assert.NotEqual(fixture.AttachmentId, reference);
        var attachment = await check.GetAsync<Atlas.Server.Features.Attachments.Domain.Attachment>(reference);
        Assert.Equal(copy.Id, attachment.PageId);
    }

    [PostgreSqlFact]
    public async Task Restored_pages_project_their_links()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var linkingId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        await fixture.Host.SeedAsync(async session =>
        {
            var space = (await session.GetAsync<Page>(fixture.PageId)).SpaceId;
            await session.SaveAsync(new Page { Id = linkingId, SpaceId = space, OwnerId = fixture.AdminId });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevision { Channel = ContentChannels.Web, Id = revisionId, PageId = linkingId, Version = 1, Title = "Linking", Markdown = "linking",
                ContentJson = BackupFixture.Rich("{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"see\",\"marks\":[{\"type\":\"link\",\"attrs\":{\"href\":\"#/pages/" + fixture.PageId + "\"}}]}]}"),
                AuthorId = fixture.AdminId, ActiveFrom = DateTime.UtcNow });
            await session.SaveAsync(new PagePublication { Channel = ContentChannels.Web, Id = Guid.NewGuid(), PageId = linkingId, RevisionId = revisionId, PublisherId = fixture.AdminId, ActiveFrom = DateTime.UtcNow });
            (await session.GetAsync<Page>(linkingId)).PublishedRevisionId = revisionId;
        });
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await using var input = File.OpenRead(archive.Path);

        // Add mode gives every page a new identity and rewrites internal links to it; the import announces each page's rebuild, whose
        // delivery projects the links (this host has no queue: the test derives them).
        await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        await fixture.Host.RefreshPageDerivedAsync();

        using var check = fixture.Host.OpenSession();
        var rows = await check.CreateSQLQuery("""
            SELECT l."SourcePageId",l."TargetPageId",l."SourceRevisionId"=p."PublishedRevisionId" FROM "PageLink" l
            JOIN "Page" p ON p."Id"=l."SourcePageId" WHERE l."SourcePageId"<>:original
            """).SetParameter("original", linkingId).ListAsync<object[]>();
        var row = Assert.Single(rows);
        Assert.NotEqual(linkingId, (Guid)row[0]);
        Assert.NotEqual(fixture.PageId, (Guid)row[1]);
        Assert.True((bool)row[2]);
        Assert.StartsWith("Current", await check.CreateSQLQuery("""SELECT "Title" FROM "PageRevision" WHERE "PageId"=:copy AND "ActiveTo" IS NULL""")
            .SetParameter("copy", (Guid)row[1]).UniqueResultAsync<string>());
    }

    [PostgreSqlFact]
    public async Task Add_remaps_page_reference_nodes_to_copied_pages_and_leaves_outside_targets()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var linkingId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var outside = Guid.NewGuid();
        await fixture.Host.SeedAsync(async session =>
        {
            var space = (await session.GetAsync<Page>(fixture.PageId)).SpaceId;
            await session.SaveAsync(new Page { Id = linkingId, SpaceId = space, OwnerId = fixture.AdminId });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevision { Channel = ContentChannels.Web, Id = revisionId, PageId = linkingId, Version = 1, Title = "Linking", Markdown = "linking",
                ContentJson = BackupFixture.Rich("{\"type\":\"paragraph\",\"content\":["
                    + "{\"type\":\"pageReference\",\"attrs\":{\"id\":\"" + fixture.PageId + "\",\"title\":\"Current\",\"url\":\"#/pages/" + fixture.PageId + "\"}},"
                    + "{\"type\":\"pageReference\",\"attrs\":{\"id\":\"" + outside + "\",\"title\":\"Outside\",\"url\":\"#/pages/" + outside + "\"}}]}", "\"plugins\":[{\"id\":\"pages\",\"version\":1}],"),
                AuthorId = fixture.AdminId, ActiveFrom = DateTime.UtcNow });
        });
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await using var input = File.OpenRead(archive.Path);

        await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);

        using var check = fixture.Host.OpenSession();
        var copiedTarget = await check.CreateSQLQuery("""SELECT "PageId" FROM "PageRevision" WHERE "Title" LIKE 'Current%' AND "PageId"<>:original AND "ActiveTo" IS NULL""")
            .SetParameter("original", fixture.PageId).UniqueResultAsync<Guid>();
        var content = await check.CreateSQLQuery("""SELECT "ContentJson" FROM "PageRevision" WHERE "Title" LIKE 'Linking%' AND "PageId"<>:original AND "ActiveTo" IS NULL""")
            .SetParameter("original", linkingId).UniqueResultAsync<string>();
        var references = System.Text.Json.Nodes.JsonNode.Parse(content)!["document"]!["content"]![0]!["content"]!.AsArray().Select(x => x!["attrs"]!).ToList();
        Assert.Equal(copiedTarget.ToString(), (string)references[0]["id"]!);
        Assert.Equal("#/pages/" + copiedTarget, (string)references[0]["url"]!);
        Assert.Equal(outside.ToString(), (string)references[1]["id"]!);
        Assert.Equal("#/pages/" + outside, (string)references[1]["url"]!);
    }

    [PostgreSqlFact]
    public async Task Add_remaps_cross_page_requirements_to_the_copies_and_leaves_prose_as_written()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var dependentId = Guid.NewGuid();
        var prose = $"https://wiki.example.test/#/pages/{fixture.PageId}";
        await fixture.Host.SeedAsync(async session =>
        {
            var space = (await session.GetAsync<Page>(fixture.PageId)).SpaceId;
            await session.SaveAsync(new Page { Id = dependentId, SpaceId = space, OwnerId = fixture.AdminId });
            await session.FlushAsync();
            var content = BackupFixture.Rich(BackupFixture.Paragraph(prose) + ",{\"type\":\"requirement\",\"attrs\":{\"id\":\"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f7\",\"key\":\"firewall\","
                + "\"direction\":\"internal\",\"state\":\"planned\",\"date\":\"\",\"party\":\"\",\"where\":\"\",\"requires\":\"dns,vpn@" + fixture.PageId + "\"},"
                + "\"content\":[" + BackupFixture.Paragraph("Firewall") + "]}", "\"plugins\":[{\"id\":\"atlas-requirements\",\"version\":1}],");
            await session.SaveAsync(new PageRevision { Channel = ContentChannels.Web, Id = Guid.NewGuid(), PageId = dependentId, Version = 1, Title = "Dependent",
                Markdown = BackupFixture.Markdown(content), ContentJson = content, AuthorId = fixture.AdminId, ActiveFrom = DateTime.UtcNow });
        });
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await using var input = File.OpenRead(archive.Path);

        await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);

        using var check = fixture.Host.OpenSession();
        var copiedTarget = await check.CreateSQLQuery("""SELECT "PageId" FROM "PageRevision" WHERE "Title" LIKE 'Current%' AND "PageId"<>:original AND "ActiveTo" IS NULL""")
            .SetParameter("original", fixture.PageId).UniqueResultAsync<Guid>();
        var copied = System.Text.Json.Nodes.JsonNode.Parse(await check.CreateSQLQuery("""SELECT "ContentJson" FROM "PageRevision" WHERE "Title" LIKE 'Dependent%' AND "PageId"<>:original AND "ActiveTo" IS NULL""")
            .SetParameter("original", dependentId).UniqueResultAsync<string>())!["document"]!["content"]!;
        // GAP2-5: the dependency on another page's block follows the copy of that page; GAP2-8: the visible text keeps its address.
        Assert.Equal("dns,vpn@" + copiedTarget, (string)copied[1]!["attrs"]!["requires"]!);
        Assert.Equal(prose, (string)copied[0]!["content"]![0]!["text"]!);
    }

    /// <summary>Migration 2026110170 over a page an installation already has: a publication in a restore transaction is a rebuild
    /// without a revision id, any other stays a publication of its revision.</summary>
    [PostgreSqlFact]
    public async Task After_migration_2026110170_an_existing_page_published_in_a_restore_announces_a_rebuild()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_import_rebuild_");
        await Events.OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110170);
        const string Page = "00000000-0000-0000-0000-0000000000f1", Writer = "00000000-0000-0000-0000-000000000001";
        await Events.OutboxTests.ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Writer}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('00000000-0000-0000-0000-00000000000a','First','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version") VALUES('{Page}','00000000-0000-0000-0000-00000000000a','{Writer}',1);
            """);
        // Up to the migration that drops the trigger this one changes (2026110420): since then the import announces each page itself.
        await Events.OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110420, from: 2026110170);

        string Publish(int version, string id) => $$"""
            UPDATE "PageRevision" SET "ActiveTo"=now() WHERE "PageId"='{{Page}}' AND "ActiveTo" IS NULL;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel","AiAssisted")
              VALUES('{{id}}','{{Page}}',{{version}},'t','{"type":"doc","content":[]}','m','{{Writer}}',now(),'web',false);
            UPDATE "Page" SET "PublishedRevisionId"='{{id}}' WHERE "Id"='{{Page}}';
            """;
        await Events.OutboxTests.ExecuteAsync(schema.ConnectionString, Publish(2, "00000000-0000-0000-0000-0000000000e1"));
        await Events.OutboxTests.ExecuteAsync(schema.ConnectionString,
            "BEGIN; SELECT set_config('scaleatlas.restore', 'on', true); " + Publish(3, "00000000-0000-0000-0000-0000000000e2") + " COMMIT;");

        Assert.Equal("published:00000000-0000-0000-0000-0000000000e1,rebuild:-", await Events.OutboxTests.ScalarAsync(schema.ConnectionString, $"""
            SELECT string_agg(("Payload"->>'revisionKind') || ':' || coalesce("Payload"->>'revisionId', '-'), ',' ORDER BY "Id") FROM "OutboxMessage"
            WHERE "Payload"->>'resourceId'='{Page}' AND "Payload"->>'revisionKind' <> 'working'
            """));
    }

    [PostgreSqlFact]
    public async Task Add_records_the_mentions_of_the_copies_and_tells_nobody_again()
    {
        await using var fixture = await BackupFixture.CreateAsync(outbox: true);
        await Events.OutboxTests.DrainAsync(fixture.Host);
        var mentioned = await fixture.Host.SeedUserAsync("mentioned");
        var content = BackupFixture.Rich("{\"type\":\"paragraph\",\"content\":[{\"type\":\"contactInline\",\"attrs\":{\"data\":{\"version\":1,\"contacts\":[{\"name\":\"Mentioned\",\"kind\":\"internal\",\"internalUser\":{\"id\":\""
            + mentioned.Id + "\",\"name\":\"Mentioned\"}}]}}}]}", "\"plugins\":[{\"id\":\"contacts\",\"version\":2}],");
        await fixture.Host.SeedAsync(async session =>
        {
            var published = await session.QueryOver<PageRevision>().Where(x => x.PageId == fixture.PageId && x.Version == 1).SingleOrDefaultAsync();
            (published.ContentJson, published.Markdown) = (content, BackupFixture.Markdown(content));
        });
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await using var input = File.OpenRead(archive.Path);

        await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        await Events.OutboxTests.DrainAsync(fixture.Host);

        // GAP2-4: the copy's publication is the archive's, not the restorer's news: recorded as told, nobody is told again.
        using var check = fixture.Host.OpenSession();
        Assert.Equal(1L, await check.CreateSQLQuery("""SELECT count(*) FROM "PageMentionNotice" WHERE "PageId"<>:original AND "UserId"=:user""")
            .SetParameter("original", fixture.PageId).SetParameter("user", mentioned.Id).UniqueResultAsync<long>());
        Assert.Equal(0L, await check.CreateSQLQuery("""SELECT count(*) FROM "Notification" """).UniqueResultAsync<long>());
    }

    internal static async Task RewriteMetadataAsync(string archivePath, string path, Action<System.Text.Json.Nodes.JsonNode> rewrite)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = zip.GetEntry(path)!;
        System.Text.Json.Nodes.JsonNode node;
        using (var reader = new StreamReader(entry.Open()))
            node = System.Text.Json.Nodes.JsonNode.Parse(await reader.ReadToEndAsync())!;
        rewrite(node);
        var bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
        entry.Delete();
        using (var output = zip.CreateEntry(path).Open())
            output.Write(bytes);
        var manifest = zip.GetEntry("manifest.json")!;
        System.Text.Json.Nodes.JsonNode manifestNode;
        using (var reader = new StreamReader(manifest.Open()))
            manifestNode = System.Text.Json.Nodes.JsonNode.Parse(await reader.ReadToEndAsync())!;
        var file = manifestNode["files"]!.AsArray().Single(x => x!["path"]!.GetValue<string>() == path)!;
        file["size"] = bytes.Length;
        file["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        manifest.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
        await writer.WriteAsync(manifestNode.ToJsonString());
    }

    [PostgreSqlFact]
    public async Task Preview_rejects_null_manifest_entries_as_invalid_data()
    {
        await using var fixture = await BackupFixture.CreateAsync(sharedAs: nameof(BackupImportTests));
        using var archive = await fixture.Archive.CreateAsync("full", default);
        using (var zip = ZipFile.Open(archive.Path, ZipArchiveMode.Update))
        {
            var manifest = zip.GetEntry("manifest.json")!;
            System.Text.Json.Nodes.JsonNode node;
            using (var reader = new StreamReader(manifest.Open()))
            {
                node = System.Text.Json.Nodes.JsonNode.Parse(await reader.ReadToEndAsync())!;
            }
            node["files"]![0] = null;
            manifest.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            await writer.WriteAsync(node.ToJsonString());
        }
        await using var input = File.OpenRead(archive.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Import.PreviewAsync(input, "restore", default));
    }

    [PostgreSqlFact]
    public async Task Fresh_restore_preserves_native_content_history_and_ids_without_importing_accounts()
    {
        await using var source = await BackupFixture.CreateAsync();
        await using var target = await BackupFixture.CreateAsync(sharedAs: nameof(BackupImportTests));
        using var archive = await source.Archive.CreateAsync("full", default);
        await using var input = File.OpenRead(archive.Path);
        await target.Import.ImportAsync(input, "restore", target.AdminId, default);
        using var before = source.Host.OpenSession();
        using var after = target.Host.OpenSession();
        var originals = await before.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId).ListAsync();
        var restored = await after.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId).ListAsync();
        Assert.Equal(originals.OrderBy(x => x.Version).Select(x => (x.Id, x.Version, x.ContentJson, x.Markdown, x.ActiveFrom, x.ActiveTo)),
            restored.OrderBy(x => x.Version).Select(x => (x.Id, x.Version, x.ContentJson, x.Markdown, x.ActiveFrom, x.ActiveTo)));
        Assert.All(restored, x => Assert.Equal(target.AdminId, x.AuthorId));
        Assert.Null(await after.GetAsync<UserAccount>(source.AdminId));
        Assert.Equal("original bytes", await File.ReadAllTextAsync(target.Files.PathFor(source.AttachmentId)));
    }

    /// <summary>Atlas 3.4 content (atlas-requirements 2: the overviews, a requirement's state label and service, history lines in its note)
    /// passes the import's validation: nothing refused, only the history lines' bullet list becomes a timed list (rich revision 5, one
    /// repair per revision), its Markdown unchanged.</summary>
    [PostgreSqlFact]
    public async Task Restore_takes_34_requirement_content_as_valid_and_unchanged()
    {
        var timed = Atlas.Markdown.Sync.LegacyDocumentRepair.Repair(InstallationFixture.DeploymentContent.ReplaceLineEndings(""));
        Assert.Contains("\"timedList\"", timed, StringComparison.Ordinal);
        var content = InstallationFixture.DeploymentContent.ReplaceLineEndings("");
        var markdown = BackupFixture.Markdown(content);
        Assert.Contains("```a-overview", markdown, StringComparison.Ordinal);
        await using var source = await BackupFixture.CreateAsync();
        await using var target = await BackupFixture.CreateAsync(sharedAs: nameof(BackupImportTests));
        await source.Host.SeedAsync(async session =>
        {
            foreach (var revision in await session.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId).ListAsync())
                (revision.ContentJson, revision.Markdown) = (content, markdown);
        });
        using var archive = await source.Archive.CreateAsync("full", default);
        await using (var input = File.OpenRead(archive.Path))
        {
            var preview = await target.Import.PreviewAsync(input, "restore", default);
            Assert.Equal((0, 2), (preview.Invalid, preview.Repaired));
        }

        await using (var input = File.OpenRead(archive.Path))
            await target.Import.ImportAsync(input, "restore", target.AdminId, default);

        using var check = target.Host.OpenSession();
        var restored = await check.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId).ListAsync();
        Assert.Equal(2, restored.Count);
        Assert.All(restored, x => Assert.Equal((timed, markdown), (x.ContentJson, x.Markdown)));
    }

    /// <summary>A 3.3 page archive (the deployment summary under atlas-requirements 1) restores with its summary repaired to the overview.</summary>
    [PostgreSqlFact]
    public async Task Restore_repairs_the_33_deployment_summary_to_the_overview()
    {
        var content = BackupFixture.Rich("""{"type":"requirementSummary","attrs":{"id":"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f0"}},""" + BackupFixture.Paragraph("Nasazení"),
            "\"plugins\":[{\"id\":\"atlas-requirements\",\"version\":1}],");
        await using var source = await BackupFixture.CreateAsync();
        await using var target = await BackupFixture.CreateAsync(sharedAs: nameof(BackupImportTests));
        await source.Host.SeedAsync(async session =>
        {
            foreach (var revision in await session.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId).ListAsync())
                revision.ContentJson = content;
        });
        using var archive = await source.Archive.CreateAsync("full", default);
        await using (var input = File.OpenRead(archive.Path))
        {
            var preview = await target.Import.PreviewAsync(input, "restore", default);
            Assert.Equal((0, 2), (preview.Invalid, preview.Repaired));
        }

        await using (var input = File.OpenRead(archive.Path))
            await target.Import.ImportAsync(input, "restore", target.AdminId, default);

        using var check = target.Host.OpenSession();
        Assert.All(await check.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId).ListAsync(), x =>
        {
            Assert.Contains("""{"type":"overview","attrs":{"id":"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f0","kind":"requirements"}}""", x.ContentJson, StringComparison.Ordinal);
            Assert.Null(HostDocumentValidator.Instance.Validate(x.ContentJson));
        });
    }

    [PostgreSqlFact]
    public async Task Import_refuses_an_archive_with_invalid_documents_and_writes_the_projection_of_a_valid_one()
    {
        const string invalid = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","attrs":{"nope":1}}]}}""";
        await using var source = await BackupFixture.CreateAsync();
        await using var target = await BackupFixture.CreateAsync(sharedAs: nameof(BackupImportTests));
        string published = null!;
        await source.Host.SeedAsync(async session =>
        {
            foreach (var revision in await session.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId).ListAsync())
            {
                // The published version 1 breaks the editor schema; the current version 2 is valid with a forged projection.
                published = revision.Version == 1 ? revision.ContentJson : published;
                (revision.ContentJson, revision.Markdown) = revision.Version == 1 ? (invalid, "as archived") : (revision.ContentJson, "forged");
            }
        });
        using (var archive = await source.Archive.CreateAsync("full", default))
        {
            await using (var input = File.OpenRead(archive.Path))
            {
                var preview = await target.Import.PreviewAsync(input, "restore", default);
                var issue = Assert.Single(preview.InvalidDocuments!);
                Assert.Equal((1, source.PageId, 1, "Published", "document.content[0].attrs.nope"), (preview.Invalid, issue.PageId, issue.Version, issue.Title, issue.Path));
            }
            await using (var input = File.OpenRead(archive.Path))
            {
                var refused = await Assert.ThrowsAsync<ApiException>(() => target.Import.ImportAsync(input, "restore", target.AdminId, default));
                Assert.Equal((400, "invalid_documents"), (refused.Status, refused.Code));
                Assert.Equal(source.PageId, Assert.Single((IReadOnlyList<BackupDocumentIssue>)refused.Extensions!["documents"]!).PageId);
            }
            using var nothing = target.Host.OpenSession();
            Assert.Null(await nothing.GetAsync<Page>(source.PageId));
        }

        // Repaired, the archive restores; its own Markdown is never stored.
        await source.Host.SeedAsync(async session => (await session.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId && x.Version == 1).SingleOrDefaultAsync()).ContentJson = published);
        using (var archive = await source.Archive.CreateAsync("full", default))
        await using (var input = File.OpenRead(archive.Path))
        {
            await target.Import.ImportAsync(input, "restore", target.AdminId, default);
        }
        using var check = target.Host.OpenSession();
        var restored = await check.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId).ListAsync();
        Assert.Equal(2, restored.Count);
        Assert.All(restored, revision => Assert.Equal(BackupFixture.Markdown(revision.ContentJson), revision.Markdown));
    }

    /// <summary>The five shapes Atlas 2.x stored that the schema refused (UX cycle 2, IM2; synthetic text): the archive restores, over
    /// existing rows too, with what the reader saw. Bold and italic code are valid as they are, the rest is repaired and listed.</summary>
    [PostgreSqlFact]
    public async Task Restore_repairs_Atlas_2_documents_the_schema_refused_and_lists_each_repair()
    {
        const string link = """{"type":"link","attrs":{"href":"https://example.com/HREF","target":"_blank","rel":"noopener noreferrer"}}""";
        var legacy = BackupFixture.Rich(string.Join(",",
            """{"type":"paragraph","content":[{"type":"text","text":"badge","marks":[LINKS]}]}""".Replace("LINKS", link.Replace("HREF", "outer") + "," + link.Replace("HREF", "inner"), StringComparison.Ordinal),
            """{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"bold"},{"type":"code"}]},{"type":"text","text":" "},{"type":"text","text":"y","marks":[{"type":"italic"},{"type":"code"}]}]}""",
            """{"type":"orderedList","attrs":{"start":12},"content":[{"type":"listItem","content":[{"type":"orderedList","attrs":{"start":12},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"inner"}]}]}]}]}]}""",
            """{"type":"orderedList","attrs":{"start":1},"content":[{"type":"listItem"}]}"""));
        await using var source = await BackupFixture.CreateAsync();
        await using var target = await BackupFixture.CreateAsync(sharedAs: nameof(BackupImportTests));
        await source.Host.SeedAsync(async session =>
            (await session.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId && x.Version == 1).SingleOrDefaultAsync()).ContentJson = legacy);
        using var archive = await source.Archive.CreateAsync("full", default);

        await using (var input = File.OpenRead(archive.Path))
        {
            var preview = await target.Import.PreviewAsync(input, "restore", default);
            Assert.Equal(0, preview.Invalid);
            Assert.Equal(3, preview.Repaired);
            Assert.All(preview.RepairedDocuments!, x => Assert.Equal((source.PageId, 1, "Published"), (x.PageId, x.Version, x.Title)));
            Assert.Contains(preview.RepairedDocuments!, x => x.Path == "document.content[0].content[0].marks[0]" && x.Reason.Contains("https://example.com/outer", StringComparison.Ordinal));
        }
        // A fresh database and the source itself (existing rows: the restored revisions join the history).
        foreach (var fixture in new[] { target, source })
        {
            await using (var input = File.OpenRead(archive.Path))
            {
                await fixture.Import.ImportAsync(input, "restore", fixture.AdminId, default);
            }
            using var check = fixture.Host.OpenSession();
            // The source keeps its own legacy row in the history; the restore adds valid ones.
            var revisions = (await check.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId && x.Channel == ContentChannels.Import).ListAsync()).ToList();
            Assert.Equal(2, revisions.Count);
            Assert.All(revisions, x => Assert.Null(HostDocumentValidator.Instance.Validate(x.ContentJson)));
            var restored = revisions.Where(x => x.Title == "Published").MaxBy(x => x.ActiveFrom)!;
            Assert.DoesNotContain("outer", restored.ContentJson, StringComparison.Ordinal);
            // The editor's serializer: code innermost, an empty first paragraph, ")" for a list right after a list.
            Assert.Equal("[badge](https://example.com/inner)\n\n**`x`** *`y`*\n\n12. \n    12. inner\n\n\n1) ", restored.Markdown);
        }
    }

    [PostgreSqlFact]
    public async Task Preview_refuses_what_the_import_refuses_a_Markdown_projection_over_the_limit()
    {
        // 600 000 stars are 1,2 M characters of Markdown (each escaped) in well under 2 M of JSON: valid JSON, too large a page (D-L2).
        var large = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"STARS"}]}]}}"""
            .Replace("STARS", new string('*', 600_000), StringComparison.Ordinal);
        await using var source = await BackupFixture.CreateAsync();
        await using var target = await BackupFixture.CreateAsync(sharedAs: nameof(BackupImportTests));
        await source.Host.SeedAsync(async session =>
            (await session.QueryOver<PageRevision>().Where(x => x.PageId == source.PageId && x.Version == 1).SingleOrDefaultAsync()).ContentJson = large);
        using var archive = await source.Archive.CreateAsync("full", default);
        await using var input = File.OpenRead(archive.Path);
        var preview = await target.Import.PreviewAsync(input, "restore", default);
        var issue = Assert.Single(preview.InvalidDocuments!);
        Assert.Equal((1, 1, null), (preview.Invalid, issue.Version, issue.Path));
        Assert.StartsWith("Stránka je příliš velká: text stránky má", issue.Reason, StringComparison.Ordinal);
    }

    [PostgreSqlFact]
    public async Task Restore_remaps_conflicting_attachment_bytes_and_keeps_existing_file_intact()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await File.WriteAllTextAsync(fixture.Files.PathFor(fixture.AttachmentId), "newer live bytes");
        await using var input = File.OpenRead(archive.Path);
        await fixture.Import.ImportAsync(input, "restore", fixture.AdminId, default);
        Assert.Equal("newer live bytes", await File.ReadAllTextAsync(fixture.Files.PathFor(fixture.AttachmentId)));
        using var check = fixture.Host.OpenSession();
        var current = await check.QueryOver<PageRevision>().Where(x => x.PageId == fixture.PageId && x.ActiveTo == null).SingleOrDefaultAsync();
        var restoredId = Assert.Single(Atlas.Server.Features.Attachments.Domain.AttachmentReferences.Extract(current.ContentJson));
        Assert.NotEqual(fixture.AttachmentId, restoredId);
        Assert.Equal("original bytes", await File.ReadAllTextAsync(fixture.Files.PathFor(restoredId)));
    }

    [PostgreSqlFact]
    public async Task Add_preserves_trash_batch_grouping_under_new_identity()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var secondTrashId = Guid.NewGuid();
        Guid? batch = null;
        await fixture.Host.SeedAsync(async session =>
        {
            var original = await session.GetAsync<Page>(fixture.TrashedPageId);
            batch = original.TrashBatchId;
            await session.SaveAsync(new Page { Id = secondTrashId, SpaceId = original.SpaceId, OwnerId = fixture.AdminId,
                IsTrashed = true, TrashedAt = original.TrashedAt, TrashedById = fixture.AdminId, TrashBatchId = batch });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevision { Channel = ContentChannels.Web, Id = Guid.NewGuid(), PageId = secondTrashId, Version = 1, Title = "Second trash",
                ContentJson = BackupFixture.Rich(BackupFixture.Paragraph("trash")), Markdown = "trash", AuthorId = fixture.AdminId, ActiveFrom = DateTime.UtcNow });
        });
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await using var input = File.OpenRead(archive.Path);
        await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        using var check = fixture.Host.OpenSession();
        var allTrash = await check.QueryOver<Page>().Where(x => x.IsTrashed).ListAsync();
        var restored = allTrash.Where(x => x.Id != fixture.TrashedPageId && x.Id != secondTrashId).ToList();
        Assert.Equal(2, restored.Count);
        var restoredBatch = Assert.Single(restored.Select(x => x.TrashBatchId).Distinct());
        Assert.NotEqual(batch, restoredBatch);
    }

    [PostgreSqlFact]
    public async Task Preview_rejects_publication_pointer_that_disagrees_with_history()
    {
        await using var fixture = await BackupFixture.CreateAsync(sharedAs: nameof(BackupImportTests));
        using var archive = await fixture.Archive.CreateAsync("full", default);
        using (var zip = ZipFile.Open(archive.Path, ZipArchiveMode.Update))
        {
            var path = $"pages/{fixture.PageId}/page.json";
            var entry = zip.GetEntry(path)!;
            System.Text.Json.Nodes.JsonNode node;
            using (var reader = new StreamReader(entry.Open()))
            {
                node = System.Text.Json.Nodes.JsonNode.Parse(await reader.ReadToEndAsync())!;
            }
            node["publications"]![0]!["revisionId"] = node["revisions"]![1]!["id"]!.GetValue<string>();
            var bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
            entry.Delete();
            using (var output = zip.CreateEntry(path).Open())
            {
                output.Write(bytes);
            }
            var manifest = zip.GetEntry("manifest.json")!;
            System.Text.Json.Nodes.JsonNode manifestNode;
            using (var reader = new StreamReader(manifest.Open()))
            {
                manifestNode = System.Text.Json.Nodes.JsonNode.Parse(await reader.ReadToEndAsync())!;
            }
            var file = manifestNode["files"]!.AsArray().Single(x => x!["path"]!.GetValue<string>() == path)!;
            file["size"] = bytes.Length;
            file["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            manifest.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            await writer.WriteAsync(manifestNode.ToJsonString());
        }
        await using var input = File.OpenRead(archive.Path);
        var importer = new WikiBackupImport(null!, null!, null!);
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.PreviewAsync(input, "restore", default));
    }

    [PostgreSqlFact]
    public async Task Restore_checks_preserved_children_with_one_statement_shape_whatever_the_number_of_pages()
    {
        // The check names every archived page; PostgreSQL takes at most 65,535 parameters per statement, so its text must not grow with them.
        await using var fixture = await BackupFixture.CreateAsync();
        using var twoPages = await fixture.Archive.CreateAsync("full", default);
        await fixture.Host.SeedAsync(async session =>
        {
            var third = Guid.NewGuid();
            await session.SaveAsync(new Page { Id = third, SpaceId = (await session.GetAsync<Page>(fixture.PageId)).SpaceId, OwnerId = fixture.AdminId });
            await session.FlushAsync();
            await session.SaveAsync(new PageRevision { Channel = ContentChannels.Web, Id = Guid.NewGuid(), PageId = third, Version = 1, Title = "Third",
                ContentJson = BackupFixture.Rich(BackupFixture.Paragraph("third")), Markdown = "third", AuthorId = fixture.AdminId, ActiveFrom = DateTime.UtcNow });
        });
        using var threePages = await fixture.Archive.CreateAsync("full", default);
        // A page created after both backups strands under a moved parent, so each restore stops at the check.
        var newSpace = await fixture.Host.SeedSpaceAsync("Moved later");
        await fixture.Host.SeedAsync(async session =>
        {
            var parent = await session.GetAsync<Page>(fixture.PageId);
            parent.SpaceId = newSpace;
            await session.FlushAsync();
            await session.SaveAsync(new Page { Id = Guid.NewGuid(), SpaceId = newSpace, ParentId = parent.Id, OwnerId = fixture.AdminId });
        });

        Assert.Equal(await RejectedRestoreAsync(twoPages.Path), await RejectedRestoreAsync(threePages.Path));
        // A refused restore changes nothing: the page stays where it was moved.
        using (var check = fixture.Host.OpenSession())
        {
            Assert.Equal(newSpace, (await check.GetAsync<Page>(fixture.PageId)).SpaceId);
        }

        async Task<IReadOnlyList<string>> RejectedRestoreAsync(string path)
        {
            using var recording = SqlRecording.Start();
            await using var input = File.OpenRead(path);
            await Assert.ThrowsAsync<ApiException>(() => fixture.Import.ImportAsync(input, "restore", fixture.AdminId, default));
            return recording.Statements;
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("pages\\escape")]
    [InlineData("C:/drive")]
    public async Task Preview_rejects_unsafe_archive_paths_before_database_access(string path)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(path).Open());
            writer.Write("invalid");
        }
        stream.Position = 0;
        var importer = new WikiBackupImport(null!, null!, null!);
        await Assert.ThrowsAsync<InvalidDataException>(() => importer.PreviewAsync(stream, "add", default));
    }

    /// <summary>The Atlas 3 identity follows a restore through the page triggers: a page the restore moves back takes its
    /// ContentResource back to its space, and the copies of an "add" are registered under their new ids.</summary>
    [PostgreSqlFact]
    public async Task Restore_and_add_keep_every_page_registered_as_a_content_resource_in_its_space()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        using var archive = await fixture.Archive.CreateAsync("full", default);
        var original = Guid.Empty;
        var later = await fixture.Host.SeedSpaceAsync("Moved later");
        await fixture.Host.SeedAsync(async session =>
        {
            var page = await session.GetAsync<Page>(fixture.PageId);
            original = page.SpaceId;
            page.SpaceId = later;
        });
        await using (var input = File.OpenRead(archive.Path))
        {
            await fixture.Import.ImportAsync(input, "restore", fixture.AdminId, default);
        }
        using (var session = fixture.Host.OpenSession())
        {
            Assert.Equal(original, (await session.GetAsync<Atlas.Server.Platform.Resources.ContentResource>(fixture.PageId)).SpaceId);
        }

        await using (var input = File.OpenRead(archive.Path))
        {
            await fixture.Import.ImportAsync(input, "add", fixture.AdminId, default);
        }
        using var check = fixture.Host.OpenSession();
        var pages = await check.QueryOver<Page>().ListAsync();
        Assert.Equal(4, pages.Count);
        foreach (var page in pages)
        {
            var resource = await check.GetAsync<Atlas.Server.Platform.Resources.ContentResource>(page.Id);
            Assert.Equal(("atlas.page", (Guid?)page.SpaceId), (resource.TypeId, resource.SpaceId));
        }
    }

    /// <summary>5.2: a page the restore moves back into a space that verifies its pages starts as a created one, the restorer as steward;
    /// a page that stays where it was gets no row.</summary>
    [PostgreSqlFact]
    public async Task A_page_restored_into_a_verifying_space_gets_the_restorer_as_its_steward()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        using var archive = await fixture.Archive.CreateAsync("full", default);
        var later = await fixture.Host.SeedSpaceAsync("Moved later");
        var original = Guid.Empty;
        await fixture.Host.SeedAsync(async session =>
        {
            var page = await session.GetAsync<Page>(fixture.PageId);
            original = page.SpaceId;
            page.SpaceId = later;
        });
        await fixture.Host.ExecuteAsync("""UPDATE "Space" SET "VerificationIntervalMonths"=6 WHERE "Id"=:id""", original);
        await using (var input = File.OpenRead(archive.Path))
        {
            await fixture.Import.ImportAsync(input, "restore", fixture.AdminId, default);
        }
        using var session = fixture.Host.OpenSession();
        var stewards = (await session.QueryOver<Atlas.Server.Features.PageVerifications.Domain.PageVerification>().ListAsync())
            .ToDictionary(x => x.PageId, x => x.StewardId);
        Assert.Equal(fixture.AdminId, stewards[fixture.PageId]);
        // The other page lies in the same verifying space, but stayed there: no row, not for lack of a verifying space.
        Assert.Equal(original, (await session.GetAsync<Page>(fixture.TrashedPageId)).SpaceId);
        Assert.False(stewards.ContainsKey(fixture.TrashedPageId));
    }

    [PostgreSqlFact]
    public async Task Restore_keeps_identity_and_existing_history_and_rejects_tampered_bytes()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        using var archive = await fixture.Archive.CreateAsync("full", default);
        await using (var input = File.OpenRead(archive.Path))
        {
            await fixture.Import.ImportAsync(input, "restore", fixture.AdminId, default);
        }
        using (var session = fixture.Host.OpenSession())
        {
            Assert.Equal(2, await session.QueryOver<Page>().RowCountAsync());
            var revisions = await session.QueryOver<PageRevision>().Where(x => x.PageId == fixture.PageId).ListAsync();
            Assert.True(revisions.Count >= 2);
            Assert.Equal("Current", revisions.Single(x => x.ActiveTo == null).Title);
        }
        using (var zip = ZipFile.Open(archive.Path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry($"attachments/{fixture.AttachmentId}/manual.txt")!;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry($"attachments/{fixture.AttachmentId}/manual.txt").Open());
            writer.Write("tampered bytes");
        }
        await using var corrupt = File.OpenRead(archive.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Import.ImportAsync(corrupt, "restore", fixture.AdminId, default));
        Assert.Equal("original bytes", await File.ReadAllTextAsync(fixture.Files.PathFor(fixture.AttachmentId)));
    }
}
