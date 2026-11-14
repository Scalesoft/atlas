using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Assistant;
using Atlas.Server.Features.Assistant.Domain;
using Xunit;
using static Atlas.Server.Tests.Assistant.ConversationSeeds;

namespace Atlas.Server.Tests.Assistant;

/// <summary>Retention (plan phase 3 §8, O3): a day for an answer from the omnisearch nobody continued, 180 days for a listed
/// conversation, 10 minutes for an answer left preparing; batches in short transactions, through the deletion listeners.</summary>
public sealed class ConversationMaintenanceTests
{
    [PostgreSqlFact]
    public async Task Retention_purges_unlisted_after_a_day_listed_after_180_days_and_cancels_stale_turns()
    {
        // Private (no sharedAs): the retention purges and cancels over the whole AiConversation and AiMessage tables, and the
        // counts and batches asserted below are of the whole table.
        await using var f = await ConversationFixture.CreateAsync(DeletionLog.Register);
        var deletions = f.Host.Services.GetRequiredService<DeletionLog>();
        var now = ConversationFixture.Now();
        var page = await f.PublishAsync("Provozní řád");
        var oldSearch = await f.StartAsync(f.Reader, AiConversationOrigin.Search, now.AddHours(-25));
        await AnsweredAsync(f.Host, oldSearch, "Kdo má službu?", [Source(1, page)], Answer(("Správce.", [1])), now.AddHours(-25));
        var freshSearch = await f.StartAsync(f.Reader, AiConversationOrigin.Search, now.AddHours(-23));
        var oldListed = await f.StartAsync(f.Writer, AiConversationOrigin.Assistant, now.AddDays(-181));
        var oldListed2 = await f.StartAsync(f.Reader, AiConversationOrigin.Assistant, now.AddDays(-200));
        var keptListed = await f.StartAsync(f.Reader, AiConversationOrigin.Assistant, now.AddDays(-179));
        var (_, stale) = await f.TurnAsync(keptListed, "Kdo má službu v noci?", [Source(1, page)], now.AddMinutes(-11));
        var running = await f.StartAsync(f.Reader, AiConversationOrigin.Assistant, now.AddMinutes(-9));
        var (_, preparing) = await f.TurnAsync(running, "Kdo má službu dnes?", [Source(1, page)], now.AddMinutes(-9));
        var runner = f.Host.Services.GetRequiredService<AiConversationMaintenanceRunner>();

        // Batches of two: three expired conversations take two batches, the shorter one ends the round.
        var result = await runner.RunOnceAsync(now, default, batch: 2);

        Assert.Equal(new AiConversationMaintenanceResult(1, 3), result);
        Assert.Equal([2, 1], deletions.Calls.Select(x => x.Ids.Length));
        Assert.Equal(new[] { oldSearch.Id, oldListed.Id, oldListed2.Id }.Order(), deletions.Calls.SelectMany(x => x.Ids).Order());
        foreach (var (conversation, kept) in new[] { (oldSearch, false), (freshSearch, true), (oldListed, false), (oldListed2, false), (keptListed, true), (running, true) })
        {
            Assert.Equal(kept ? 1L : 0L, await ConversationDeleteTests.CountAsync(f, "AiConversation", "Id", conversation.Id));
        }
        var canceled = (await f.StoreAsync(store => store.MessageAsync(stale.Id, f.Reader.Id, false, default)))!.Message;
        Assert.Equal((AiMessageState.Canceled, (DateTime?)now), (canceled.State, canceled.CompletedAt));
        Assert.Equal(AiMessageState.Preparing, (await f.StoreAsync(store => store.MessageAsync(preparing.Id, f.Reader.Id, false, default)))!.Message.State);

        // A second round finds nothing.
        Assert.Equal(new AiConversationMaintenanceResult(0, 0), await runner.RunOnceAsync(now, default));
        Assert.Equal(1L, await ConversationDeleteTests.CountAsync(f, "PageRevision", "Id", page.RevisionId));
    }
}
