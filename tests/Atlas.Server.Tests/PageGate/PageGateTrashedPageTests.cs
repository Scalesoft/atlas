using Atlas.Server.Features.AiSuggestions.Queries;
using Atlas.Server.Features.AiWriting.Commands;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.PagePdfExports;
using Atlas.Server.Features.PageSource.Commands;
using Atlas.Server.Features.PageTemplates;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageGate;

/// <summary>
/// What the copies of the page gate answer for a page in the trash, where no feature test says it: the 404 of a missing
/// page, without a code, from every one of them today. Save, Publish, GetPage, the history read, the collaboration lease and
/// the page connections have theirs in PageHandlerTests, PageOrganizationHandlerTests and PageConnectionsHandlerTests. A row
/// stays when its copy has converted to the gate, until the last copy of the page procedure converts, as in
/// PageGateMovedPageTests.
/// </summary>
/// <remarks>
/// The answer alone does not show that a copy checks the trash itself: Restore, ReplaceSource and SetPageTemplate end in
/// PageReader, which answers a trashed page with the same 404 and the same text after the work is done, and the transaction
/// rolls back. So each row also
/// pins where the message stopped: at its own load of the page, with nothing sent after it. A copy that lost its check goes
/// on to decide the page and to write, and its row fails on the steps.
/// </remarks>
public sealed class PageGateTrashedPageTests
{
    private const string EditLadder = "advisory shared | read Page.SpaceId | Space FOR SHARE | UserAccount FOR SHARE | set actor | Page FOR UPDATE";
    private const string ReadLadder = "advisory shared | read Page.SpaceId | Space FOR SHARE | UserAccount FOR SHARE | set actor | Page FOR SHARE";

    [PostgreSqlTheory]
    [InlineData("FindLinkCandidates", ReadLadder)]
    [InlineData("WritingRead", ReadLadder)]
    [InlineData("RestorePage", EditLadder)]
    [InlineData("ReplacePageSource", EditLadder)]
    // The space role is decided before the page is loaded, which reads the Space row.
    [InlineData("SetPageTemplate", "advisory shared | read Page.SpaceId | Space FOR SHARE | UserAccount FOR SHARE | set actor | read Space | Page FOR UPDATE")]
    // Decides on the unlocked row.
    [InlineData("SavePagePdfExport", "advisory shared | read Page.SpaceId | Space FOR SHARE | UserAccount FOR SHARE | set actor | read Page")]
    public async Task A_page_in_the_trash_gets_the_answer_of_a_missing_page_from_the_copys_own_check(string message, string stoppedAfter)
    {
        await using var f = await PageGateFixture.CreateAsync();
        // A wiki admin may do each of them on a page of the space, so the answer comes from the trash alone.
        var admin = await f.Host.SeedUserAsync("admin", isWikiAdmin: true);
        var page = await f.PublishedPageAsync("Trashed");
        var revision = await f.RevisionAsync(page.Id, 1);
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        await f.As<OrganizationChange>(f.Writer, new TrashPage(page.Id, new TrashPageRequest(false, tree.Version, page.Version)));
        Func<Task> send = message switch
        {
            "FindLinkCandidates" => () => f.As<LinkCandidateLookup>(admin, new FindLinkCandidates(page.Id, [])),
            "WritingRead" => () => f.As<WritingContext>(admin, new WritingRead(page.Id, "editor.outline", "Topic")),
            "RestorePage" => () => f.As<PageDto>(admin, new RestorePage(page.Id, new RestorePageRequest(revision, page.Version))),
            "ReplacePageSource" => () => f.As<PageDto>(admin, new ReplacePageSource(page.Id, new(PageGateFixture.Rich("Edited"), "Edited", page.Version))),
            "SetPageTemplate" => () => f.As<PageDto>(admin, new SetPageTemplate(page.Id, true)),
            "SavePagePdfExport" => () => f.As<PagePdfExportView>(admin,
                new SavePagePdfExport(page.Id, new PagePdfExportRequest(false, null, "trashed", "document.pdf", false, false, 0), "https://atlas.test/")),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message, null)
        };

        using var recording = SqlRecording.Start();
        var refused = await Assert.ThrowsAsync<ApiException>(send);
        var statements = recording.Statements;

        Assert.Equal((404, (string?)null, "Stránka nebyla nalezena."), (refused.Status, refused.Code, refused.Message));
        Assert.Equal(stoppedAfter, string.Join(" | ", LockTrace.Of(statements)));
        // The load of the page is the last statement of the message: no grant query, no write and no read-back follow it.
        Assert.Equal(stoppedAfter.Split(" | ")[^1], Assert.Single(LockTrace.Of([statements[^1]])));
    }
}
