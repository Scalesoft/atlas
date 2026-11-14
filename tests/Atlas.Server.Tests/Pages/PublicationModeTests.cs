using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Server.Features.Collaboration.Queries;
using Atlas.Server.Features.ExternalSharing.Domain;
using Atlas.Server.Features.ExternalSharing.Commands;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.SpaceSettings.Commands;
using Atlas.Server.Features.SpaceSettings.Contracts;
using Atlas.Server.Features.SpaceSettings.Queries;
using Atlas.Server.Tests.Events;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Projector;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Pages;

/// <summary>3.6-I „Publikace“: Ručně, Automaticky, Živě, inherited from the space.</summary>
public sealed class PublicationModeTests
{
    /// <summary>Migration 2026110650 over an installation: every space and page keeps today's behaviour (Manual, inherited), and a change
    /// of the space's default is versioned with the other space settings.</summary>
    [PostgreSqlFact]
    public async Task The_migration_keeps_every_existing_space_and_page_manual()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_publication_mode_migration_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110650);
        const string Owner = "00000000-0000-0000-0000-000000000001", Space = "00000000-0000-0000-0000-00000000000a";
        await ScalarAsync(schema.ConnectionString, $"""
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES('{Owner}','writer','Writer',false,true,'stamp');
            INSERT INTO "Space"("Id","Name","Icon","Color") VALUES('{Space}','Space','book-open','blue');
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version","SortOrder") VALUES('00000000-0000-0000-0000-0000000000a1','{Space}','{Owner}',1,1024);
            """);

        // Up to 2026110930, which moved what the triggers tested here did to C# (ContentRowEffects).
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110930, from: 2026110650);

        Assert.Equal("Manual|-|Manual|1", await ScalarAsync(schema.ConnectionString, """
            SELECT (SELECT "PublicationMode" FROM "Space") || '|' || (SELECT coalesce("PublicationMode",'-') FROM "Page") || '|'
              || (SELECT "PublicationMode" FROM "SpaceMetadataRevision" WHERE "ActiveTo" IS NULL) || '|' || (SELECT count(*) FROM "SpaceMetadataRevision")
            """));
        await ScalarAsync(schema.ConnectionString, """UPDATE "Space" SET "PublicationMode"='Live'""");
        Assert.Equal("Live|2", await ScalarAsync(schema.ConnectionString, """
            SELECT (SELECT "PublicationMode" FROM "SpaceMetadataRevision" WHERE "ActiveTo" IS NULL) || '|' || (SELECT count(*) FROM "SpaceMetadataRevision")
            """));
        await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(schema.ConnectionString, """UPDATE "Page" SET "PublicationMode"='Sometimes'"""));
    }

    [PostgreSqlFact]
    public async Task A_page_inherits_its_space_default_and_its_editors_may_override_it()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.PublishedAsync("Postup");
        Assert.Equal((null, "Manual", "Manual"), Modes(await f.As<PagePublicationState>(f.Reader, new GetPagePublication(page))));

        // The space default is the space administrator's, with the other space settings.
        var settings = await f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings(f.SpaceId));
        await Assert.ThrowsAsync<ApiException>(() => f.As<SpaceSettingsDto>(f.Writer, new SaveSpaceSettings(f.SpaceId, Request(settings, "Automatic"))));
        Assert.Equal("Automatic", (await f.As<SpaceSettingsDto>(f.Admin, new SaveSpaceSettings(f.SpaceId, Request(settings, "Automatic")))).PublicationMode);
        Assert.Equal((null, "Automatic", "Automatic"), Modes(await f.As<PagePublicationState>(f.Writer, new GetPagePublication(page))));

        // The page's own value is anyone's who may edit the page; null inherits again.
        Assert.Equal(("Live", "Automatic", "Live"), Modes(await f.As<PagePublicationState>(f.Writer, new SetPagePublication(page, new("Live")))));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<PagePublicationState>(f.Reader, new SetPagePublication(page, new("Manual"))))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.As<PagePublicationState>(f.Writer, new SetPagePublication(page, new("Weekly"))))).Status);
        Assert.Equal((null, "Automatic", "Automatic"), Modes(await f.As<PagePublicationState>(f.Writer, new SetPagePublication(page, new(null)))));
    }

    [PostgreSqlFact]
    public async Task A_version_closed_by_the_pause_is_published_automatically_only_within_the_publication_rules()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Host.ExecuteAsync("""UPDATE "Space" SET "PublicationMode"='Automatic' WHERE "Id"=:id""", f.SpaceId);

        // A page nobody published yet stays private: automatic publication never shares it.
        var draft = await f.CreateAsync("Koncept");
        await f.CloseAsync(draft, await f.SaveAsync(draft, "Koncept 2", 1));
        Assert.Null((await f.As<PagePublicationState>(f.Writer, new GetPagePublication(draft))).PublishedVersion);

        var page = await f.PublishedAsync("Postup");
        var second = await f.SaveAsync(page, "Postup 2", 1);
        // Only the pause publishes: a version that its publication, a name or the day closed is no inactivity.
        await f.CloseAsync(page, second, "published");
        Assert.Equal(1, (await f.As<PagePublicationState>(f.Writer, new GetPagePublication(page))).PublishedVersion);
        await f.CloseAsync(page, second);
        await f.CloseAsync(page, second);
        var state = await f.As<PagePublicationState>(f.Reader, new GetPagePublication(page));
        Assert.Equal((2, true, false, "Writer"), (state.PublishedVersion, state.PublishedAutomatically, state.UnpublishedChanges, state.PublishedBy));
        Assert.Equal("Postup 2", (await f.As<PageDto>(f.Reader, new GetPage(page))).Title);
        Assert.Equal(2L, await f.ScalarAsync("""SELECT count(*) FROM "PagePublication" WHERE "PageId"=:id""", page));

        // A page's own Manual wins over the space's Automatic.
        var manual = await f.PublishedAsync("Ručně");
        await f.As<PagePublicationState>(f.Writer, new SetPagePublication(manual, new("Manual")));
        await f.CloseAsync(manual, await f.SaveAsync(manual, "Ručně 2", 1));
        Assert.Equal(1, (await f.As<PagePublicationState>(f.Writer, new GetPagePublication(manual))).PublishedVersion);

        // The version's author must still be allowed to publish it, as a publication by hand requires.
        var revoked = await f.PublishedAsync("Odebráno");
        var saved = await f.SaveAsync(revoked, "Odebráno 2", 1);
        await f.Host.ExecuteAsync("""UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "UserId"=:id AND "ActiveTo" IS NULL""", f.Writer.Id);
        await f.CloseAsync(revoked, saved);
        Assert.Equal(1, (await f.As<PagePublicationState>(f.Admin, new GetPagePublication(revoked))).PublishedVersion);
    }

    [PostgreSqlFact]
    public async Task A_live_page_gives_its_readers_the_latest_save_and_a_read_only_join()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.PublishedAsync("Postup");
        await f.SaveAsync(page, "Postup 2", 1);
        var session = await f.As<WikiCollaborationSession>(f.Writer, new OpenCollaboration(page));
        Assert.Equal(("Postup", false), Read(await f.As<PageDto>(f.Reader, new GetPage(page))));
        Assert.False((await f.Host.InvokeAsync<CollaboratorAuthorization>(new AuthorizeCollaborator(page, session.Generation, f.Reader.Id))).CanRead);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<WikiCollaborationSession>(f.Reader, new WatchCollaboration(page)))).Status);

        await f.As<PagePublicationState>(f.Writer, new SetPagePublication(page, new("Live")));

        Assert.Equal(("Postup 2", true), Read(await f.As<PageDto>(f.Reader, new GetPage(page))));
        var reader = await f.Host.InvokeAsync<CollaboratorAuthorization>(new AuthorizeCollaborator(page, session.Generation, f.Reader.Id));
        Assert.Equal((true, false), (reader.CanRead, reader.CanWrite));
        Assert.True((await f.Host.InvokeAsync<CollaboratorAuthorization>(new AuthorizeCollaborator(page, session.Generation, f.Writer.Id))).CanWrite);
        Assert.Equal(session.DocumentId, (await f.As<WikiCollaborationSession>(f.Reader, new WatchCollaboration(page))).DocumentId);
        // Live is for those who may read the page: an account without access to the space joins nothing.
        var outsider = await f.Host.SeedUserAsync("Outsider");
        Assert.False((await f.Host.InvokeAsync<CollaboratorAuthorization>(new AuthorizeCollaborator(page, session.Generation, outsider.Id))).CanRead);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<WikiCollaborationSession>(outsider, new WatchCollaboration(page)))).Status);
        // A private new page stays private, live or not.
        var draft = await f.CreateAsync("Koncept");
        await f.As<PagePublicationState>(f.Writer, new SetPagePublication(draft, new("Live")));
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Reader, new GetPage(draft)))).Status);
    }

    [PostgreSqlFact]
    public async Task A_share_link_created_on_a_manual_page_never_starts_following_its_saves()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.PublishedAsync("Postup");
        await f.As<SharingPolicyDto>(f.Admin, new SaveSharingPolicy(f.SpaceId, new(true, null)));
        var pinned = await f.As<IssuedShareLink>(f.Admin, new CreateShareLink(page, new("Ručně", DateTimeOffset.UtcNow.AddDays(1))));

        // The space default becomes Živě, then the page is saved: the link made under Ručně still shows the published version.
        var settings = await f.As<SpaceSettingsDto>(f.Admin, new GetSpaceSettings(f.SpaceId));
        await f.As<SpaceSettingsDto>(f.Admin, new SaveSpaceSettings(f.SpaceId, Request(settings, "Live")));
        await f.SaveAsync(page, "Postup 2", 1);
        Assert.Equal("Postup", (await f.As<SharedPageDto>(f.Admin, new OpenSharedPage(ShareSecret.Hash(pinned.Secret)))).Title);

        // A link made while the page is Živě follows it.
        var live = await f.As<IssuedShareLink>(f.Admin, new CreateShareLink(page, new("Živě", DateTimeOffset.UtcNow.AddDays(1))));
        Assert.Equal("Postup 2", (await f.As<SharedPageDto>(f.Admin, new OpenSharedPage(ShareSecret.Hash(live.Secret)))).Title);
    }

    /// <summary>Migration 2026110660 over existing links: none of them follows a live page.</summary>
    [PostgreSqlFact]
    public async Task The_migration_leaves_every_existing_share_link_pinned()
    {
        await using var schema = await DisposableSchema.CreateAsync("atlas_share_follows_live_migration_");
        await OutboxTests.ApplyBeforeAsync(schema.ConnectionString, 2026110660);
        // The link alone: its page, revision and policy rows do not matter to the migration (replica skips the foreign keys).
        await ScalarAsync(schema.ConnectionString, """
            SET session_replication_role = replica;
            INSERT INTO "PageShareLink"("Id","SpaceId","PageId","RevisionId","PolicyId","CreatedById","CreatorSecurityStamp","Name","TokenHash","ExpiresAt","ActiveFrom")
            VALUES(gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),gen_random_uuid(),'stamp','Odkaz',repeat('A',64),now()+interval '1 day',now());
            """);

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal("1:0", await ScalarAsync(schema.ConnectionString,
            """SELECT count(*) || ':' || count(*) FILTER (WHERE "FollowsLive") FROM "PageShareLink" """));
    }

    private static (string? Mode, string Space, string Effective) Modes(PagePublicationState state) => (state.Mode, state.SpaceMode, state.Effective);

    private static (string Title, bool Live) Read(PageDto page) => (page.Title, page.Live);

    private static SaveSpaceSettingsRequest Request(SpaceSettingsDto settings, string mode)
        => new(settings.Name, settings.Icon, settings.Color, settings.Description, settings.PinPolicy, settings.Version, PublicationMode: mode);

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(AtlasTestHost host, UserAccount admin, UserAccount writer, UserAccount reader, Guid spaceId)
        {
            Host = host; Admin = admin; Writer = writer; Reader = reader; SpaceId = spaceId;
        }

        public AtlasTestHost Host { get; }
        public UserAccount Admin { get; }
        public UserAccount Writer { get; }
        public UserAccount Reader { get; }
        public Guid SpaceId { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var host = await AtlasTestHost.CreateAsync("atlas_publication_mode_");
            var admin = await host.SeedUserAsync("Admin", isWikiAdmin: true);
            var writer = await host.SeedUserAsync("Writer");
            var reader = await host.SeedUserAsync("Reader");
            var space = await host.SeedSpaceAsync("Provoz", (writer, SpaceRole.Writer), (reader, SpaceRole.Reader));
            return new Fixture(host, admin, writer, reader, space);
        }

        public Task<T> As<T>(UserAccount actor, object message)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync<T>(message);
        }

        public async Task<Guid> CreateAsync(string title)
            => (await As<PageDto>(Writer, new CreatePage(new CreatePageRequest(SpaceId, null, title, Rich(title), null)))).Id;

        public async Task<Guid> PublishedAsync(string title)
        {
            var page = await CreateAsync(title);
            await As<PageDto>(Writer, new PublishPage(page, new PublishPageRequest(1)));
            return page;
        }

        /// <summary>Saves the next version and answers the id of its revision.</summary>
        public async Task<Guid> SaveAsync(Guid page, string title, int expectedVersion)
        {
            await As<PageDto>(Writer, new SavePage(page, new SavePageRequest(title, Rich(title), null, expectedVersion)));
            using var session = Host.OpenSession();
            return await session.QueryOver<PageRevision>().Where(x => x.PageId == page && x.ActiveTo == null).Select(x => x.Id).SingleOrDefaultAsync<Guid>();
        }

        /// <summary>The delivery 3.6-C's VersionClosed makes, handled as the outbox would.</summary>
        public Task CloseAsync(Guid page, Guid revision, string reason = VersionCloseReasons.Pause)
            => Host.InvokeAsync(new AutoPublishOnVersionClosed(new VersionClosed(page, revision, reason)));

        public async Task<long> ScalarAsync(string sql, Guid id)
        {
            using var session = Host.OpenSession();
            return Convert.ToInt64(await session.CreateSQLQuery(sql).SetParameter("id", id).UniqueResultAsync());
        }

        private static string Rich(string text) => ProjectorDocuments.Json(ProjectorDocuments.Paragraph(text));

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
