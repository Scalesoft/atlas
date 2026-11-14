using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NHibernate;
using Npgsql;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Backups;
using Atlas.Server.Features.Backups.Installation;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Features.Contacts.LinkRun;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.YouTrack.Commands;
using Atlas.Server.Platform.Backup;
using Atlas.Server.Platform.Jobs;
using Atlas.Server.Tests.Attachments;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Backups;

/// <summary>
/// An installation with something in every kind of data the format v7 must carry: pages with history, publications, a child, the
/// trash, tags, a decoration, attachments (two with the same content), live collaboration state, accounts with a password, a group,
/// grants, a vault with its file and password, and an integration secret. And an empty installation to restore it into.
/// </summary>
internal sealed class InstallationFixture : IAsyncDisposable
{
    public const string YouTrackToken = "perm:youtrack-token-of-the-source";
    public const string KdbxPassword = "kdbx-master-password";
    public static readonly byte[] KdbxFile = Encoding.UTF8.GetBytes("KDBX file bytes");

    private InstallationFixture(AttachmentFixture pages, string root, InstallationServices installation)
    {
        Pages = pages;
        Root = root;
        Installation = installation;
    }

    public AttachmentFixture Pages { get; }
    public string Root { get; }
    public InstallationServices Installation { get; }
    public string ConnectionString => Pages.Host.ConnectionString;
    public Guid PageId { get; private set; }
    public Guid VaultId { get; private set; }
    public Guid LocationId { get; private set; }
    public Guid BoardId { get; private set; }
    public Guid ContactId { get; } = Guid.NewGuid();
    public Guid CoverFileId { get; } = Guid.NewGuid();
    public static readonly byte[] CoverFile = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGD4DwABBAEAwS2OUAAAAABJRU5ErkJggg==");

    /// <summary>The same content over the pages and installation services of a Git backup test, which owns and disposes them.</summary>
    public static InstallationFixture Over(GitBackupTests.GitFixture git) => new(git.Pages, git.Root, git.Installation);

    /// <param name="outbox">A source host whose outbox delivers (AtlasTestHost.CreateAsync).</param>
    public static async Task<InstallationFixture> CreateAsync(bool outbox = false)
    {
        var pages = await AttachmentFixture.CreateIsolatedAsync(outbox);
        var root = Path.Combine(Path.GetTempPath(), "atlas-installation-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new InstallationFixture(pages, root, new InstallationServices(pages.Host, pages.Files, Path.Combine(root, "source")));
    }

    public static string Attachments(params string[] urls) => System.Text.Json.JsonSerializer.Serialize(new
    {
        format = "atlas-rich",
        version = 1,
        plugins = new[] { new { id = "attachments", version = 1 } },
        document = new { type = "doc", content = urls.Select(url => new { type = "attachment", attrs = new { data = new { version = 1, name = "file", url } } }).ToArray() }
    });

    public async Task<BackupKey> GenerateKeyAsync()
    {
        using var session = Pages.Host.OpenSession();
        using var transaction = session.BeginTransaction();
        var key = Installation.Keys.Replace(await session.GetAsync<BackupSettings>(1, LockMode.Upgrade));
        await transaction.CommitAsync();
        return key;
    }

    public async Task PopulateAsync()
    {
        var host = Pages.Host;
        var created = await Pages.As<PageDto>(Pages.Writer, new CreatePage(new CreatePageRequest(Pages.SpaceId, null, "Provoz", AttachmentFixture.Rich("first"), "first")));
        PageId = created.Id;
        await Pages.As<PageDto>(Pages.Writer, new PublishPage(created.Id, new PublishPageRequest(created.Version)));
        var notes = await Pages.UploadAsync(Pages.Writer, created.Id, "notes.txt", new MemoryStream("vpn notes"u8.ToArray()));
        var copy = await Pages.UploadAsync(Pages.OtherWriter, created.Id, "copy.txt", new MemoryStream("vpn notes"u8.ToArray()));
        var saved = await Pages.As<PageDto>(Pages.OtherWriter, new SavePage(created.Id, new SavePageRequest("Provoz",
            Attachments(notes.DownloadUrl, copy.DownloadUrl), "See [notes](" + notes.DownloadUrl + ").", created.Version)));
        await Pages.As<PageDto>(Pages.Writer, new PublishPage(created.Id, new PublishPageRequest(saved.Version)));
        await Pages.As<PageDto>(Pages.Writer, new CreatePage(new CreatePageRequest(Pages.SpaceId, created.Id, "Dítě", AttachmentFixture.Rich("child"), "child")));
        // PER1-6: a published page that mentions an account and holds a requirement, so the restore rebuilds non-empty derived tables.
        var mention = $$$$"""{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"{{{{Pages.Reader.DisplayName}}}}","kind":"internal","internalUser":{"id":"{{{{Pages.Reader.Id:D}}}}","name":"{{{{Pages.Reader.DisplayName}}}}"}}]}}}""";
        var requirement = """{"type":"requirement","attrs":{"id":"6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f7","key":"vpn","direction":"internal","state":"planned","date":"","party":"","where":"","requires":""},"content":[{"type":"paragraph","content":[{"type":"text","text":"VPN brána"}]}]}""";
        var plan = await Pages.As<PageDto>(Pages.Writer, new CreatePage(new CreatePageRequest(Pages.SpaceId, null, "Plán",
            """{"format":"atlas-rich","version":1,"plugins":[{"id":"contacts","version":2},{"id":"atlas-requirements","version":1}],"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Pro "},"""
            + mention + "]}," + requirement + "]}}", "Pro čtenáře")));
        await Pages.As<PageDto>(Pages.Writer, new PublishPage(plan.Id, new PublishPageRequest(plan.Version)));
        var old = await Pages.As<PageDto>(Pages.Writer, new CreatePage(new CreatePageRequest(Pages.SpaceId, null, "Staré", AttachmentFixture.Rich("old"), "old")));
        long tree;
        using (var session = host.OpenSession())
        {
            tree = await TreeVersion.ReadAsync(session, Pages.SpaceId, default);
        }
        await Pages.As<OrganizationChange>(Pages.Writer, new TrashPage(old.Id, new TrashPageRequest(false, tree, old.Version)));
        await host.SeedGroupAsync("Provozáci", Pages.Writer, Pages.Reader);
        LocationId = Guid.NewGuid();
        VaultId = Guid.NewGuid();
        BoardId = Guid.NewGuid();
        var youTrack = Installation.Protection.CreateProtector(SaveYouTrackSettingsHandler.TokenPurpose).Protect(YouTrackToken);
        var kdbx = Installation.Protection.CreateProtector(VaultProtection.KdbxPasswordPurpose).Protect(KdbxPassword);
        await ExecuteAsync(ConnectionString, """
            INSERT INTO "BuiltinCredential"("UserId","PasswordHash") VALUES (@writer, 'AQAAAAIAAYagAAAAE-hash-of-the-writer');
            INSERT INTO "Tag"("Id","Name","NormalizedName","CreatedAt","CreatedById") VALUES (@tag, 'Síť', 'sit', now(), @writer);
            INSERT INTO "PageTag"("Id","PageId","TagId","CreatedById","CreatedAt","ActiveFrom") VALUES (gen_random_uuid(), @page, @tag, @writer, now(), now());
            INSERT INTO "PageDecoration"("PageId","RevisionId","Icon","Color") SELECT @page, "Id", 'server', 'teal' FROM "PageRevision" WHERE "PageId" = @page AND "ActiveTo" IS NULL;
            INSERT INTO "PageCollaboration"("PageId","Generation","BaseRevisionId","Sequence","SnapshotSequence")
              SELECT @page, gen_random_uuid(), "Id", 1, 0 FROM "PageRevision" WHERE "PageId" = @page AND "ActiveTo" IS NULL
              ON CONFLICT ("PageId") DO UPDATE SET "Sequence" = "PageCollaboration"."Sequence" + 1;
            -- 3.6-H origins: an agent's update names its personal API token or its assistant conversation, never both in one row.
            INSERT INTO "CollaborationUpdate"("PageId","Generation","Sequence","ClientUpdateId","UserId","ReceivedAt","Payload","Channel","ApiTokenId")
              SELECT "PageId", "Generation", "Sequence", gen_random_uuid(), @writer, now(), '\x0102ff00'::bytea, 'api', @apiToken FROM "PageCollaboration" WHERE "PageId" = @page;
            UPDATE "PageCollaboration" SET "Sequence" = "Sequence" + 1 WHERE "PageId" = @page;
            INSERT INTO "CollaborationUpdate"("PageId","Generation","Sequence","ClientUpdateId","UserId","ReceivedAt","Payload","Channel","AiConversationId")
              SELECT "PageId", "Generation", "Sequence", gen_random_uuid(), @writer, now(), '\x03'::bytea, 'assistant', gen_random_uuid() FROM "PageCollaboration" WHERE "PageId" = @page;
            INSERT INTO "YouTrackConfiguration"("Id","Enabled","BaseUrl","ProtectedToken","SpaceProjectsJson","ChangedById","ActiveFrom")
              VALUES (gen_random_uuid(), true, 'https://youtrack.example.test', @token, '{}', @writer, now());
            INSERT INTO "VaultLocation"("Id","Name","Kind","Root") VALUES (@location, 'Spravované', 'managed', '');
            INSERT INTO "Vault"("Id","SpaceId","Name","LocationId","FileName","ProtectedKdbxPassword","State","CreatedBy","CreatedAt")
              VALUES (@vault, @space, 'Provoz', @location, 'Provoz.kdbx', @kdbx, 'unlocked', @writer, now());
            INSERT INTO "VaultSecret"("Id","VaultId","EntryUuid","Title","GroupPath","AvailableFields","FirstSeenAt")
              VALUES (@secret, @vault, gen_random_uuid(), 'Root VPN', 'Síť', 15, now());
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","SettingsJson","ParentPageId","SortOrder")
              VALUES (@board, @space, 'Architektura', 1, now(), @writer, now(), false, '{"grid": true}', @page, 1024);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","Color","TargetResourceId","FromItemId","ToItemId","Version","IsDeleted","PropsJson","SearchText","ParentId") VALUES
              (@group, @board, 'group', 0, 0, 400, 300, 1, 'Síť', NULL, NULL, NULL, NULL, 1, false, '{}', 'Síť', NULL),
              (@note, @board, 'note', 10, 10, 100, 100, 2, 'VPN brána', 'yellow', NULL, NULL, NULL, 1, false, '{"shape": "round"}', 'VPN brána', @group),
              (gen_random_uuid(), @board, 'page', 200, 10, 150, 100, 3, NULL, NULL, @page, NULL, NULL, 1, false, '{}', NULL, @group),
              (gen_random_uuid(), @board, 'connector', 0, 0, 8, 8, 4, NULL, NULL, NULL, @note, @group, 1, false, '{"state": "done"}', NULL, NULL);
            INSERT INTO "BoardOperation"("BoardId","Sequence","OperationId","AuthorId","AppliedAt","ChangesJson")
              VALUES (@board, 1, gen_random_uuid(), @writer, now(), '[{"op": "add"}]');
            /* The board's envelope, which its comment references: bare SQL writes what the triggers did until 2026110930. */
            """ + DerivedRows.Sql + """
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data")
              VALUES (@contact, 'atlas.contact', 'wiki', NULL, NULL, 1, now(), @writer, now(), 'Jana Nováková', '{"email": "jana@example.test"}');
            INSERT INTO "ObjectChange"("ObjectId","Version","ChangeId","AuthorId","AppliedAt","PayloadJson")
              VALUES (@contact, 1, gen_random_uuid(), @writer, now(), '{"title": "Jana Nováková"}');
            INSERT INTO "ContactPhoto"("ContactId","MimeType","Sha256","Content")
              VALUES (@contact, 'image/png', repeat('a', 64), decode('89504e470d0a1a0a', 'hex'));
            INSERT INTO "TreeReference"("Id","SpaceId","ParentPageId","SortOrder","TargetId","Anchor","CreatedBy","CreatedAt")
              VALUES (gen_random_uuid(), @space, @page, 2048, @contact, 'kontakt', @writer, now());
            INSERT INTO "Comment"("Id","ObjectId","AuthorId","Body","Anchor","CreatedAt","EditedAt","DeletedAt","DeletedById") VALUES
              (@comment, @board, @writer, 'Zkontrolovat **VPN** bránu', CAST(@note AS text), now(), now(), NULL, NULL),
              (gen_random_uuid(), @page, @writer, '', NULL, now(), NULL, now(), @writer);
            INSERT INTO "CommentMention"("CommentId","UserId") VALUES (@comment, @writer);
            """ + Atlas.Server.Features.Comments.Persistence.CommentWrites.SearchRefreshSql("= @comment") + """
            -- Everything else a content table can hold, so that no column comes back unproven (every-column test): history rows
            -- closed by a later one, a space in the trash with its board and tree reference, an object in the trash, a page board with a purpose, a
            -- Projector catalog, an uploaded and a catalog cover, pins, favourites, a private resource, a revision and a
            -- publication through the API, live collaboration state with a refusal and a secret, and a Wiki.js import mapping.
            INSERT INTO "Workspace"("Id","Name","CreatedAt") VALUES (1, 'Atlas', now()) ON CONFLICT ("Id") DO NOTHING;
            INSERT INTO "ModuleOnboarding"("Id","WorkspaceId","ActiveFrom","ActiveTo","CompletedById")
              VALUES (gen_random_uuid(), 1, now() - interval '2 days', now() - interval '1 day', @writer);
            INSERT INTO "Space"("Id","Name","Icon","Color","TrashedAt","TrashedById","TrashBatchId")
              VALUES (@trashedSpace, 'Archiv', 'archive', 'slate', now(), @writer, @batch);
            -- A permanently deleted space with its page: tombstones the access history names, restored as tombstones (ARCH1-1).
            INSERT INTO "Space"("Id","Name","Icon","Color","TrashedAt","TrashedById","TrashBatchId","PurgedAt")
              VALUES (@purgedSpace, 'Smazaný', 'archive', 'slate', now(), @writer, @purgedBatch, now());
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","SortOrder","IsTrashed","TrashedAt","TrashedById","TrashBatchId","PurgedAt")
              VALUES (@purgedPage, @purgedSpace, @writer, 1, 1024, true, now(), @writer, @purgedBatch, now());
            -- A page created from a template (plan 3.1, template origin): the column comes back with the page from the repository alone.
            UPDATE "Page" SET "TemplatePageId" = @page WHERE "Id" = @purgedPage;
            -- The page's own publication (3.6-I) comes back with the page.
            UPDATE "Page" SET "PublicationMode" = 'Live' WHERE "Id" = @purgedPage;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              VALUES (gen_random_uuid(), @purgedPage, 1, '', '{"type":"doc","content":[]}', '', @writer, now(), 'web');
            INSERT INTO "SpaceMetadataRevision"("Id","SpaceId","Name","Icon","Color","Description","ChangedBy","ActiveFrom","ActiveTo","PinPolicy","CommentsEnabled") VALUES
              (gen_random_uuid(), @trashedSpace, 'Archív', 'archive', 'slate', 'Staré věci', @writer, now() - interval '2 days', now() - interval '1 day', 'Default', true);
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","TrashedAt","TrashedById","TrashBatchId","SortOrder")
              VALUES (gen_random_uuid(), @trashedSpace, 'Stará tabule', 0, now(), @writer, now(), true, now(), @writer, @batch, 1024);
            UPDATE "Board" SET "Purpose" = 'projector.deployments' WHERE "Id" = @board;
            INSERT INTO "TreeReference"("Id","SpaceId","ParentPageId","SortOrder","TargetId","Anchor","CreatedBy","CreatedAt","IsTrashed","TrashedAt","TrashedById","TrashBatchId")
              VALUES (gen_random_uuid(), @trashedSpace, NULL, 1024, @contact, NULL, @writer, now(), true, now(), @writer, @batch);
            INSERT INTO "ProjectorCatalogPage"("PageId","MarkedById","MarkedAt") VALUES (@page, @writer, now());
            INSERT INTO "SpaceCover"("Id","SpaceId","FileId","MimeType","Size","Sha256","Tinted","ChangedBy","ActiveFrom","ActiveTo","Source","CatalogKey") VALUES
              (gen_random_uuid(), @space, @coverFile, 'image/png', @coverSize, @coverSha, true, @writer, now() - interval '2 days', now() - interval '1 day', 'Upload', NULL),
              (gen_random_uuid(), @space, NULL, NULL, NULL, NULL, false, @writer, now() - interval '1 day', NULL, 'Catalog', 'mountains');
            INSERT INTO "SpacePin"("Id","UserId","SpaceId","Pinned","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(), @writer, @space, false, now() - interval '2 days', now() - interval '1 day'),
              (gen_random_uuid(), @writer, @space, true, now() - interval '1 day', NULL);
            INSERT INTO "SpaceTrashCleanupSetting"("Id","SpaceId","Automatic","Days","ChangedById","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(), @space, false, 60, @writer, now() - interval '2 days', now() - interval '1 day'),
              (gen_random_uuid(), @space, true, 90, @writer, now() - interval '1 day', NULL);
            INSERT INTO "SpaceExternalSharingPolicy"("Id","SpaceId","Enabled","ChangedById","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(), @space, false, @writer, now() - interval '2 days', now() - interval '1 day'),
              (gen_random_uuid(), @space, true, @writer, now() - interval '1 day', NULL);
            INSERT INTO "PageFavorite"("Id","UserId","PageId","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(), @writer, @page, now() - interval '2 days', now() - interval '1 day'),
              (gen_random_uuid(), @writer, @page, now() - interval '1 day', NULL);
            INSERT INTO "PageAccessModeRevision"("Id","PageId","OwnAccess","ChangedBy","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(), @page, true, @writer, now() - interval '2 days', now() - interval '1 day');
            INSERT INTO "PageTag"("Id","PageId","TagId","CreatedById","CreatedAt","ActiveFrom","ActiveTo")
              VALUES (gen_random_uuid(), @page, @tag, @writer, now() - interval '2 days', now() - interval '2 days', now() - interval '1 day');
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data")
              VALUES (gen_random_uuid(), 'atlas.library-link', 'private', NULL, @writer, 1, now(), @writer, now(), 'Moje odkazy', '{"url": "https://example.test"}');
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","TrashedAt","TrashedById","Title","Data")
              VALUES (gen_random_uuid(), 'atlas.library-link', 'private', NULL, @writer, 1, now(), @writer, now(), now(), @writer, 'Staré odkazy', '{"url": "https://example.test/old"}');
            -- A private note shared with the reader for reading (spec 2026-10-29).
            INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","OwnerId","Version","CreatedAt","CreatedBy","UpdatedAt","Title","Data")
              VALUES (@personalNote, 'atlas.note', 'private', NULL, @writer, 1, now(), @writer, now(), 'Moje nápady', '{"title": "Moje nápady", "text": "- první"}');
            INSERT INTO "ResourceGrant"("Id","ObjectId","UserId","Role","ActiveFrom") VALUES (gen_random_uuid(), @personalNote, @reader, 1, now());
            UPDATE "Attachment" SET "ActiveTo" = "ActiveFrom" + interval '1 minute' WHERE "Id" = @removedAttachment;
            INSERT INTO "PersonalApiToken"("Id","UserId","Name","TokenHash","Prefix","SecurityStamp","ActiveFrom","ExpiresAt")
              VALUES (@apiToken, @writer, 'CI', repeat('AB', 32), 'atl_ci', 'stamp', now() - interval '3 days', now() + interval '30 days');
            UPDATE "PageRevision" SET "Channel" = 'api', "ApiTokenId" = @apiToken, "Label" = 'Hotovo', "AiConversationId" = gen_random_uuid() WHERE "PageId" = @page AND "Version" = 1;
            -- A version that ended where it started (3.6, migration 2026111020) names the listed revision it equals.
            UPDATE "PageRevision" SET "SameAsRevisionId" = (SELECT "Id" FROM "PageRevision" WHERE "PageId" = @page AND "Version" = 1)
              WHERE "PageId" = @page AND "ActiveTo" IS NULL AND "Version" > 1;
            UPDATE "PagePublication" SET "Channel" = 'api', "ApiTokenId" = @apiToken, "ChangeSummary" = 'Doplněné poznámky'
              WHERE "Id" = (SELECT "Id" FROM "PagePublication" WHERE "PageId" = @page ORDER BY "ActiveFrom" DESC LIMIT 1);
            UPDATE "PageCollaboration" SET "SchemaJson" = '{"nodes": []}', "State" = '\x0102'::bytea, "StateSha256" = encode(sha256('\x0102'::bytea), 'hex'),
              "ProjectionJson" = '{"type": "doc"}', "LastSavedSequence" = 0,
              "RefusedSequence" = "Sequence", "Refusal" = 'schema' WHERE "PageId" = @page;
            INSERT INTO "CollaborationSecretReference"("PageId","Generation","SecretId") SELECT "PageId", "Generation", @secret FROM "PageCollaboration" WHERE "PageId" = @page;
            INSERT INTO "WikiImportSource"("Id","SourceKey") VALUES (@importSource, 'wikijs:atlas');
            INSERT INTO "WikiImportDestination"("Id","SourceId","SpaceId","ActiveFrom","ActiveTo") VALUES
              (gen_random_uuid(), @importSource, @trashedSpace, now() - interval '2 days', now() - interval '1 day'),
              (gen_random_uuid(), @importSource, @space, now() - interval '1 day', NULL);
            INSERT INTO "WikiImportPage"("Id","SourceId","SourcePath","PageId") VALUES (@importPage, @importSource, 'docs/provoz', @page);
            INSERT INTO "WikiImportCheckpoint"("Id","ImportPageId","RevisionId","SourceSha256","ActiveFrom","ActiveTo")
              SELECT gen_random_uuid(), @importPage, "Id", repeat('a', 64), now() - interval '2 days', now() - interval '1 day' FROM "PageRevision" WHERE "PageId" = @page AND "Version" = 1
              UNION ALL SELECT gen_random_uuid(), @importPage, "Id", repeat('b', 64), now() - interval '1 day', NULL FROM "PageRevision" WHERE "PageId" = @page AND "ActiveTo" IS NULL;
            """ + DerivedRows.Sql, ("writer", Pages.Writer.Id), ("tag", Guid.NewGuid()), ("page", created.Id), ("token", youTrack), ("location", LocationId),
            ("vault", VaultId), ("space", Pages.SpaceId), ("kdbx", kdbx), ("board", BoardId), ("group", Guid.NewGuid()), ("note", Guid.NewGuid()),
            ("contact", ContactId), ("comment", Guid.NewGuid()), ("secret", Guid.NewGuid()), ("trashedSpace", Guid.NewGuid()), ("batch", Guid.NewGuid()),
            ("purgedSpace", Guid.NewGuid()), ("purgedPage", Guid.NewGuid()), ("purgedBatch", Guid.NewGuid()),
            ("coverFile", CoverFileId), ("coverSize", (long)CoverFile.Length), ("coverSha", Sha256Of(CoverFile)),
            ("removedAttachment", copy.Id), ("apiToken", Guid.NewGuid()), ("importSource", Guid.NewGuid()), ("importPage", Guid.NewGuid()),
            ("personalNote", Guid.NewGuid()), ("reader", Pages.Reader.Id));
        // The rows above are bare SQL, which announces nothing (no outbox trigger since 2026110420): it asks for the catalog's rebuild,
        // so the source's derived tables hold the directory contact as a real installation's would.
        await Pages.Host.Services.GetRequiredService<Atlas.Server.Platform.Jobs.IJobRunner>().RunToEndAsync(Atlas.Server.Features.Entities.EntityCatalogRebuildJob.JobId, "Seed");
        // No trigger writes the search documents since 2026110935: bare SQL asks for their rebuild too.
        await Pages.Host.Services.GetRequiredService<Atlas.Server.Platform.Jobs.IJobRunner>().RunToEndAsync(Atlas.Server.Features.Search.SearchDocumentsRebuildJob.JobId, "Seed");
        // GAP2-3: a page never published whose draft names the directory contact; the restore rebuilds its mention too.
        await Pages.As<PageDto>(Pages.Writer, new CreatePage(new CreatePageRequest(Pages.SpaceId, null, "Koncept",
            """{"format":"atlas-rich","version":1,"plugins":[{"id":"contacts","version":2}],"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Volat "},"""
            + $$$$"""{"type":"contactInline","attrs":{"data":{"version":1,"contacts":[{"name":"Jana Nováková","objectId":"{{{{ContactId:D}}}}"}]}}}]}]}}""", "Volat Jana Nováková")));
        await PopulateDeploymentAsync();
        var cover = Pages.Files.PathFor(CoverFileId);
        Directory.CreateDirectory(Path.GetDirectoryName(cover)!);
        await File.WriteAllBytesAsync(cover, CoverFile);
        var file = Path.Combine(Installation.Vaults.Managed(LocationId), "Provoz.kdbx");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, KdbxFile);
    }

    /// <summary>A 3.4 deployment page: its requirement with a state label, a service and history lines in its note under atlas-requirements 2,
    /// the overviews of all three kinds.</summary>
    public const string DeploymentContent = """
        {"format":"atlas-rich","version":1,"plugins":[{"id":"atlas-requirements","version":2},{"id":"heading-anchors","version":1}],"document":{"type":"doc","content":[
        {"type":"overview","attrs":{"id":"7a1d2c3b-4a5e-4f60-8a71-92b3c4d5e601","kind":"requirements"}},
        {"type":"overview","attrs":{"id":"7a1d2c3b-4a5e-4f60-8a71-92b3c4d5e602","kind":"contacts"}},
        {"type":"overview","attrs":{"id":"7a1d2c3b-4a5e-4f60-8a71-92b3c4d5e603","kind":"issues"}},
        {"type":"heading","attrs":{"level":2,"headingId":"portal-pacienta"},"content":[{"type":"text","text":"Portál pacienta"}]},
        {"type":"requirement","attrs":{"id":"7a1d2c3b-4a5e-4f60-8a71-92b3c4d5e604","key":"nia.cert","direction":"inbound","state":"waiting_for_input","stateLabel":"čeká na podpis","date":"2026-11-10","party":"IT KBN","where":"","system":"NIA","requires":""},"content":[
          {"type":"paragraph","content":[{"type":"text","text":"Certifikát NIA"}]},
          {"type":"bulletList","content":[
            {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"1. 11. 2026 23:40 — Odeslána žádost o certifikát"}]}]},
            {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"30. 10. 2026 — Domluven termín"}]}]}]}]}]}}
        """;

    public Guid DeploymentPageId { get; private set; }
    public Guid DeploymentBoardId { get; private set; }

    /// <summary>The 3.4 content: the published deployment page, and the board „Poskládat tabuli“ makes under it in the space tree (Purpose
    /// null) with a product and a service card, their labelled connector and the board's requirements overview. The source host has no
    /// boards module, so the rows go in by SQL with the props the board takes (BoardProps.Create).</summary>
    private async Task PopulateDeploymentAsync()
    {
        var page = await Pages.As<PageDto>(Pages.Writer, new CreatePage(new CreatePageRequest(Pages.SpaceId, null, "Nasazení", DeploymentContent, null)));
        await Pages.As<PageDto>(Pages.Writer, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        DeploymentPageId = page.Id;
        DeploymentBoardId = Guid.NewGuid();
        static string Props(string kind, JsonObject props) => BoardProps.Create(kind, props) ?? throw new InvalidOperationException($"Invalid {kind} props {props}");
        await ExecuteAsync(ConnectionString, """
            INSERT INTO "Board"("Id","SpaceId","Title","Sequence","CreatedAt","CreatedBy","UpdatedAt","IsTrashed","ParentPageId","SortOrder")
              VALUES (@board, @space, 'Tabule nasazení', 1, now(), @writer, now(), false, @page, 4096);
            INSERT INTO "BoardItem"("Id","BoardId","Kind","X","Y","Width","Height","Z","Text","TargetResourceId","FromItemId","ToItemId","Version","IsDeleted","PropsJson") VALUES
              (@product, @board, 'page', 0, 0, 280, 160, 1, NULL, @page, NULL, NULL, 1, false, CAST(@productProps AS jsonb)),
              (@service, @board, 'page', 520, 0, 240, 96, 2, NULL, @page, NULL, NULL, 1, false, CAST(@serviceProps AS jsonb)),
              (gen_random_uuid(), @board, 'connector', 0, 0, 8, 8, 3, 'NIA · 1 pož.', NULL, @product, @service, 1, false, CAST(@connectorProps AS jsonb)),
              (gen_random_uuid(), @board, 'block', 0, 240, 520, 200, 4, NULL, NULL, NULL, NULL, 1, false, CAST(@blockProps AS jsonb));
            """, ("board", DeploymentBoardId), ("space", Pages.SpaceId), ("writer", Pages.Writer.Id), ("page", page.Id), ("product", Guid.NewGuid()), ("service", Guid.NewGuid()),
            ("productProps", Props("page", new JsonObject { ["display"] = "product", ["anchor"] = "portal-pacienta" })),
            ("serviceProps", Props("page", new JsonObject { ["display"] = "service", ["anchor"] = "nia" })),
            ("connectorProps", Props("connector", new JsonObject { ["route"] = "curved", ["arrowStart"] = "none", ["arrowEnd"] = "arrow", ["dash"] = "solid",
                ["fromSide"] = "right", ["toSide"] = "left", ["state"] = "waiting" })),
            ("blockProps", Props("block", new JsonObject { ["block"] = new JsonObject { ["type"] = "overview", ["attrs"] = new JsonObject { ["kind"] = "requirements" } } })));
    }

    /// <summary>The 3.4 content of <see cref="PopulateDeploymentAsync"/> came back unchanged: the page's rich JSON of every revision and
    /// the props of the board's items, which the board still takes as valid (BoardProps).</summary>
    public async Task AssertDeploymentRestoredAsync(string target)
    {
        const string revisions = """SELECT string_agg("Version" || ':' || "ContentJson", E'\n' ORDER BY "Version") FROM "PageRevision" WHERE "PageId" = @id""";
        const string items = """SELECT string_agg("Kind" || ':' || coalesce("Text", '') || ':' || "PropsJson"::text, E'\n' ORDER BY "Z") FROM "BoardItem" WHERE "BoardId" = @id AND NOT "IsDeleted" """;
        Assert.Equal(await ScalarAsync(ConnectionString, revisions, ("id", DeploymentPageId)), await ScalarAsync(target, revisions, ("id", DeploymentPageId)));
        var restored = (string)(await ScalarAsync(target, items, ("id", DeploymentBoardId)))!;
        Assert.Equal(await ScalarAsync(ConnectionString, items, ("id", DeploymentBoardId)), restored);
        var lines = restored.Split('\n');
        Assert.Equal(["page", "page", "connector", "block"], lines.Select(x => x[..x.IndexOf(':')]));
        Assert.All(lines, line =>
        {
            var kind = line[..line.IndexOf(':')];
            Assert.NotNull(BoardProps.Create(kind, JsonNode.Parse(line[(line.IndexOf(":{", StringComparison.Ordinal) + 1)..])!.AsObject()));
        });
        Assert.Equal(DeploymentPageId, await ScalarAsync(target, """SELECT "ParentPageId" FROM "Board" WHERE "Id" = @id AND "Purpose" IS NULL""", ("id", DeploymentBoardId)));
    }

    /// <summary>A complete ZIP of the installation, kept under the fixture's folder.</summary>
    public async Task<string> ArchiveAsync(string name = "complete.zip")
    {
        using var archive = await Installation.Archive.CreateAsync(default);
        var path = Path.Combine(Root, name);
        File.Copy(archive.Path, path, true);
        return path;
    }

    /// <summary>An installation as after the setup wizard: an administrator with a starter space and the contact the setup's
    /// contacts.accounts job gives the account (R4, 3.5: the restore replaces it); nothing else.</summary>
    public Task<Target> CreateTargetAsync(bool outbox = false) => CreateTargetAsync(Root, outbox);

    public static async Task<Target> CreateTargetAsync(string root, bool outbox = false)
    {
        var storage = new AttachmentTests.Storage();
        // Own host: a restore of the whole installation needs an empty one (InstallationRestore refuses any other) and replaces every table.
        var host = await AtlasTestHost.CreateAsync("atlas_restore_target_", services => services.AddSingleton(storage.Files), modules: TestModules.AllEnabled,
            outbox: outbox);
        var restorer = await host.SeedUserAsync("restorer", isWikiAdmin: true);
        await host.SeedSpaceAsync("Začínáme", (restorer, SpaceRole.Admin));
        await host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(AccountContactsJob.JobId, "Zakladatel wiki");
        Assert.Equal(1L, await ScalarAsync(host.ConnectionString, AccountContactCount, ("id", restorer.Id)));
        return new Target(host, storage, restorer, new InstallationServices(host, storage.Files, Path.Combine(root, "target-" + Guid.NewGuid().ToString("N")[..8])));
    }

    /// <summary>The live contacts of the account @id (AccountContacts.OwnContact).</summary>
    public const string AccountContactCount = """
        SELECT count(*) FROM "ContentResource" c WHERE c."TypeId" = 'atlas.contact' AND c."TrashedAt" IS NULL AND lower(c."Data"->>'directoryId') = CAST(@id AS text)
        """;

    /// <summary>One value per table that changes with any value of any row; Data Protection columns are compared decrypted instead.</summary>
    /// <param name="ignored">Further columns left out of every table, for a restore that changes them on purpose.</param>
    public static async Task<Dictionary<string, string>> FingerprintsAsync(string connectionString, IEnumerable<BackupTable> tables, params string[] ignored)
    {
        var result = new Dictionary<string, string>();
        foreach (var table in tables)
        {
            var excluded = string.Join(",", (table.Protected?.Keys ?? []).Concat(ignored).Select(x => "'" + x + "'"));
            result[table.Name] = (string)(await ScalarAsync(connectionString,
                $"SELECT count(*) || ':' || coalesce(md5(string_agg(x, E'\\n' ORDER BY x)), '') FROM (SELECT CAST(to_jsonb(t) - ARRAY[{excluded}]::text[] AS text) x FROM \"{table.Name}\" t) s"))!;
        }
        return result;
    }

    /// <summary>The modules that hold what people write (owner: the content must come back whole, boards above all).</summary>
    public static readonly string[] ContentModules = ["platform", "pages", "attachments", "boards", "objects", "comments", "projector"];

    /// <summary>"Table" for an empty table, "Table.Column" for a column that is NULL in every row: what a round trip cannot prove.</summary>
    public static async Task<List<string>> UnfilledAsync(string connectionString, IEnumerable<BackupTable> tables)
    {
        var unfilled = new List<string>();
        foreach (var table in tables)
        {
            var columns = new List<string>();
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT attname FROM pg_attribute WHERE attrelid = to_regclass(@t) AND attnum > 0 AND NOT attisdropped ORDER BY attnum", connection);
                command.Parameters.AddWithValue("t", "\"" + table.Name + "\"");
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
            }
            var counts = string.Join(" || ',' || ", columns.Select(c => $"count(\"{c}\")"));
            var values = ((string)(await ScalarAsync(connectionString, $"SELECT count(*) || ',' || {counts} FROM \"{table.Name}\""))!).Split(',');
            if (values[0] == "0")
                unfilled.Add(table.Name);
            else
                unfilled.AddRange(columns.Where((_, i) => values[i + 1] == "0").Select(c => table.Name + "." + c));
        }
        return unfilled;
    }

    public static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteScalarAsync();
    }

    public static Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters) => ScalarAsync(connectionString, sql, parameters);

    public static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public async ValueTask DisposeAsync()
    {
        await Pages.DisposeAsync();
        BackupDirectory.Delete(Root);
    }

    internal sealed class Target(AtlasTestHost host, AttachmentTests.Storage storage, UserAccount restorer, InstallationServices installation) : IAsyncDisposable
    {
        public AtlasTestHost Host { get; } = host;
        public AttachmentFiles Files => storage.Files;
        public UserAccount Restorer { get; } = restorer;
        public InstallationServices Installation { get; } = installation;

        public async Task<InstallationRestoreResult> RestoreAsync(string zip, string? key)
        {
            await using var input = File.OpenRead(zip);
            using var backup = await BackupDirectory.ExtractAsync(input, key, default);
            return await RestoreAsync(backup);
        }

        public async Task<InstallationRestoreResult> RestoreAsync(BackupDirectory backup)
        {
            var preview = await Installation.Restore.PreviewAsync(backup, default);
            Assert.True(preview.Problems.Count == 0, string.Join("\n", preview.Problems));
            return await Installation.Restore.RestoreAsync(backup, Restorer.Id, default);
        }

        /// <summary>The restart after a restore runs contacts.accounts: then every active account has exactly one contact of its own.</summary>
        public async Task AssertAccountsHaveTheirContactAsync()
        {
            await Host.Services.GetRequiredService<IJobRunner>().RunToEndAsync(AccountContactsJob.JobId, "Restart po obnově");
            Assert.Equal("", await ScalarAsync(Host.ConnectionString, $"""
                SELECT coalesce(string_agg(a."Username", ','), '') FROM "UserAccount" a
                WHERE a."IsActive" AND ({AccountContactCount.Replace("@id", "a.\"Id\"", StringComparison.Ordinal)}) <> 1
                """));
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            storage.Dispose();
        }
    }
}
