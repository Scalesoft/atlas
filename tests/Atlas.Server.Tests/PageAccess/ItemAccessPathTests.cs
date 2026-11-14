using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Attachments.Commands;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.Attachments.Queries;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Queries;
using Atlas.Server.Features.ContentExport;
using Atlas.Server.Features.ContentExport.Contracts;
using Atlas.Server.Features.ContentExport.Queries;
using Atlas.Server.Features.ContentImport.Domain;
using Atlas.Server.Features.ExternalSharing.Commands;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.ExternalSharing.Domain;
using Atlas.Server.Features.ExternalSharing.Queries;
using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.PageHistory.Queries;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageSource.Commands;
using Atlas.Server.Features.PageSource.Contracts;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.PageAccess.ItemAccessSeed;
using Atlas.Server.Features.PageTags.Queries;

namespace Atlas.Server.Tests.PageAccess;

/// <summary>S2: every point-of-use page path decides on the page itself. On an own-access page a space Writer without a page grant
/// is refused as if the page were missing, while an account whose only access is a direct page grant is allowed exactly the
/// operations of that grant (and told 403 for the rest).</summary>
public sealed class ItemAccessPathTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Page_read_save_and_publish_follow_the_page_grant()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");

        foreach (var message in new object[] { new GetPage(page.Id), Save(page, 1), new PublishPage(page.Id, new(1)) })
            Assert.Equal(404, await f.StatusAsync<PageDto>(f.Writer, message));

        var read = await f.As<PageDto>(f.PageReader, new GetPage(page.Id));
        Assert.False(read.CanEdit);
        // Save resolves its gate with the standard answer: the 403 carries the text of EditContent, not a wording of its own.
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.PageReader, Save(page, 1)));
        Assert.Equal((403, "K této operaci potřebujete oprávnění k úpravám stránky."), (refused.Status, refused.Message));
        Assert.Equal(403, await f.StatusAsync<PageDto>(f.PageReader, new PublishPage(page.Id, new(1))));

        Assert.True((await f.As<PageDto>(f.PageWriter, new GetPage(page.Id))).CanEdit);
        var saved = await f.As<PageDto>(f.PageWriter, Save(page, 1));
        Assert.True((await f.As<PageDto>(f.PageWriter, new PublishPage(page.Id, new(saved.Version)))).IsPublished);
        // D4: the space Admin keeps document management on own-access pages.
        Assert.True((await f.As<PageDto>(f.Admin, new GetPage(page.Id))).CanEdit);
    }

    [PostgreSqlFact]
    public async Task Creating_a_subpage_needs_edit_content_on_the_parent()
    {
        await using var f = await Fixture.CreateAsync();
        var parent = await f.OwnPageAsync("Parent");

        Assert.Equal(400, await f.StatusAsync<PageDto>(f.Writer, Create(f.SpaceId, parent.Id)));
        Assert.Equal(403, await f.StatusAsync<PageDto>(f.PageReader, Create(f.SpaceId, parent.Id)));
        // D12: a root page still needs Writer on the space, which a page grant alone does not give. Placing a page at the root is a
        // space operation: without a role in the space the answer is 404, and a space Reader, who knows the space, is told 403 (review S3).
        Assert.Equal(404, await f.StatusAsync<PageDto>(f.PageWriter, Create(f.SpaceId, null)));
        Assert.Equal(403, await f.StatusAsync<PageDto>(f.Reader, Create(f.SpaceId, null)));
        var shared = await f.SharedPageAsync("Shared");
        var refused = await Assert.ThrowsAsync<ApiException>(() => f.As<PageDto>(f.Reader, Create(f.SpaceId, shared.Id)));
        Assert.Equal((403, "Podstránku můžete vytvořit jen pod stránkou, kterou smíte upravovat."), (refused.Status, refused.Message));
        Assert.Null((await f.As<PageDto>(f.Writer, Create(f.SpaceId, null))).ParentId);
        // With a direct Writer grant on the parent the space Writer may add a subpage; copy-on-create (S4) gives it the parent's
        // grants, so a page-only account can read its own result too (ItemAccessListTests).
        await f.Grant(parent.Id, f.Writer, 2);
        Assert.Equal(parent.Id, (await f.As<PageDto>(f.Writer, Create(f.SpaceId, parent.Id))).ParentId);
    }

    [PostgreSqlFact]
    public async Task Rename_move_trash_and_untrash_follow_the_page_grant()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");
        var target = await f.OwnPageAsync("Target", grants: false);
        var shared = await f.SharedPageAsync("Shared");
        var child = await f.OwnPageAsync("Child");
        await f.As<OrganizationChange>(f.Admin, new MovePage(child.Id, new(shared.Id, null, await f.TreeVersionAsync())));

        Assert.Equal(404, await f.StatusAsync(f.Writer, new RenamePage(page.Id, new("Renamed", page.Version))));
        Assert.Equal(403, await f.StatusAsync(f.PageReader, new RenamePage(page.Id, new("Renamed", page.Version))));
        await f.As(f.PageWriter, new RenamePage(page.Id, new("Renamed", page.Version)));

        Assert.Equal(404, await f.StatusAsync<OrganizationChange>(f.Writer, new MovePage(page.Id, new(shared.Id, null, await f.TreeVersionAsync()))));
        // The target parent needs EditContent too: a parent the account cannot read is indistinguishable from a missing one.
        Assert.Equal(400, await f.StatusAsync<OrganizationChange>(f.Writer, new MovePage(shared.Id, new(target.Id, null, await f.TreeVersionAsync()))));
        Assert.Equal(400, await f.StatusAsync<OrganizationChange>(f.PageWriter, new MovePage(page.Id, new(target.Id, null, await f.TreeVersionAsync()))));
        // A target parent the account can read but not edit is refused with 403 and says why.
        await f.Grant(target.Id, f.PageWriter, 1);
        var tree = await f.TreeVersionAsync();
        var readOnlyParent = await Assert.ThrowsAsync<ApiException>(() =>
            f.As<OrganizationChange>(f.PageWriter, new MovePage(page.Id, new(target.Id, null, tree))));
        Assert.Equal((403, "Pod cílovou stránku nemůžete vkládat podstránky, protože nemáte oprávnění k jejím úpravám."),
            (readOnlyParent.Status, readOnlyParent.Message));
        await f.CloseGrantsAsync(target.Id, f.PageWriter);
        await f.Grant(target.Id, f.PageWriter, 2);
        // Owner decision (a): the space root needs Writer on the space, like creating a root page (D12).
        Assert.Equal(404, await f.StatusAsync<OrganizationChange>(f.PageWriter, new MovePage(page.Id, new(null, null, await f.TreeVersionAsync()))));
        await f.As<OrganizationChange>(f.PageWriter, new MovePage(page.Id, new(target.Id, null, await f.TreeVersionAsync())));

        var version = await f.PageVersionAsync(page.Id);
        Assert.Equal(404, await f.StatusAsync<OrganizationChange>(f.Writer, new TrashPage(page.Id, new(true, await f.TreeVersionAsync(), version))));
        // Trashing a subtree needs EditContent on every page of it (D2): the own-access subpage is invisible to the space Writer.
        var sharedVersion = await f.PageVersionAsync(shared.Id);
        Assert.Equal(404, await f.StatusAsync<OrganizationChange>(f.Writer, new TrashPage(shared.Id, new(true, await f.TreeVersionAsync(), sharedVersion))));
        await f.As<OrganizationChange>(f.PageWriter, new TrashPage(page.Id, new(true, await f.TreeVersionAsync(), version)));

        // Restoring from the trash stays with space administration: ManageAccess on the page and the space's Admin role, which a page's
        // own manage grant is not (SEC-Z-1, 9651c6b0), under the space root as under a parent it may edit.
        Assert.Equal(403, await f.StatusAsync<OrganizationChange>(f.PageWriter, new UntrashPage(page.Id, new(null, true, await f.TreeVersionAsync()))));
        Assert.Equal(403, await f.StatusAsync<OrganizationChange>(f.PageManager, new UntrashPage(page.Id, new(null, true, await f.TreeVersionAsync()))));
        await f.Grant(target.Id, f.PageManager, 2);
        Assert.Equal(403, await f.StatusAsync<OrganizationChange>(f.PageManager, new UntrashPage(page.Id, new(target.Id, true, await f.TreeVersionAsync()))));
        await f.As<OrganizationChange>(f.Admin, new UntrashPage(page.Id, new(target.Id, true, await f.TreeVersionAsync())));
    }

    [PostgreSqlFact]
    public async Task Moving_to_another_space_needs_edit_content_on_every_moved_page_and_writer_in_both_spaces()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");
        var shared = await f.SharedPageAsync("Shared");
        var target = await f.Host.SeedSpaceAsync("target", (f.Writer, SpaceRole.Writer), (f.PageWriter, SpaceRole.Writer), (f.Reader, SpaceRole.Writer));

        Assert.Equal(404, await f.StatusAsync<CrossSpaceChange>(f.Writer, await f.MoveToSpaceAsync(page, target)));
        // A space Reader who reads the page is told 403 (1.0: 404).
        Assert.Equal(403, await f.StatusAsync<CrossSpaceChange>(f.Reader, await f.MoveToSpaceAsync(shared, target)));
        // Owner decision (b): as in 1.0 the pages leave the source space, so a page grant alone is not enough there.
        Assert.Equal(404, await f.StatusAsync<CrossSpaceChange>(f.PageWriter, await f.MoveToSpaceAsync(page, target)));
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = f.PageWriter.Id, Role = 2 }));
        var moved = await f.As<CrossSpaceChange>(f.PageWriter, await f.MoveToSpaceAsync(page, target));
        Assert.Equal(target, moved.TargetSpaceId);
    }

    [PostgreSqlFact]
    public async Task A_move_position_is_anchored_only_on_a_sibling_the_account_can_list()
    {
        await using var f = await Fixture.CreateAsync();
        var hidden = await f.OwnPageAsync("Hidden");
        var visible = await f.SharedPageAsync("Visible");
        var moved = await f.SharedPageAsync("Moved");

        // The own-access sibling is published but not readable for the space Writer: anchoring on it would confirm it exists.
        Assert.Equal(400, await f.StatusAsync<OrganizationChange>(f.Writer, new MovePage(moved.Id, new(null, hidden.Id, await f.TreeVersionAsync()))));
        await f.As<OrganizationChange>(f.Writer, new MovePage(moved.Id, new(null, visible.Id, await f.TreeVersionAsync())));
        await f.As<OrganizationChange>(f.Admin, new MovePage(moved.Id, new(null, hidden.Id, await f.TreeVersionAsync())));
    }

    [PostgreSqlFact]
    public async Task History_needs_edit_content_and_hides_itself_from_readers()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");
        var revision = await f.CurrentRevisionAsync(page.Id);

        // Who cannot see the page learns nothing; who reads it is told the missing right (review S3), and still sees no history.
        foreach (var (reader, status) in new[] { (f.Writer, 404), (f.PageReader, 403) })
        {
            Assert.Equal(status, await f.StatusAsync<PageHistoryList>(reader, new GetPageHistory(page.Id, null, 20)));
            Assert.Equal(status, await f.StatusAsync<PageHistoryRevision>(reader, new GetPageRevision(page.Id, revision)));
            Assert.Equal(status, await f.StatusAsync<PageDto>(reader, new RestorePage(page.Id, new(revision, page.Version))));
        }
        Assert.Single((await f.As<PageHistoryList>(f.PageWriter, new GetPageHistory(page.Id, null, 20))).Items);
        Assert.Equal(revision, (await f.As<PageHistoryRevision>(f.PageWriter, new GetPageRevision(page.Id, revision))).Id);
        await f.As<PageDto>(f.PageWriter, new RestorePage(page.Id, new(revision, page.Version)));
    }

    [PostgreSqlFact]
    public async Task Page_source_needs_manage_access_and_tells_readers_why()
    {
        await using var f = await Fixture.CreateAsync();
        var shared = await f.SharedPageAsync("Shared");
        var page = await f.OwnPageAsync("Private");

        // A space Reader who can read the page is told the refusal (1.0 answered 404).
        Assert.Equal(403, await f.StatusAsync<PageDto>(f.Reader, Source(shared, "reader")));
        Assert.Equal(404, await f.StatusAsync<PageDto>(f.Writer, Source(page, "writer")));
        Assert.Equal(403, await f.StatusAsync<PageDto>(f.PageWriter, Source(page, "page writer")));
        Assert.Equal("manager", (await f.As<PageDto>(f.PageManager, Source(page, "manager"))).Markdown);
    }

    [PostgreSqlFact]
    public async Task Attachments_follow_the_page_grant()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");

        var writer = await Assert.ThrowsAsync<ApiException>(() => f.UploadAsync(f.Writer, page.Id));
        Assert.Equal((404, "Příloha není dostupná."), (writer.Status, writer.Message));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.UploadAsync(f.PageReader, page.Id))).Status);
        var attachment = await f.UploadAsync(f.PageWriter, page.Id);

        Assert.Equal(404, await f.StatusAsync<AttachmentContentDto>(f.Writer, new OpenAttachment(attachment.Id, false)));
        Assert.Equal(attachment.Id, (await f.As<AttachmentContentDto>(f.PageWriter, new OpenAttachment(attachment.Id, false))).Id);
    }

    [PostgreSqlFact]
    public async Task Decorations_tags_and_personal_state_follow_the_page_grant()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");
        var decoration = new SetPageDecoration(page.Id, new("star", "blue", null));
        var tags = new SetPageTags(page.Id, new(["alpha"], []));

        Assert.Equal(404, await f.StatusAsync<PageDecorationState>(f.Writer, new GetPageDecoration(page.Id)));
        Assert.Equal(404, await f.StatusAsync<PageDecorationState>(f.Writer, decoration));
        Assert.Equal(404, await f.StatusAsync<PageTagState>(f.Writer, new GetPageTags(page.Id)));
        Assert.Equal(404, await f.StatusAsync<PageTagState>(f.Writer, tags));
        Assert.Equal(404, await f.StatusAsync<PersonalPageState>(f.Writer, new GetPersonalPageState(page.Id)));
        Assert.Equal(404, await f.StatusAsync<PersonalPageState>(f.Writer, new RecordPageVisit(page.Id)));
        Assert.Equal(404, await f.StatusAsync<PersonalPageState>(f.Writer, new SetPageFavorite(page.Id, new(true))));

        Assert.Null((await f.As<PageDecorationState>(f.PageReader, new GetPageDecoration(page.Id))).Decoration);
        Assert.Empty((await f.As<PageTagState>(f.PageReader, new GetPageTags(page.Id))).Tags);
        Assert.Equal(403, await f.StatusAsync<PageDecorationState>(f.PageReader, decoration));
        Assert.Equal(403, await f.StatusAsync<PageTagState>(f.PageReader, tags));
        await f.As<PersonalPageState>(f.PageReader, new RecordPageVisit(page.Id));
        Assert.True((await f.As<PersonalPageState>(f.PageReader, new SetPageFavorite(page.Id, new(true)))).IsFavorite);
        Assert.NotNull((await f.As<PersonalPageState>(f.PageReader, new GetPersonalPageState(page.Id))).VisitedAt);

        Assert.Equal("star", (await f.As<PageDecorationState>(f.PageWriter, decoration)).Decoration!.Icon);
        Assert.Equal("alpha", Assert.Single((await f.As<PageTagState>(f.PageWriter, tags)).Tags).Name);
    }

    [PostgreSqlFact]
    public async Task Access_overview_explains_to_readers_and_lists_people_to_managers()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");

        Assert.Equal(404, await f.StatusAsync<PageAccessState>(f.Writer, new GetPageAccess(page.Id)));
        Assert.Equal(404, await f.StatusAsync<PageAccessHistory>(f.Writer, new GetPageAccessHistory(page.Id)));
        var own = await f.As<PageAccessState>(f.PageReader, new GetPageAccess(page.Id));
        Assert.False(own.CanManage);
        Assert.Empty(own.Items);
        Assert.Equal(403, await f.StatusAsync<PageAccessHistory>(f.PageReader, new GetPageAccessHistory(page.Id)));
        Assert.True((await f.As<PageAccessState>(f.PageManager, new GetPageAccess(page.Id))).CanManage);
        await f.As<PageAccessHistory>(f.PageManager, new GetPageAccessHistory(page.Id));
    }

    [PostgreSqlFact]
    public async Task Page_export_needs_read_content_at_build_and_at_the_final_revalidation()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");

        Assert.Equal(404, await f.StatusAsync<ContentExportArchive>(f.Writer, new BuildContentExport(page.Id, false)));
        var archive = await f.As<ContentExportArchive>(f.PageReader, new BuildContentExport(page.Id, false));
        try
        {
            Assert.Equal(page.Id, Assert.Single(archive.Selected).PageId);
            await f.As(f.PageReader, new RevalidateContentExport(archive.SpaceId, archive.Selected));
            // The space Writer cannot pass the final check for an archive of a page it cannot read.
            Assert.Equal(404, await f.StatusAsync(f.Writer, new RevalidateContentExport(archive.SpaceId, archive.Selected)));
            await f.CloseGrantsAsync(page.Id, f.PageReader);
            Assert.Equal(404, await f.StatusAsync(f.PageReader, new RevalidateContentExport(archive.SpaceId, archive.Selected)));
        }
        finally
        {
            await archive.DisposeAsync();
        }
    }

    [PostgreSqlFact]
    public async Task Collaboration_needs_edit_content_on_the_page()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");

        Assert.Equal(404, await f.StatusAsync<WikiCollaborationSession>(f.Writer, new OpenCollaboration(page.Id)));
        Assert.Equal(404, await f.StatusAsync<WikiCollaborationSession>(f.PageReader, new OpenCollaboration(page.Id)));
        var session = await f.As<WikiCollaborationSession>(f.PageWriter, new OpenCollaboration(page.Id));

        Assert.False((await f.Host.InvokeAsync<CollaboratorAuthorization>(new AuthorizeCollaborator(page.Id, session.Generation, f.Writer.Id))).CanWrite);
        Assert.False((await f.Host.InvokeAsync<CollaboratorAuthorization>(new AuthorizeCollaborator(page.Id, session.Generation, f.PageReader.Id))).CanWrite);
        Assert.True((await f.Host.InvokeAsync<CollaboratorAuthorization>(new AuthorizeCollaborator(page.Id, session.Generation, f.PageWriter.Id))).CanWrite);
    }

    [PostgreSqlFact]
    public async Task Share_links_need_share_on_the_page_for_the_whole_life_of_the_link()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");
        await f.As<SharingPolicyDto>(f.Admin, new SaveSharingPolicy(f.SpaceId, new(true, null)));
        var expiry = DateTimeOffset.UtcNow.AddDays(7);

        Assert.Equal(404, await f.StatusAsync<IssuedShareLink>(f.Writer, new CreateShareLink(page.Id, new("Writer", expiry))));
        Assert.Equal(404, await f.StatusAsync<ShareLinksDto>(f.Writer, new ListShareLinks(f.SpaceId, page.Id)));
        Assert.Equal(403, await f.StatusAsync<IssuedShareLink>(f.PageWriter, new CreateShareLink(page.Id, new("Writer", expiry))));
        var issued = await f.As<IssuedShareLink>(f.PageManager, new CreateShareLink(page.Id, new("Partner", expiry)));
        Assert.Single((await f.As<ShareLinksDto>(f.PageManager, new ListShareLinks(f.SpaceId, page.Id))).Items);
        Assert.Equal("Private", (await f.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(issued.Secret)))).Title);

        // The issuer's page grant ends: the link stops resolving, and a bounded grant cannot issue a longer link.
        await f.CloseGrantsAsync(page.Id, f.PageManager);
        Assert.Equal(404, await f.StatusAsync<SharedPageDto>(f.PageManager, new OpenSharedPage(ShareSecret.Hash(issued.Secret))));
        await f.Grant(page.Id, f.PageManager, 3, expiresAt: "now()+interval '1 day'");
        Assert.Equal(403, await f.StatusAsync<IssuedShareLink>(f.PageManager, new CreateShareLink(page.Id, new("Too long", expiry))));
        await f.As<IssuedShareLink>(f.PageManager, new CreateShareLink(page.Id, new("Short", DateTimeOffset.UtcNow.AddHours(12))));
        Assert.Equal(404, await f.StatusAsync(f.Writer, new RevokeShareLink(page.Id, issued.Link.Id)));
        await f.As(f.PageManager, new RevokeShareLink(page.Id, issued.Link.Id));
    }

    [PostgreSqlFact]
    public async Task Share_link_invalidation_triggers_count_page_share_authority()
    {
        await using var f = await Fixture.CreateAsync();
        var page = await f.OwnPageAsync("Private");
        await f.As<SharingPolicyDto>(f.Admin, new SaveSharingPolicy(f.SpaceId, new(true, null)));
        var expiry = DateTimeOffset.UtcNow.AddDays(7);
        // The space Admin also manages the page directly; losing the space grant leaves the page authority, so the link lives.
        await f.Grant(page.Id, f.Admin, 3);
        var admin = await f.As<IssuedShareLink>(f.Admin, new CreateShareLink(page.Id, new("Admin", expiry)));
        var manager = await f.As<IssuedShareLink>(f.PageManager, new CreateShareLink(page.Id, new("Manager", expiry)));
        await f.Host.CloseAccessAsync(AccessAuditLog.SpaceGrant, "\"UserId\"=:id", f.Admin.Id);
        Assert.Null(await f.LinkClosedAtAsync(admin.Link.Id));
        Assert.Equal("Private", (await f.Host.InvokeAsync<SharedPageDto>(new OpenSharedPage(ShareSecret.Hash(admin.Secret)))).Title);

        // Closing the page grant closes the issuer's links of that page in the same transaction, not only at the next open.
        await f.CloseGrantsAsync(page.Id, f.PageManager);
        Assert.NotNull(await f.LinkClosedAtAsync(manager.Link.Id));
        Assert.Null(await f.LinkClosedAtAsync(admin.Link.Id));
        await f.CloseGrantsAsync(page.Id, f.Admin);
        Assert.NotNull(await f.LinkClosedAtAsync(admin.Link.Id));
    }

    [PostgreSqlFact]
    public async Task Import_quarantine_counts_page_grants()
    {
        await using var f = await Fixture.CreateAsync();
        var quarantine = await f.Host.SeedSpaceAsync("quarantine");
        var page = await PageAsync(f.Host, quarantine, f.Admin);
        using (var session = f.Host.OpenSession())
            await ImportGuards.RequireQuarantineAsync(session, quarantine, default);

        await f.Grant(page, f.PageReader, 1);
        using (var session = f.Host.OpenSession())
            Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => ImportGuards.RequireQuarantineAsync(session, quarantine, default))).Status);
    }

    private static SavePage Save(PageDto page, int expectedVersion) =>
        new(page.Id, new SavePageRequest("Edited", Fixture.Rich("Edited"), "Edited", expectedVersion));

    private static CreatePage Create(Guid spaceId, Guid? parentId) =>
        new(new CreatePageRequest(spaceId, parentId, "Child", Fixture.Rich("Child"), "Child"));

    private static ReplacePageSource Source(PageDto page, string text) =>
        new(page.Id, new ReplacePageSourceRequest(Fixture.Rich(text), text, page.Version));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly NpgsqlConnection m_connection;

        private Fixture(AtlasTestHost host, NpgsqlConnection connection)
        {
            Host = host;
            m_connection = connection;
        }

        public AtlasTestHost Host { get; }
        public UserAccount Admin { get; private set; } = null!;
        public UserAccount Writer { get; private set; } = null!;
        public UserAccount Reader { get; private set; } = null!;
        /// <summary>No space grant: a direct page grant is the only access of these accounts.</summary>
        public UserAccount PageReader { get; private set; } = null!;
        public UserAccount PageWriter { get; private set; } = null!;
        public UserAccount PageManager { get; private set; } = null!;
        public Guid SpaceId { get; private set; }

        public static string Rich(string text) =>
            "{\"format\":\"atlas-rich\",\"version\":1,\"document\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}]}}";

        public static async Task<Fixture> CreateAsync()
        {
            // Attachments and exports go to the host's own temporary content root.
            var host = await AtlasTestHost.CreateAsync("atlas_item_paths_", services => services.AddSingleton<ContentExportFiles>(), sharedAs: "item-paths");
            var f = new Fixture(host, await OpenAsync(host))
            {
                Admin = await host.SeedUserAsync("admin"),
                Writer = await host.SeedUserAsync("writer"),
                Reader = await host.SeedUserAsync("reader"),
                PageReader = await host.SeedUserAsync("pagereader"),
                PageWriter = await host.SeedUserAsync("pagewriter"),
                PageManager = await host.SeedUserAsync("pagemanager"),
            };
            f.SpaceId = await host.SeedSpaceAsync("space", (f.Admin, SpaceRole.Admin), (f.Writer, SpaceRole.Writer), (f.Reader, SpaceRole.Reader));
            return f;
        }

        /// <summary>A published page of the space with default access.</summary>
        public async Task<PageDto> SharedPageAsync(string title)
        {
            var page = await As<PageDto>(Admin, new CreatePage(new CreatePageRequest(SpaceId, null, title, Rich(title), title)));
            return await As<PageDto>(Admin, new PublishPage(page.Id, new(page.Version)));
        }

        /// <summary>A published own-access page, by default with a Reader, Writer and Manager grant for the page-only accounts.</summary>
        public async Task<PageDto> OwnPageAsync(string title, bool grants = true)
        {
            var page = await SharedPageAsync(title);
            await Host.ExecuteAsync("UPDATE \"Page\" SET \"OwnAccess\"=true WHERE \"Id\"=:id", page.Id);
            if (grants)
            {
                await Grant(page.Id, PageReader, 1);
                await Grant(page.Id, PageWriter, 2);
                await Grant(page.Id, PageManager, 3);
            }
            return page;
        }

        public Task Grant(Guid pageId, UserAccount user, int role, string expiresAt = "NULL") =>
            GrantAsync(m_connection, pageId, user.Id, null, role, "NULL", expiresAt);

        /// <summary>As the application closes a grant: with its audit and the share links its issuer loses with it.</summary>
        public Task CloseGrantsAsync(Guid pageId, UserAccount user) =>
            Host.CloseAccessAsync(AccessAuditLog.ResourceGrant, $"\"PageId\"='{pageId}' AND \"UserId\"=:id", user.Id);

        public async Task<DateTime?> LinkClosedAtAsync(Guid linkId)
        {
            using var session = Host.OpenSession();
            return (await session.GetAsync<PageShareLink>(linkId))!.ActiveTo;
        }

        public async Task<long> TreeVersionAsync()
        {
            using var session = Host.OpenSession();
            return await TreeVersion.ReadAsync(session, SpaceId, default);
        }

        public async Task<int> PageVersionAsync(Guid pageId)
        {
            using var session = Host.OpenSession();
            return (await session.GetAsync<Page>(pageId))!.Version;
        }

        public async Task<Guid> CurrentRevisionAsync(Guid pageId)
        {
            using var session = Host.OpenSession();
            return await session.QueryOver<PageRevision>().Where(x => x.PageId == pageId && x.ActiveTo == null).Select(x => x.Id).SingleOrDefaultAsync<Guid>();
        }

        public async Task<MovePageToSpace> MoveToSpaceAsync(PageDto page, Guid target)
        {
            using var session = Host.OpenSession();
            return new MovePageToSpace(page.Id, new MovePageSpaceRequest(target, null, null, true,
                await TreeVersion.ReadAsync(session, page.SpaceId, default), await TreeVersion.ReadAsync(session, target, default),
                (await session.GetAsync<Page>(page.Id))!.Version, true));
        }

        public async Task<AttachmentDto> UploadAsync(UserAccount actor, Guid pageId)
        {
            await As(actor, new AuthorizeAttachmentUpload(pageId));
            var id = Guid.NewGuid();
            using var content = new MemoryStream("attachment"u8.ToArray());
            var stored = await Host.Services.GetRequiredService<AttachmentFiles>().WriteAsync(id, content, false, CancellationToken.None);
            return await As<AttachmentDto>(actor, new StoreAttachment(id, pageId, "note.txt", stored.Size, stored.Hash, stored.MimeType, false));
        }

        public Task<T> As<T>(UserAccount actor, object message)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync<T>(message);
        }

        public Task As(UserAccount actor, object message)
        {
            Host.Actor.Current = actor;
            return Host.InvokeAsync(message);
        }

        public async Task<int> StatusAsync<T>(UserAccount actor, object message)
        {
            try
            {
                var result = await As<T>(actor, message);
                if (result is IAsyncDisposable disposable)
                    await disposable.DisposeAsync();
                return 200;
            }
            catch (ApiException error)
            {
                return error.Status;
            }
        }

        public async Task<int> StatusAsync(UserAccount actor, object message)
        {
            try
            {
                await As(actor, message);
                return 200;
            }
            catch (ApiException error)
            {
                return error.Status;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await m_connection.DisposeAsync();
            await Host.DisposeAsync();
        }
    }
}
