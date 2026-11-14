using Atlas.Server.Features.AiSuggestions.Queries;
using Atlas.Server.Features.AiWriting.Commands;
using Atlas.Server.Features.Attachments.Contracts;
using Atlas.Server.Features.PageHistory.Commands;
using Atlas.Server.Features.PageHistory.Queries;
using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Features.PageLinks.Queries;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageSource.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.PersonalLibrary.Commands;
using Atlas.Server.Features.PersonalLibrary.Contracts;
using Atlas.Server.Features.PersonalLibrary.Queries;
using Xunit;

namespace Atlas.Server.Tests.PageGate;

/// <summary>
/// What each copy of the page gate answers when the page moves to another space between the copy's look at the page's space
/// and its space row lock. The answers differ between the copies today and are kept (owner decision 2026-10-10): a row is
/// added before a copy converts to the gate and stays when it has converted, as the answer the message keeps through the
/// gate, until the last copy of the page procedure converts.
/// </summary>
public sealed class PageGateMovedPageTests
{
    [PostgreSqlTheory]
    [InlineData("SavePage", 409, "space_changed")]
    [InlineData("RenamePage", 409, "space_changed")]
    [InlineData("GetPage", 404, null)]
    [InlineData("RestorePage", 404, null)]
    [InlineData("StoreAttachment", 409, null)]
    [InlineData("GetPageConnections", 404, null)]
    [InlineData("FindLinkCandidates", 404, null)]
    [InlineData("WritingRead", 404, null)]
    [InlineData("GetPageHistory", 404, null)]
    [InlineData("GetPersonalPageState", 404, null)]
    [InlineData("RecordPageVisit", 404, null)]
    [InlineData("ReplacePageSource", 409, "space_changed")]
    public async Task A_page_moved_before_the_space_lock_gets_the_answer_of_its_copy(string message, int status, string? code)
    {
        await using var f = await PageGateFixture.CreateAsync();
        var page = await f.PublishedPageAsync("Moved");
        // The actor may write in the target too, so the answer comes from the move alone.
        var target = await f.Host.SeedSpaceAsync("Elsewhere", (f.Writer, SpaceRole.Writer));
        var revision = await f.RevisionAsync(page.Id, 1);
        var upload = await f.StoredAsync(page.Id);
        // The source of a page is replaced with ManageAccess, which the Writer lacks: a wiki administrator sends that one, so
        // its answer comes from the move alone as well.
        var admin = await f.Host.SeedUserAsync("admin", isWikiAdmin: true);
        Func<Task> send = message switch
        {
            "SavePage" => () => f.As<PageDto>(f.Writer, new SavePage(page.Id, new SavePageRequest("Moved", PageGateFixture.Rich("Edited"), "Edited", page.Version))),
            "RenamePage" => () => f.As(f.Writer, new RenamePage(page.Id, new RenamePageRequest("Renamed", page.Version))),
            "GetPage" => () => f.As<PageDto>(f.Writer, new GetPage(page.Id)),
            "RestorePage" => () => f.As<PageDto>(f.Writer, new RestorePage(page.Id, new RestorePageRequest(revision, page.Version))),
            "StoreAttachment" => () => f.As<AttachmentDto>(f.Writer, upload),
            "GetPageConnections" => () => f.As<PageConnections>(f.Writer, new GetPageConnections(page.Id, 10)),
            "FindLinkCandidates" => () => f.As<LinkCandidateLookup>(f.Writer, new FindLinkCandidates(page.Id, [])),
            "WritingRead" => () => f.As<WritingContext>(f.Writer, new WritingRead(page.Id, "editor.outline", "Topic")),
            "GetPageHistory" => () => f.As<PageHistoryList>(f.Writer, new GetPageHistory(page.Id, null, 20)),
            "GetPersonalPageState" => () => f.As<PersonalPageState>(f.Writer, new GetPersonalPageState(page.Id)),
            "RecordPageVisit" => () => f.As<PersonalPageState>(f.Writer, new RecordPageVisit(page.Id)),
            "ReplacePageSource" => () => f.As<PageDto>(admin, new ReplacePageSource(page.Id, new(PageGateFixture.Rich("Edited"), "Edited", page.Version))),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message, null)
        };
        var moved = false;
        RecordingLocks.Start().BeforeNextSpaceLock(async () =>
        {
            await f.Host.SeedAsync(session => session.CreateSQLQuery("""UPDATE "Page" SET "SpaceId"=:target WHERE "Id"=:page""")
                .SetParameter("target", target).SetParameter("page", page.Id).ExecuteUpdateAsync());
            moved = true;
        });

        var refused = await Assert.ThrowsAsync<ApiException>(send);

        Assert.Equal((status, code), (refused.Status, refused.Code));
        Assert.True(moved, "The page was to move before the first space row lock, and no space row lock was taken.");
    }
}
