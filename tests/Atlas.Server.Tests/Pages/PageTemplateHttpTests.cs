using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Attachments.Domain;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PageTemplates;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Platform.Content;
using Atlas.Server.Tests.Backups;
using Atlas.Server.Tests.PageGate;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Pages;

public sealed class PageTemplateHttpTests
{
    private const string Rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Template content"}]},{"type":"callout","attrs":{"variant":"tip"},"content":[{"type":"paragraph"}]}]}}""";
    private const string Wrong = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";

    [PostgreSqlFact]
    public async Task Admin_marks_normal_page_as_template_and_copy_uses_saved_snapshot()
    {
        await using var site = await AtlasHttpSite.CreateAsync("atlas_page_flag_http_");
        using var admin = site.Client();
        await site.CompleteSetupAsync(admin);
        var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
        var source = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Runbook", contentJson = Rich, markdown = "Template content", isTemplate = true }), HttpStatusCode.OK);
        var sourceId = source["id"]!.GetValue<string>();
        Assert.True(source["isTemplate"]!.GetValue<bool>());
        var list = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/page-templates"), HttpStatusCode.OK);
        Assert.Equal(sourceId, list[0]!["id"]!.GetValue<string>());
        Assert.Null(list[0]!["contentJson"]);
        var normalTree = await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/pages"), HttpStatusCode.OK);
        Assert.Empty(normalTree.AsArray());

        var copy = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId, parentId = (string?)null, title = "Copy", contentJson = Wrong, markdown = "wrong", templatePageId = sourceId }), HttpStatusCode.OK);
        Assert.False(copy["isTemplate"]!.GetValue<bool>());
        Assert.Equal(Rich, copy["contentJson"]!.GetValue<string>());
        Assert.NotEqual(sourceId, copy["id"]!.GetValue<string>());
        await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{sourceId}/template",
            new { isTemplate = false }), HttpStatusCode.OK);
        Assert.Empty((await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/page-templates"), HttpStatusCode.OK)).AsArray());
        Assert.Single((await AtlasHttpSite.Json(await admin.GetAsync($"/api/spaces/{spaceId}/pages"), HttpStatusCode.OK)).AsArray(),
            x => x!["id"]!.GetValue<string>() == sourceId);
    }

    [PostgreSqlFact]
    public async Task Writer_reads_published_template_but_cannot_edit_it_and_copy_owns_attachments()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        var writer = await fixture.Host.SeedUserAsync("templatewriter");
        Guid spaceId;
        UserAccount admin;
        using (var session = fixture.Host.OpenSession())
        {
            spaceId = (await session.GetAsync<Page>(fixture.PageId)).SpaceId;
            admin = await session.GetAsync<UserAccount>(fixture.AdminId);
        }
        var rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"image","attrs":{"src":"/api/attachments/""" + fixture.AttachmentId + """/content"}}]}}""";
        await fixture.Host.SeedAsync(async session =>
        {
            var page = await session.GetAsync<Page>(fixture.PageId);
            page.IsTemplate = true;
            var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == fixture.PageId && x.ActiveTo == null).SingleOrDefaultAsync();
            revision.ContentJson = rich;
            await session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = spaceId, UserId = writer.Id, Role = (int)SpaceRole.Writer });
        });
        fixture.Host.Actor.Current = writer;
        Assert.Single(await fixture.Host.InvokeAsync<IReadOnlyList<PageTemplateSummary>>(new ListPageTemplates(spaceId)));
        var denied = await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<PageDto>(
            new SavePage(fixture.PageId, new SavePageRequest("Changed", Rich, "Changed", 2))));
        Assert.Equal(403, denied.Status);
        fixture.Host.Actor.Current = admin;
        var copy = await fixture.Host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Copy", Wrong, "wrong", TemplatePageId: fixture.PageId)));
        Assert.False(copy.IsTemplate);
        Assert.Equal(fixture.AttachmentId.ToString(), Assert.Single(AttachmentReferences.Extract(rich)).ToString());
        var copiedAttachmentId = Assert.Single(AttachmentReferences.Extract(copy.ContentJson));
        Assert.NotEqual(fixture.AttachmentId, copiedAttachmentId);
        Assert.Contains(copiedAttachmentId.ToString(), copy.Markdown);
        using var check = fixture.Host.OpenSession();
        var attachment = await check.GetAsync<Attachment>(copiedAttachmentId);
        Assert.Equal(copy.Id, attachment.PageId);
        Assert.Equal("original bytes", await File.ReadAllTextAsync(fixture.Files.PathFor(copiedAttachmentId)));
        Assert.Equal("original bytes", await File.ReadAllTextAsync(fixture.Files.PathFor(fixture.AttachmentId)));
    }

    [PostgreSqlFact]
    public async Task Markdown_appended_to_a_template_follows_its_copied_attachments_and_a_template_changed_since_the_read_is_a_conflict()
    {
        await using var fixture = await BackupFixture.CreateAsync();
        Guid spaceId;
        UserAccount admin;
        using (var session = fixture.Host.OpenSession())
        {
            spaceId = (await session.GetAsync<Page>(fixture.PageId)).SpaceId;
            admin = await session.GetAsync<UserAccount>(fixture.AdminId);
        }
        var rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"image","attrs":{"src":"/api/attachments/""" + fixture.AttachmentId + """/content"}}]}}""";
        await SetTemplateContentAsync(fixture, rich);
        fixture.Host.Actor.Current = admin;
        // The create of the personal API and MCP: the template as read, the Markdown appended outside any transaction, then the create.
        var template = await fixture.Host.InvokeAsync<PageDto>(new GetPage(fixture.PageId));
        var (create, _) = PageMarkdownCreate.Convert(new CreatePageRequest(spaceId, null, "Copy", "", "", TemplatePageId: fixture.PageId), "Doplněný text.",
            template, new MergeContext(_ => PageLinkResolution.None, _ => false, () => Guid.NewGuid().ToString()));

        var copy = await fixture.Host.InvokeAsync<PageDto>(create);

        var blocks = RichDocument.Parse(copy.ContentJson).Blocks;
        var copied = Assert.Single(AttachmentReferences.Extract(copy.ContentJson));
        Assert.NotEqual(fixture.AttachmentId, copied);
        Assert.Equal("/api/attachments/" + copied + "/content", blocks[0]["attrs"]!["src"]!.GetValue<string>());
        Assert.Equal("Doplněný text.", blocks[1]["content"]![0]!["text"]!.GetValue<string>());
        Assert.Contains(copied.ToString(), copy.Markdown, StringComparison.Ordinal);

        // The template changed between the read and the create: nothing is created from the stale copy.
        await SetTemplateContentAsync(fixture, rich.Replace("""}}]}}""", """}},{"type":"paragraph","content":[{"type":"text","text":"Nové"}]}]}}""", StringComparison.Ordinal));
        var stale = await Assert.ThrowsAsync<ApiException>(() => fixture.Host.InvokeAsync<PageDto>(create));
        Assert.Equal((409, "version_conflict"), (stale.Status, stale.Code));
    }

    private static Task SetTemplateContentAsync(BackupFixture fixture, string contentJson)
    {
        return fixture.Host.SeedAsync(async session =>
        {
            (await session.GetAsync<Page>(fixture.PageId)).IsTemplate = true;
            var revision = await session.QueryOver<PageRevision>().Where(x => x.PageId == fixture.PageId && x.ActiveTo == null).SingleOrDefaultAsync();
            revision.ContentJson = contentJson;
        });
    }

    [PostgreSqlFact]
    public async Task Space_admin_flags_a_published_page_and_every_refusal_of_the_template_flag_answers_like_an_unknown_page()
    {
        var locks = new RecordingLocks.Log();
        await using var host = await AtlasTestHost.CreateAsync("atlas_page_flag_draft_", RecordingLocks.Into(locks));
        var admin = await host.SeedUserAsync("spaceadmin");
        var author = await host.SeedUserAsync("author");
        var outsider = await host.SeedUserAsync("outsider");
        var spaceId = await host.SeedSpaceAsync("Templates", (admin, SpaceRole.Admin), (author, SpaceRole.Writer));
        host.Actor.Current = author;
        var draft = await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Private", Rich, "Template content")));
        var shared = await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Shared", Rich, "Template content")));
        await host.InvokeAsync<PageDto>(new PublishPage(shared.Id, new PublishPageRequest(shared.Version)));
        var tree = await TreeVersionAsync(host, spaceId);
        // The private draft rule (docs/architecture.md 10.3): a space Admin alone does not read another author's unpublished draft.
        host.Actor.Current = admin;
        var read = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<PageDto>(new GetPage(draft.Id)));
        var unknown = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<PageDto>(new SetPageTemplate(Guid.NewGuid(), true)));
        Assert.Equal((404, (string?)null), (unknown.Status, unknown.Code));

        PageDto? answer = null;
        var refusal = await Record.ExceptionAsync(async () => answer = await host.InvokeAsync<PageDto>(new SetPageTemplate(draft.Id, true)));

        // The flag answers with the page, so it must refuse exactly where the read refuses, and change nothing.
        Assert.Null(answer);
        var refused = Assert.IsType<ApiException>(refusal);
        Assert.Equal(Answer(read), Answer(refused));
        Assert.Equal(Answer(unknown), Answer(refused));
        // An account with no role in the space gets the answer of an unknown id too: a different text would tell it whether a page
        // id exists. A Writer on a page it reads knows the page, so it is told that the flag needs the space Admin (review S3).
        host.Actor.Current = outsider;
        var withoutAccess = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<PageDto>(new SetPageTemplate(draft.Id, true)));
        Assert.Equal(Answer(unknown), Answer(withoutAccess));
        host.Actor.Current = author;
        var withoutRole = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<PageDto>(new SetPageTemplate(shared.Id, true)));
        Assert.Equal(403, withoutRole.Status);
        // The page id names the space; were that space row gone by the time it is locked, the lock would answer with the
        // space's text. A space cannot be deleted under its pages, so the lock's answer for a missing row is put in its place.
        host.Actor.Current = admin;
        locks.BeforeNextSpaceLock(() => throw new ApiException(404, "Prostor nebyl nalezen nebo k němu nemáte přístup."));
        var vanished = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync<PageDto>(new SetPageTemplate(shared.Id, true)));
        Assert.Equal(Answer(unknown), Answer(vanished));
        using (var check = host.OpenSession())
        {
            Assert.False((await check.GetAsync<Page>(draft.Id)).IsTemplate);
            Assert.False((await check.GetAsync<Page>(shared.Id)).IsTemplate);
        }
        Assert.Equal(tree, await TreeVersionAsync(host, spaceId));

        // The role that is enough: a space Admin who is no wiki admin, on a page the Admin reads.
        host.Actor.Current = admin;
        Assert.True((await host.InvokeAsync<PageDto>(new SetPageTemplate(shared.Id, true))).IsTemplate);
    }

    [PostgreSqlFact]
    public async Task Wiki_admin_flags_another_authors_unpublished_draft_without_a_role_in_its_space()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_page_flag_wiki_");
        var wikiAdmin = await host.SeedUserAsync("wikiadmin", isWikiAdmin: true);
        var author = await host.SeedUserAsync("author");
        var spaceId = await host.SeedSpaceAsync("Templates", (author, SpaceRole.Writer));
        host.Actor.Current = author;
        var draft = await host.InvokeAsync<PageDto>(new CreatePage(new CreatePageRequest(spaceId, null, "Private", Rich, "Template content")));

        // A wiki administrator reads every draft (docs/architecture.md 10.4), so the flag and its answer stay as they were.
        host.Actor.Current = wikiAdmin;
        var flagged = await host.InvokeAsync<PageDto>(new SetPageTemplate(draft.Id, true));

        Assert.True(flagged.IsTemplate);
        Assert.Equal(("Private", Rich), (flagged.Title, flagged.ContentJson));
        using var check = host.OpenSession();
        Assert.True((await check.GetAsync<Page>(draft.Id)).IsTemplate);
    }

    private static async Task<long> TreeVersionAsync(AtlasTestHost host, Guid spaceId)
    {
        using var session = host.OpenSession();
        return await TreeVersion.ReadAsync(session, spaceId, CancellationToken.None);
    }

    /// <summary>Everything of a refusal that reaches the client: the error body is written from these and nothing else.</summary>
    private static (int Status, string? Code, string Text, bool Members) Answer(ApiException refusal)
    {
        return (refusal.Status, refusal.Code, refusal.Message, refusal.Extensions is not null);
    }
}
