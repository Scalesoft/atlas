using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Features.Administration.Queries;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Settings.Queries;
using Atlas.Server.Features.Backups;
using Atlas.Server.Features.Backups.Archive;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Queries;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.YouTrack;
using Atlas.Server.Features.YouTrack.Commands;
using Atlas.Server.Features.YouTrack.Queries;
using Atlas.YouTrack;
using Atlas.Server.Platform.Backup;
using Atlas.Server.Platform.Events;
using Atlas.Server.Tests.Attachments;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Backups;

/// <summary>Format v7 over ZIP (plan chapter 15): determinism, the secure part, validation and the restore into an empty installation.</summary>
public sealed class InstallationBackupTests
{
    [PostgreSqlFact]
    public async Task The_same_installation_gives_the_same_bytes_whenever_it_is_exported()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        await f.GenerateKeyAsync();

        var first = await f.ArchiveAsync("first.zip");
        await Task.Delay(1100);
        var second = await f.ArchiveAsync("second.zip");

        // No time of the run anywhere: not in the manifest, not in the ZIP entries.
        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        var manifest = Entries(first)[BackupManifest.Path];
        Assert.DoesNotContain(DateTime.UtcNow.ToString("yyyy-MM-dd"), manifest);
        Assert.Contains("\"format\": \"atlas-backup\"", manifest);
        Assert.Contains("\"version\": 7", manifest);
    }

    [PostgreSqlFact]
    public async Task A_saved_revision_changes_only_the_files_of_its_rows()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        await f.GenerateKeyAsync();
        var before = Entries(await f.ArchiveAsync("before.zip"));
        var page = await f.Pages.As<PageDto>(f.Pages.Writer, new GetPage(f.PageId));

        await f.Pages.As<PageDto>(f.Pages.Writer, new SavePage(f.PageId, new SavePageRequest("Provoz", AttachmentFixture.Rich("third"), "third", page.Version)));
        var after = Entries(await f.ArchiveAsync("after.zip"));

        var changed = after.Keys.Union(before.Keys).Where(x => before.GetValueOrDefault(x) != after.GetValueOrDefault(x)).Order(StringComparer.Ordinal).ToList();
        Assert.Contains(BackupManifest.Path, changed);
        // The new revision, the closed one, the page's version and its envelope; every account, grant and secret stays as it was.
        Assert.Contains(changed, x => x.StartsWith(".atlas/modules/pages/PageRevision/", StringComparison.Ordinal));
        Assert.Contains($".atlas/modules/pages/Page/{f.PageId:D}.json", changed);
        Assert.All(changed, x => Assert.True(x == BackupManifest.Path || x.StartsWith(".atlas/modules/pages/", StringComparison.Ordinal)
            || x.StartsWith(".atlas/modules/platform/ContentResource/", StringComparison.Ordinal), x));
        Assert.True(changed.Count <= 10, string.Join("\n", changed));
    }

    [PostgreSqlFact]
    public async Task Accounts_grants_and_secrets_travel_only_encrypted_and_each_content_is_stored_once()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        var key = await f.GenerateKeyAsync();

        var entries = Entries(await f.ArchiveAsync());

        var plain = entries.Where(x => !x.Key.EndsWith(".enc", StringComparison.Ordinal)).Select(x => x.Value).ToList();
        foreach (var secret in new[] { InstallationFixture.YouTrackToken, InstallationFixture.KdbxPassword, "hash-of-the-writer", "\"Username\"" })
        {
            Assert.DoesNotContain(plain, x => x.Contains(secret, StringComparison.Ordinal));
        }
        Assert.Contains(entries.Keys, x => x.StartsWith(".atlas/secure/identity/UserAccount/", StringComparison.Ordinal));
        Assert.Contains(entries.Keys, x => x.StartsWith(".atlas/secure/access/SpaceGrant/", StringComparison.Ordinal));
        Assert.Contains($".atlas/secure/vaults/files/{f.VaultId:D}.kdbx.enc", entries.Keys);
        // Inside the encrypted file a Data Protection secret is readable again, for a host with other Data Protection keys.
        var youTrack = entries.Keys.Single(x => x.StartsWith(".atlas/secure/settings/YouTrackConfiguration/", StringComparison.Ordinal));
        using (var zip = ZipFile.OpenRead(Path.Combine(f.Root, "complete.zip")))
        {
            await using var stream = zip.GetEntry(youTrack)!.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.Contains(InstallationFixture.YouTrackToken, Encoding.UTF8.GetString(key.Decrypt(youTrack, buffer.ToArray())));
        }
        // Two attachments with the same bytes: one blob; the uploaded space cover is the other.
        Assert.Equal(2, entries.Keys.Count(x => x.StartsWith(".atlas/blobs/", StringComparison.Ordinal)));
        Assert.Equal(2, entries.Keys.Count(x => x.StartsWith(".atlas/modules/attachments/Attachment/", StringComparison.Ordinal)));
        Assert.Contains(entries, x => x.Key.StartsWith(".atlas/modules/identity/names/", StringComparison.Ordinal) && x.Value.Contains("\"DisplayName\""));
    }

    [PostgreSqlFact]
    public async Task Validation_refuses_a_changed_or_unlisted_file_a_foreign_key_an_unknown_column_and_another_module_format()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        var key = await f.GenerateKeyAsync();
        var zip = await f.ArchiveAsync();

        async Task<Exception> OpenAsync(Action<string> change, string? text = null)
        {
            var folder = Path.Combine(f.Root, "tree-" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(zip, folder);
            change(folder);
            return await Assert.ThrowsAnyAsync<Exception>(() => BackupDirectory.OpenAsync(folder, text ?? key.Text, default));
        }
        var row = $".atlas/modules/pages/Page/{f.PageId:D}.json";
        Assert.Contains("Kontrolní součet", (await OpenAsync(folder => File.AppendAllText(Path.Combine(folder, row), " "))).Message);
        Assert.Contains("manifest neuvádí", (await OpenAsync(folder => File.WriteAllText(Path.Combine(folder, ".atlas/modules/pages/Page/extra.json"), "{}"))).Message);
        Assert.Contains("chybí soubor", (await OpenAsync(folder => File.Delete(Path.Combine(folder, row)))).Message);
        Assert.Equal(400, Assert.IsType<ApiException>(await OpenAsync(_ => { }, BackupKey.Generate().Text)).Status);

        // Consistent files the running version cannot take: refused in the preview, before anything is written.
        await using var target = await f.CreateTargetAsync();
        var edited = Path.Combine(f.Root, "edited");
        ZipFile.ExtractToDirectory(zip, edited);
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(edited, row)))!.AsObject();
        node["Bogus"] = 1;
        File.WriteAllText(Path.Combine(edited, row), node.ToJsonString());
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(edited, BackupManifest.Path)))!.AsObject();
        manifest["files"]![row] = InstallationFixture.Sha256(Path.Combine(edited, row));
        manifest["modules"]!["boards"] = 99;
        File.WriteAllText(Path.Combine(edited, BackupManifest.Path), manifest.ToJsonString());
        using var backup = await BackupDirectory.OpenAsync(edited, key.Text, default);
        var preview = await target.Installation.Restore.PreviewAsync(backup, default);
        Assert.Contains(preview.Problems, x => x.Contains("Bogus", StringComparison.Ordinal));
        Assert.Contains(preview.Problems, x => x.Contains("boards", StringComparison.Ordinal) && x.Contains("99", StringComparison.Ordinal));
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => target.Installation.Restore.RestoreAsync(backup, target.Restorer.Id, default))).Status);
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT count(*) FROM \"Page\""));
    }

    [PostgreSqlFact]
    public async Task The_round_trip_data_fills_every_column_of_every_content_table()
    {
        // The round trips compare table by table; an empty table or an always-NULL column would pass them without proving anything.
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        var content = f.Installation.Catalog.Modules.Where(x => InstallationFixture.ContentModules.Contains(x.ModuleId)).SelectMany(x => x.Tables);

        var unfilled = await InstallationFixture.UnfilledAsync(f.ConnectionString, content);
        Assert.True(unfilled.Count == 0, string.Join(", ", unfilled));
    }

    [PostgreSqlFact]
    public async Task A_complete_backup_restores_into_an_empty_installation_row_for_row_with_its_files()
    {
        await using var f = await InstallationFixture.CreateAsync(outbox: true);
        await f.PopulateAsync();
        // A signed-out browser session stays revoked (the fingerprints below compare its row).
        await InstallationFixture.ExecuteAsync(f.ConnectionString,
            """INSERT INTO "RevokedBrowserSession" ("Id", "UserId") SELECT gen_random_uuid(), "Id" FROM "UserAccount" ORDER BY "Id" LIMIT 1""");
        var key = await f.GenerateKeyAsync();
        var zip = await f.ArchiveAsync();
        var tables = f.Installation.Catalog.Modules.SelectMany(x => x.Tables).ToList();
        var expected = await InstallationFixture.FingerprintsAsync(f.ConnectionString, tables);
        await using var target = await f.CreateTargetAsync(outbox: true);
        // The process that restores keeps its jobs: a paused job stays paused and a run in progress keeps its row.
        await InstallationFixture.ExecuteAsync(target.Host.ConnectionString, """
            INSERT INTO "Job" ("Id", "ModuleId", "Paused", "PausedAt") VALUES ('backups.zip', 'backups', true, now());
            INSERT INTO "JobRun" ("JobId", "Trigger", "State", "StartedAt") VALUES ('backups.zip', 'manual', 'running', now());
            """);

        var result = await target.RestoreAsync(zip, key.Text);

        Assert.Equal("full", result.Mode);
        // A backup of this schema taken while the 3.4 conversion ran may bring 3.3 content back: the pages' rebuild requests the job again.
        Assert.Equal(1L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString,
            """SELECT count(*) FROM "JobRun" WHERE "JobId" = 'pages.requirements-overview' AND "State" = 'queued'"""));
        Assert.Equal(1L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString,
            """SELECT count(*) FROM "JobRun" WHERE "JobId" = 'pages.requirements-headings' AND "State" = 'queued'"""));
        Assert.Equal("true|running", await InstallationFixture.ScalarAsync(target.Host.ConnectionString,
            """SELECT j."Paused" || '|' || r."State" FROM "Job" j JOIN "JobRun" r ON r."JobId" = j."Id" WHERE j."Id" = 'backups.zip'"""));
        var actual = await InstallationFixture.FingerprintsAsync(target.Host.ConnectionString, tables);
        // The live state is the server's to seed again (3.6 LiveSeeds: every page announcement on either side, and the restore's job).
        Assert.Empty(expected.Where(x => x.Key != "PageCollaboration" && actual[x.Key] != x.Value).Select(x => x.Key));
        Assert.Equal(1L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString,
            """SELECT count(*) FROM "JobRun" WHERE "JobId" = 'collaboration.live-seeds' AND "State" = 'queued'"""));
        Assert.Contains(expected, x => x.Key == "PageRevision" && !x.Value.StartsWith("0:", StringComparison.Ordinal));
        Assert.Contains(expected, x => x.Key == "RevokedBrowserSession" && x.Value.StartsWith("1:", StringComparison.Ordinal));
        // Secrets are protected again by the restoring host and decrypt there to what they were.
        var token = (string)(await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT \"ProtectedToken\" FROM \"YouTrackConfiguration\""))!;
        Assert.Equal(InstallationFixture.YouTrackToken, target.Installation.Protection.CreateProtector(SaveYouTrackSettingsHandler.TokenPurpose).Unprotect(token));
        var kdbx = (string)(await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT \"ProtectedKdbxPassword\" FROM \"Vault\""))!;
        Assert.Equal(InstallationFixture.KdbxPassword, target.Installation.Protection.CreateProtector(VaultProtection.KdbxPasswordPurpose).Unprotect(kdbx));
        Assert.Equal(InstallationFixture.KdbxFile, await File.ReadAllBytesAsync(Path.Combine(target.Installation.Vaults.Managed(f.LocationId), "Provoz.kdbx")));
        // Every attachment's bytes under its own id, and the derived search documents rebuilt.
        foreach (var (id, sha256) in await AttachmentsAsync(f.ConnectionString))
        {
            Assert.Equal(sha256, InstallationFixture.Sha256(target.Files.PathFor(id)));
        }
        Assert.Equal(await InstallationFixture.ScalarAsync(f.ConnectionString, "SELECT count(*) FROM \"SearchDocument\""),
            await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT count(*) FROM \"SearchDocument\""));
        Assert.Equal(await InstallationFixture.ScalarAsync(f.ConnectionString, "SELECT count(*) FROM \"WorkingSearchDocument\""),
            await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT count(*) FROM \"WorkingSearchDocument\""));
        Assert.Contains(expected, x => x.Key == "BoardItem" && x.Value.StartsWith("8:", StringComparison.Ordinal));
        await f.AssertDeploymentRestoredAsync(target.Host.ConnectionString);
        Assert.Contains(expected, x => x.Key == "TreeReference" && x.Value.StartsWith("2:", StringComparison.Ordinal));
        // Derived and operational data is not carried: contributions are rebuilt, the board's sync log starts empty.
        Assert.Equal(await InstallationFixture.ScalarAsync(f.ConnectionString, "SELECT string_agg(\"UserId\" || ':' || \"PageId\" || ':' || \"LastRevisionVersion\", ',' ORDER BY \"UserId\", \"PageId\") FROM \"PageContribution\""),
            await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT string_agg(\"UserId\" || ':' || \"PageId\" || ':' || \"LastRevisionVersion\", ',' ORDER BY \"UserId\", \"PageId\") FROM \"PageContribution\""));
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT count(*) FROM \"BoardOperation\""));
        Assert.Equal(1L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT count(*) FROM \"BoardSearchDocument\" WHERE \"Body\" LIKE '%VPN%'"));
        Assert.Equal(1L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT count(*) FROM \"CommentSearchDocument\""));
        Assert.Equal("Jana Nováková", await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT \"Title\" FROM \"ContentResource\" WHERE \"TypeId\" = 'atlas.contact'"));
        // PER1-6: the derived 3.0 tables come back from the restored content, the entity catalog through the outbox: the restore queues
        // its rebuild, which the restart would start.
        await Events.OutboxTests.DrainAsync(f.Pages.Host);
        await target.Host.Services.GetRequiredService<Atlas.Server.Platform.Jobs.IJobRunner>()
            .RunToEndAsync(Atlas.Server.Features.Entities.EntityCatalogRebuildJob.JobId, "Obnova zálohy");
        // The page links and the mentions are rebuilt by their jobs, which the restore queues as well.
        foreach (var job in new[] { Atlas.Server.Features.PageLinks.PageLinkRebuildJob.JobId, Atlas.Server.Features.Contacts.PageObjectMentionRebuildJob.JobId })
        {
            Assert.Equal(1L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, $"""SELECT count(*) FROM "JobRun" WHERE "JobId" = '{job}' AND "State" = 'queued'"""));
            await target.Host.Services.GetRequiredService<Atlas.Server.Platform.Jobs.IJobRunner>().RunToEndAsync(job, "Obnova zálohy");
        }
        await Events.OutboxTests.DrainAsync(target.Host);
        // The requirement index is kept in code (migration 2026110870): the source host here has no outbox, so its index is made current
        // as a read would; the target's was rebuilt by the restore itself.
        using (var session = f.Pages.Host.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await Atlas.Server.Features.Projector.Persistence.ProjectorIndex.EnsureFreshAsync(session, null, default);
            await transaction.CommitAsync();
        }
        foreach (var derived in new[]
        {
            """SELECT string_agg("ResourceId" || '|' || "RevisionKind" || '|' || "IdentityKey", ',' ORDER BY "ResourceId", "RevisionKind", "IdentityKey") FROM "EntityOccurrence" """,
            """SELECT string_agg("PageId" || '|' || "UserId", ',' ORDER BY "PageId", "UserId") FROM "PageMentionNotice" """,
            """SELECT string_agg(t::text, ',' ORDER BY t::text) FROM (SELECT * FROM "PageObjectMention") t""",
            """SELECT count(*)::text FROM "ProjectorRequirementIndex" """,
        })
        {
            Assert.Equal(await InstallationFixture.ScalarAsync(f.ConnectionString, derived), await InstallationFixture.ScalarAsync(target.Host.ConnectionString, derived));
        }
        Assert.NotEqual(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT count(*) FROM "EntityOccurrence" """));
        Assert.NotEqual(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT count(*) FROM "PageMentionNotice" """));
        // GAP2-3: the contact a never published draft names is in use after the restore as before it.
        Assert.Equal(1L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT count(*) FROM "PageObjectMention" WHERE "ObjectId" = @id""", ("id", f.ContactId)));
        Assert.NotEqual(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT count(*) FROM "ProjectorRequirementIndex" """));
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT count(*) FROM wolverine_dead_letters"""));
        // The restored installation works: the page opens with its content for its writer; a client of the board that asks for the
        // changes since its sequence learns that the log is gone and reloads the board.
        target.Host.Actor.Current = f.Pages.Writer;
        Assert.Equal("Provoz", (await target.Host.InvokeAsync<PageDto>(new GetPage(f.PageId))).Title);
        Assert.Contains((await target.Host.InvokeAsync<PageTreeDto>(new GetPageTree(f.Pages.SpaceId))).Items,
            item => (item.Type, item.Title, item.ParentId, item.Anchor) == ("reference", "Jana Nováková", (Guid?)f.PageId, "kontakt"));
        var resync = await Assert.ThrowsAsync<ApiException>(() => target.Host.InvokeAsync<BoardOperationsDto>(new GetBoardOperations(f.BoardId, 0)));
        Assert.Equal((409, "resync_required"), (resync.Status, resync.Code));
        Assert.Empty((await target.Host.InvokeAsync<BoardOperationsDto>(new GetBoardOperations(f.BoardId, 1))).Batches);
        await target.AssertAccountsHaveTheirContactAsync();
    }

    [PostgreSqlFact]
    public async Task A_backup_with_a_null_secret_as_31_wrote_it_restores_whole_with_the_secret_not_set()
    {
        // 3.1.2 wrote an empty secret (a YouTrack token cleared, an AI profile without a key) as null, and so does a lost key ring;
        // its restore into the NOT NULL column aborted the whole restore.
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        await InstallationFixture.ExecuteAsync(f.ConnectionString, """
            UPDATE "YouTrackConfiguration" SET "ProtectedToken" = '';
            UPDATE "UserAccount" SET "IsWikiAdmin" = true WHERE "Id" = @writer;
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
              VALUES (@ai,'chat',true,'openai-compatible','company','http://lmstudio.example:1234/v1','gemma-4',120,2,@writer,now());
            INSERT INTO "AiProfileAssignment"("Id","Kind","RevisionId","ChangedById","ActiveFrom") VALUES (gen_random_uuid(),'chat',@ai,@writer,now());
            """, ("ai", Guid.NewGuid()), ("writer", f.Pages.Writer.Id));
        var key = await f.GenerateKeyAsync();
        var zip = await f.ArchiveAsync();
        var tables = f.Installation.Catalog.Modules.SelectMany(x => x.Tables).ToList();
        var expected = await InstallationFixture.FingerprintsAsync(f.ConnectionString, tables);

        var edited = Path.Combine(f.Root, "as-3.1");
        ZipFile.ExtractToDirectory(zip, edited);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(edited, BackupManifest.Path)))!.AsObject();
        var rewritten = 0;
        foreach (var (folder, column) in new[] { ("settings/YouTrackConfiguration/", "ProtectedToken"), ("ai/AiProfileRevision/", "ProtectedApiKey") })
        {
            foreach (var file in Directory.GetFiles(Path.Combine(edited, ".atlas", "secure", folder)))
            {
                var path = ".atlas/secure/" + folder + Path.GetFileName(file);
                var row = JsonNode.Parse(key.Decrypt(path, File.ReadAllBytes(file)))!.AsObject();
                Assert.Equal("", (string?)row[column]);
                row[column] = null;
                File.WriteAllBytes(file, key.Encrypt(path, Encoding.UTF8.GetBytes(row.ToJsonString())));
                manifest["files"]![path] = InstallationFixture.Sha256(file);
                rewritten++;
            }
        }
        Assert.Equal(2, rewritten);
        File.WriteAllText(Path.Combine(edited, BackupManifest.Path), manifest.ToJsonString());
        await using var target = await f.CreateTargetAsync();

        using (var backup = await BackupDirectory.OpenAsync(edited, key.Text, default))
        {
            Assert.Equal("full", (await target.RestoreAsync(backup)).Mode);
        }

        var actual = await InstallationFixture.FingerprintsAsync(target.Host.ConnectionString, tables);
        Assert.Empty(expected.Where(x => actual[x.Key] != x.Value).Select(x => x.Key));
        Assert.Equal("", await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT "ProtectedToken" FROM "YouTrackConfiguration" """));
        Assert.Equal("", await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT "ProtectedApiKey" FROM "AiProfileRevision" """));
        // A nullable secret column keeps null as its "not set": "" fails a managed vault location's check (and readers unprotect it).
        Assert.Equal(DBNull.Value, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT "ProtectedPassword" FROM "VaultLocation" """));
        using (var session = target.Host.OpenSession())
        {
            // The backup's accounts replace the target's: its administrator opens the settings.
            target.Host.Actor.Current = await session.GetAsync<UserAccount>(f.Pages.Writer.Id);
        }
        // The YouTrack settings page's query, built by hand: the test host does not register the module's actor check.
        using (var scope = target.Host.Services.CreateScope())
        {
            var handler = new GetYouTrackSettingsHandler(scope.ServiceProvider.GetRequiredService<NHibernate.ISession>(),
                ActivatorUtilities.CreateInstance<YouTrackActor>(scope.ServiceProvider), Options.Create(new YouTrackOptions { Enabled = true }));
            var youTrack = await handler.Handle(new GetYouTrackSettings(), default);
            Assert.Equal(("https://youtrack.example.test", false, "not-configured"), (youTrack.BaseUrl, youTrack.HasToken, youTrack.Status));
        }
        var ai = await target.Host.InvokeAsync<AiProfileView>(new GetAiProfile(AiProfileKind.Chat));
        Assert.Equal(("gemma-4", false), (ai.Model, ai.HasApiKey));
    }

    [PostgreSqlFact]
    public async Task A_backup_of_an_older_schema_gets_the_data_migrations_since_then_after_its_restore()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        // A 3.3 deployment board someone made a favourite, and a requirement card on another board (migration 2026110470).
        var deployment = Guid.NewGuid();
        await InstallationFixture.ExecuteAsync(f.ConnectionString, """
            INSERT INTO "Board"("Id","SpaceId","ParentPageId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SortOrder","Purpose")
              SELECT @deployment, "SpaceId", "ParentPageId", 'Tabule nasazení', 7, now(), "CreatedBy", now(), false, 2048, 'projector.deployment' FROM "Board" WHERE "Id" = @board;
            """ + DerivedRows.Sql + """
            INSERT INTO "PageFavorite"("Id","UserId","PageId","ActiveFrom") SELECT gen_random_uuid(), "CreatedBy", @deployment, now() FROM "Board" WHERE "Id" = @board;
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","TargetResourceId","Version","IsDeleted","PropsJson")
              SELECT gen_random_uuid(), @board, 'page', 0, 400, 280, 128, 9, "ParentPageId", 1, false, '{"display": "requirement"}' FROM "Board" WHERE "Id" = @board;
            """, ("deployment", deployment), ("board", f.BoardId));
        var zip = await f.ArchiveAsync();
        // PER-S-2: the same rows as an installation before migration 2026103035 wrote them (no pending totals).
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(BackupManifest.Path)!;
            JsonNode manifest;
            using (var reader = new StreamReader(entry.Open()))
                manifest = JsonNode.Parse(await reader.ReadToEndAsync())!;
            manifest["schema"] = 2026103030;
            // Its live state still carries the projections migration 2026110670 dropped: they are left out, the rest restores.
            var collaboration = $".atlas/modules/pages/PageCollaboration/{f.PageId:D}.json";
            var stateEntry = archive.GetEntry(collaboration)!;
            JsonObject state;
            using (var reader = new StreamReader(stateEntry.Open()))
                state = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            state["ProjectionMarkdown"] = "VPN";
            state["ProjectionText"] = "";
            var stateBytes = Encoding.UTF8.GetBytes(state.ToJsonString());
            stateEntry.Delete();
            await using (var stream = archive.CreateEntry(collaboration).Open())
                await stream.WriteAsync(stateBytes);
            manifest["files"]![collaboration] = InstallationFixture.Sha256Of(stateBytes);
            entry.Delete();
            await using var writer = new StreamWriter(archive.CreateEntry(BackupManifest.Path).Open());
            await writer.WriteAsync(manifest.ToJsonString());
        }
        await using var target = await f.CreateTargetAsync();

        await target.RestoreAsync(zip, null);

        // The fixture's two unsaved updates (one of the personal API, one of the assistant).
        Assert.Equal(2, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT "PendingCount" FROM "PageCollaboration" WHERE "PageId" = @id""", ("id", f.PageId)));        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT count(*) FROM "ContentResource" WHERE "Id" = @id""", ("id", deployment)));
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString,
            """SELECT count(*) FROM "BoardItem" WHERE NOT "IsDeleted" AND "PropsJson" ->> 'display' = 'requirement'"""));
        // The page that declares atlas-requirements 1 (migration 2026110480): its conversion is requested again.
        Assert.Equal(1L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString,
            """SELECT count(*) FROM "JobRun" WHERE "JobId" = 'pages.requirements-overview' AND "State" = 'queued'"""));
    }

    [PostgreSqlFact]
    public async Task Without_the_key_the_content_comes_back_and_accounts_become_inactive_placeholders()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        await f.GenerateKeyAsync();
        var zip = await f.ArchiveAsync();
        var content = f.Installation.Catalog.Modules.SelectMany(x => x.Tables).Where(x => !x.Secure).ToList();
        var expected = await InstallationFixture.FingerprintsAsync(f.ConnectionString, content);
        await using var target = await f.CreateTargetAsync();

        var result = await target.RestoreAsync(zip, null);

        Assert.Equal("content-only", result.Mode);
        Assert.Contains(result.Warnings, x => x.Contains("Bez klíče", StringComparison.Ordinal));
        var actual = await InstallationFixture.FingerprintsAsync(target.Host.ConnectionString, content);
        // References to the secure part are cleared: the revision and the publication made through the API lose their token and
        // their channel becomes "legacy" (the check couples them); everything else, their content included, comes back as it was.
        Assert.Equal(["PagePublication", "PageRevision"], expected.Where(x => actual[x.Key] != x.Value).Select(x => x.Key).Order(StringComparer.Ordinal));
        Assert.Equal(await InstallationFixture.FingerprintsAsync(f.ConnectionString, content, "ApiTokenId", "Channel"),
            await InstallationFixture.FingerprintsAsync(target.Host.ConnectionString, content, "ApiTokenId", "Channel"));
        Assert.Equal("legacy|", await InstallationFixture.ScalarAsync(target.Host.ConnectionString,
            "SELECT \"Channel\" || '|' || coalesce(\"ApiTokenId\"::text, '') FROM \"PageRevision\" WHERE \"PageId\" = @id AND \"Version\" = 1", ("id", f.PageId)));
        // Authors are inactive placeholders under their names; the restorer stays the only active administrator; no grant survives.
        Assert.Equal("writer|false|restored-" + f.Pages.Writer.Id.ToString("N"), await InstallationFixture.ScalarAsync(target.Host.ConnectionString,
            "SELECT \"DisplayName\" || '|' || \"IsActive\" || '|' || \"Username\" FROM \"UserAccount\" WHERE \"Id\" = @id", ("id", f.Pages.Writer.Id)));
        Assert.Equal("restorer", await InstallationFixture.ScalarAsync(target.Host.ConnectionString,
            "SELECT string_agg(\"Username\", ',') FROM \"UserAccount\" WHERE \"IsActive\" AND \"IsWikiAdmin\""));
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT count(*) FROM \"SpaceGrant\""));
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT count(*) FROM \"BuiltinCredential\" WHERE \"UserId\" = @id", ("id", f.Pages.Writer.Id)));
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT count(*) FROM \"YouTrackConfiguration\""));
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT count(*) FROM \"Vault\""));
        // The restorer, wiki administrator, sees the restored page.
        target.Host.Actor.Current = target.Restorer;
        Assert.Equal("Provoz", (await target.Host.InvokeAsync<PageDto>(new GetPage(f.PageId))).Title);
        // The restorer's setup contact went with the installation's content; the restart gives the account its contact again.
        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, InstallationFixture.AccountContactCount, ("id", target.Restorer.Id)));
        await target.AssertAccountsHaveTheirContactAsync();
    }

    [PostgreSqlFact]
    public async Task Without_the_key_every_account_keeps_a_current_revision_and_the_restorer_reads_the_profile()
    {
        // The account history is operational and the restore empties it: the profile (/api/profile/account) answered 401 without it.
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        await f.GenerateKeyAsync();
        var zip = await f.ArchiveAsync();
        await using var target = await f.CreateTargetAsync();

        await target.RestoreAsync(zip, null);

        Assert.Equal(0L, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """
            SELECT count(*) FROM "UserAccount" u WHERE NOT EXISTS (SELECT 1 FROM "UserAccountRevision" r WHERE r."UserId" = u."Id" AND r."ActiveTo" IS NULL)
            """));
        target.Host.Actor.Current = target.Restorer;
        Assert.Equal(target.Restorer.DisplayName, (await target.Host.InvokeAsync<ProfileAccount>(new GetProfileAccount(null, null))).DisplayName);
    }

    [PostgreSqlFact]
    public async Task A_restore_needs_an_empty_installation_and_changes_nothing_when_refused()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        var key = await f.GenerateKeyAsync();
        var zip = await f.ArchiveAsync();
        await using var target = await f.CreateTargetAsync();
        target.Host.Actor.Current = target.Restorer;
        var space = await target.Host.SeedSpaceAsync("Vlastní", (target.Restorer, SpaceRole.Admin));
        await target.Host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(space, null, "Moje", AttachmentFixture.Rich("mine"), "mine")));

        await using var input = File.OpenRead(zip);
        using var backup = await BackupDirectory.ExtractAsync(input, key.Text, default);
        var preview = await target.Installation.Restore.PreviewAsync(backup, default);

        Assert.False(preview.Empty);
        Assert.Contains(preview.Problems, x => x.Contains("prázdnou instalaci", StringComparison.Ordinal));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => target.Installation.Restore.RestoreAsync(backup, target.Restorer.Id, default))).Status);
        Assert.Equal("Moje", await InstallationFixture.ScalarAsync(target.Host.ConnectionString, "SELECT string_agg(\"Title\", ',') FROM \"PageRevision\""));
        Assert.False(Directory.Exists(target.Files.Root) && Directory.EnumerateFiles(target.Files.Root).Any());
    }

    [PostgreSqlFact]
    public async Task Only_the_untouched_account_contacts_of_the_setup_count_as_empty_and_a_contact_someone_wrote_refuses_the_restore()
    {
        // R4 (3.5): the setup's contacts.accounts gives the founder a contact at once, so a new installation must still take a restore;
        // a contact a person wrote or changed is content, which the restore would delete.
        await using var f = await InstallationFixture.CreateAsync();
        var zip = await f.ArchiveAsync();
        foreach (var written in new[]
        {
            // A contact entered by hand.
            """
            INSERT INTO "ContentResource"("Id","TypeId","Scope","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data")
              VALUES (gen_random_uuid(), 'atlas.contact', 'wiki', 1, now(), @restorer, now(), 'Jana Nováková', '{"name": "Jana Nováková"}')
            """,
            // The account's contact, changed since the setup.
            """UPDATE "ContentResource" SET "Version" = 2, "Data" = "Data" || '{"name": "Jan Restorer"}' WHERE "TypeId" = 'atlas.contact'""",
            // The account's own contact with a field the setup never writes.
            """UPDATE "ContentResource" SET "Data" = "Data" || '{"phones": ["+420 777 000 000"]}' WHERE "TypeId" = 'atlas.contact'""",
            // The account's contact in the trash.
            """UPDATE "ContentResource" SET "TrashedAt" = now(), "TrashedById" = @restorer WHERE "TypeId" = 'atlas.contact'""",
        })
        {
            await using var target = await f.CreateTargetAsync();
            await using var input = File.OpenRead(zip);
            using var backup = await BackupDirectory.ExtractAsync(input, null, default);
            Assert.True((await target.Installation.Restore.PreviewAsync(backup, default)).Empty);
            await InstallationFixture.ExecuteAsync(target.Host.ConnectionString, written, ("restorer", target.Restorer.Id));
            var contacts = await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT string_agg("Data"::text, ',' ORDER BY "Id") FROM "ContentResource" """);

            var preview = await target.Installation.Restore.PreviewAsync(backup, default);

            Assert.False(preview.Empty, written);
            Assert.Contains(preview.Problems, x => x.Contains("prázdnou instalaci", StringComparison.Ordinal));
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => target.Installation.Restore.RestoreAsync(backup, target.Restorer.Id, default))).Status);
            Assert.Equal(contacts, await InstallationFixture.ScalarAsync(target.Host.ConnectionString, """SELECT string_agg("Data"::text, ',' ORDER BY "Id") FROM "ContentResource" """));
        }
    }

    [PostgreSqlFact]
    public async Task A_2x_archive_is_pointed_to_the_archive_import_which_still_reads_it()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        using var archive = await new WikiBackupArchive(f.Pages.Host.Database, f.Pages.Files).CreateAsync("full", default);

        await using (var input = File.OpenRead(archive.Path))
        {
            var refused = await Assert.ThrowsAsync<InvalidDataException>(() => BackupDirectory.ExtractAsync(input, null, default));
            Assert.Contains("2.x (formát 1–6)", refused.Message);
            Assert.Contains("importem archivu", refused.Message);
        }
        await using (var input = File.OpenRead(archive.Path))
        {
            var import = new WikiBackupImport(f.Pages.Host.Database, f.Pages.Files, f.Pages.Host.Services.GetRequiredService<IServiceScopeFactory>());
            Assert.Equal(6, (await import.PreviewAsync(input, "restore", default)).Pages);
        }
    }

    [PostgreSqlFact]
    public async Task The_complete_zip_mode_creates_the_key_once_archives_format_7_and_skips_an_unchanged_installation()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        var target = Path.Combine(f.Root, "backups");
        var environment = new AttachmentTests.EnvironmentStub(f.Root);
        var service = new BackupService(f.Pages.Host.Database, new WikiBackupArchive(f.Pages.Host.Database, f.Pages.Files), f.Installation.Protection,
            environment, NullLogger<BackupService>.Instance, null, f.Installation.Archive);

        var saved = await service.SaveSettingsAsync(new BackupSettingsRequest(true, 1440, "complete", target, "", "", 10), default);
        var again = await service.SaveSettingsAsync(new BackupSettingsRequest(true, 1440, "complete", target, "", "", 10), default);
        var run = await service.RunAsync("manual", false, default);
        var unchanged = await service.RunAsync("manual", false, default);

        var key = BackupKey.Parse(saved.NewBackupKey!.Key);
        Assert.Null(again.NewBackupKey);
        Assert.Equal("succeeded", run!.Status);
        Assert.Equal("skipped", unchanged!.Status);
        var entries = Entries(Path.Combine(target, run.FileName!));
        Assert.Contains($"\"keyId\": \"{key.Id}\"", entries[BackupManifest.Path]);
        Assert.Contains(entries.Keys, x => x.StartsWith(".atlas/secure/identity/", StringComparison.Ordinal));
    }

    [PostgreSqlFact]
    public async Task A_new_installation_backs_up_the_whole_installation_and_its_zip_restores_boards_and_contacts()
    {
        await using var f = await InstallationFixture.CreateAsync();
        await f.PopulateAsync();
        var target = Path.Combine(f.Root, "backups");
        var service = new BackupService(f.Pages.Host.Database, new WikiBackupArchive(f.Pages.Host.Database, f.Pages.Files), f.Installation.Protection,
            new AttachmentTests.EnvironmentStub(f.Root), NullLogger<BackupService>.Instance, null, f.Installation.Archive);

        // GAP2-1: the administrator sets only where the archives go; what they hold is the default.
        var defaults = (await service.GetStatusAsync(default)).Settings;
        var saved = await service.SaveSettingsAsync(new BackupSettingsRequest(true, defaults.IntervalMinutes, defaults.Mode, target, "", "", defaults.RetentionCount), default);
        var run = await service.RunAsync("manual", false, default);
        await using var restored = await f.CreateTargetAsync();
        await restored.RestoreAsync(Path.Combine(target, run!.FileName!), saved.NewBackupKey!.Key);

        Assert.Equal("complete", defaults.Mode);
        Assert.Equal("Architektura", await InstallationFixture.ScalarAsync(restored.Host.ConnectionString, """SELECT "Title" FROM "Board" WHERE "Id" = @id""", ("id", f.BoardId)));
        Assert.Equal("Jana Nováková", await InstallationFixture.ScalarAsync(restored.Host.ConnectionString, """SELECT "Title" FROM "ContentResource" WHERE "Id" = @id""", ("id", f.ContactId)));
    }

    [PostgreSqlFact]
    public async Task After_migration_2026110172_a_zip_backup_set_up_before_keeps_its_mode()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_backup_complete_default_");
        await Events.OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110172);
        await Events.OutboxTests.ExecuteAsync(schema.ConnectionString, """UPDATE "BackupSettings" SET "Enabled" = true, "Destination" = '/var/backups/atlas'""");
        await using var host = await AtlasTestHost.CreateAsync(schema);

        Assert.Equal("full", await Events.OutboxTests.ScalarAsync(schema.ConnectionString, """SELECT "Mode" FROM "BackupSettings" """));
    }

    private static Dictionary<string, string> Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.ToDictionary(x => x.FullName, x =>
        {
            using var reader = new StreamReader(x.Open(), Encoding.Latin1);
            return reader.ReadToEnd();
        });
    }

    internal static async Task<List<(Guid Id, string Sha256)>> AttachmentsAsync(string connectionString)
    {
        var text = (string)(await InstallationFixture.ScalarAsync(connectionString, "SELECT string_agg(\"Id\" || ':' || \"Sha256\", ',') FROM \"Attachment\""))!;
        return text.Split(',').Select(x => x.Split(':')).Select(x => (Guid.Parse(x[0]), x[1])).ToList();
    }
}
