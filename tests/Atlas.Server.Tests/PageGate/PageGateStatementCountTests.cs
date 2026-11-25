using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Markdown.Sync;
using Xunit;

namespace Atlas.Server.Tests.PageGate;

/// <summary>
/// Exact SQL round-trips of the hot page paths once warm, counted for the message alone (handler and transaction, no HTTP
/// authentication). PerformanceBudgetHttpTests bounds the reads from above with slack and does not cover save or append; a
/// change of the gate must leave these numbers as they are unless it says otherwise.
/// </summary>
public sealed class PageGateStatementCountTests
{
    [PostgreSqlFact]
    public async Task Hot_page_paths_send_exactly_these_statements_once_warm()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var page = await f.PublishedPageAsync("Counted");
        var typed = await f.PublishedPageAsync("Typed");
        var live = await f.As<WikiCollaborationSession>(f.Writer, new OpenCollaboration(typed.Id));
        // The warm-up: each message once before it is counted.
        await f.As<PageDto>(f.Reader, new GetPage(page.Id));
        await f.As<PageDto>(f.Writer, Save(page.Id, "Warm", 1));
        await f.As<PageTreeDto>(f.Reader, new GetPageTree(f.SpaceId));
        await f.As<AppendUpdateResult>(f.Writer, Append(typed.Id, live.Generation, f.Writer.Id));

        (string Message, int Expected, IReadOnlyList<string> Sent)[] counted =
        [
            // 12: the publication the page inherits (3.6-I Živě) comes with the space lock, not a statement of its own.
            ("GetPage by a reader", 12, await f.RecordAsync<PageDto>(f.Reader, new GetPage(page.Id))),
            // 25 since 2026110930: the revision's contribution and notification and the page's envelope are statements of their own;
            // 26 since 2026110935: the working search document too (SearchDocuments).
            // 22: the read-back keeps the account locked by the gate (no second lock, re-read and revocation check: - 3), and the
            // revision's contribution and notification go with the page's envelope (- 1). Its text is not the published one: a save
            // back to the publication carries the publication over (97138c0b8), seven statements more.
            ("SavePage with new content", 22, await f.RecordAsync<PageDto>(f.Writer, Save(page.Id, "Changed", 2))),
            // 12 since 5.2: the verification states of the space's pages, for the link chips.
            ("GetPageTree by a reader", 12, await f.RecordAsync<PageTreeDto>(f.Reader, new GetPageTree(f.SpaceId))),
            // 13 since PRF-S-7: the account is read under its lock in one statement (15 since COL2-6: the pending totals live on the head);
            // 12 since e28bb5c4f: the wiki administrator status is read once per transaction.
            ("AppendUpdate", 12, await f.RecordAsync<AppendUpdateResult>(f.Writer, Append(typed.Id, live.Generation, f.Writer.Id))),
        ];

        // A changed count is diagnosed from the statements, so a failure prints them, one per line in the order they were sent.
        var differences = counted.Where(x => x.Sent.Count != x.Expected).Select(x =>
            $"{x.Message}: {x.Sent.Count} statements, expected {x.Expected}{Environment.NewLine}"
            + string.Join(Environment.NewLine, x.Sent.Select((sql, index) => $"  {index + 1}. {string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}")));
        Assert.True(counted.All(x => x.Sent.Count == x.Expected), string.Join(Environment.NewLine + Environment.NewLine, differences));
    }

    private static SavePage Save(Guid pageId, string text, int expectedVersion)
    {
        return new SavePage(pageId, new SavePageRequest("Counted", PageGateFixture.Rich(text), text, expectedVersion));
    }

    private static AppendUpdate Append(Guid pageId, Guid generation, Guid authorId)
    {
        return new AppendUpdate(pageId, generation, Guid.NewGuid(), authorId, DateTimeOffset.UtcNow, [1, 2, 3]);
    }
}
