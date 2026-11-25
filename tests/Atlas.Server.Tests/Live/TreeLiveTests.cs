using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.PageDecorations;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Live;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Features.TreeReferences.Commands;
using Atlas.Server.Features.TreeReferences.Contracts;
using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.AiIndex;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Npgsql;
using Xunit;

namespace Atlas.Server.Tests.Live;

/// <summary>
/// The live tree channel <c>tree:{spaceId}</c>: every write of what a row of the space's tree shows raises one signal, whatever path wrote
/// it („Tabule nasazení“ making the page's own board, a rename, an icon, a reference node), a save that shows nothing new in the tree raises
/// none, and the listener relays it as TreeChanged to who may read the space's tree.
/// </summary>
public sealed class TreeLiveTests
{
    [PostgreSqlFact]
    public async Task Every_change_of_what_a_tree_row_shows_raises_the_spaces_tree_signal()
    {
        await using var f = await BoardsFixture.SharedAsync();
        await using var listener = new NpgsqlConnection(f.Host.ConnectionString);
        await listener.OpenAsync();
        var heard = new List<string>();
        listener.Notification += (_, e) => heard.Add(e.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN " + PageChangeListener.Channel, listener))
        {
            await listen.ExecuteNonQueryAsync();
        }
        var signal = $"{f.SpaceId}:tree";
        // Every notification of a committed command has arrived once the next round trip returns; the shared database carries other spaces too.
        async Task<int> Signals()
        {
            await using (var ping = new NpgsqlCommand("SELECT 1", listener))
            {
                await ping.ExecuteScalarAsync();
            }
            var count = heard.Count(payload => payload == signal);
            heard.Clear();
            return count;
        }

        var pages = new TestPages(f.Host);
        var page = await pages.CreateAsync(new CreatePageRequest(f.SpaceId, null, "Zakázka", IndexFixture.Rich("first"), "x"), f.Writer, default);
        Assert.Equal(1, await Signals());

        // The page's own board (projector_compose_board, „Tabule nasazení“): not a node, but the page's row gets its mark.
        var board = await f.As<BoardDto>(f.Writer, new CreateBoard(f.SpaceId, "Tabule nasazení", page.Id, "projector.board"));
        Assert.Equal(1, await Signals());
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        Assert.Equal(board.Id, tree.Items.Single(item => item.Id == page.Id).AttachedBoardId);

        // A save of the text alone shows nothing new in the tree; a new title does.
        var saved = await pages.SaveAsync(page.Id, new SavePageRequest("Zakázka", IndexFixture.Rich("second"), null, page.Version), f.Writer, default);
        Assert.Equal(0, await Signals());
        await pages.SaveAsync(page.Id, new SavePageRequest("Zakázka 2026", IndexFixture.Rich("second"), null, saved.Version), f.Writer, default);
        Assert.Equal(1, await Signals());

        // The page's icon, and a reference node added to the tree (Přidat do stromu).
        await f.As<PageDecorationState>(f.Writer, new SetPageDecoration(page.Id, new SetPageDecorationRequest("star", "red-2", null)));
        Assert.Equal(1, await Signals());
        await f.As<PageTreeItem>(f.Writer, new AddTreeReference(f.SpaceId, new AddTreeReferenceRequest(page.Id)));
        Assert.Equal(1, await Signals());
    }

    [PostgreSqlFact]
    public async Task The_tree_channel_follows_the_spaces_list_rule_and_relays_tree_changed()
    {
        await using var f = await BoardsFixture.SharedAsync();
        Task<LiveAccess> Decide(UserAccount account, string? stamp = null) =>
            f.Host.InvokeAsync<LiveAccess>(new AuthorizeTreeSubscriber(f.SpaceId, account.Id, stamp ?? account.SecurityStamp, null));
        Assert.Equal(new LiveAccess(LiveAccessOutcome.Allowed, false, 0), await Decide(f.Reader));
        Assert.Equal(LiveAccess.NotFound, await Decide(f.Outsider));
        Assert.Equal(LiveAccess.Forbidden, await Decide(f.Writer, "rotated"));

        var reader = f.SubscribeTo(f.Reader, TreeLiveChannel.Name(f.SpaceId), canWrite: false);
        var mark = f.Hub.Sent.Count;
        await new TestPages(f.Host).CreateAsync(new CreatePageRequest(f.SpaceId, null, "Nová", IndexFixture.Rich("x"), "x"), f.Writer, default);
        var expected = new TreeChangedMessage(f.SpaceId);
        for (var attempt = 0; attempt < 500 && !f.Hub.Sent.Skip(mark).Any(sent => Equals(sent.Payload, expected)); attempt++)
        {
            await Task.Delay(20);
        }
        var heard = Assert.Single(f.Hub.Sent.Skip(mark), sent => Equals(sent.Payload, expected));
        Assert.Equal(("TreeChanged", reader), (heard.Method, Assert.Single(heard.Connections)));
    }
}
