using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Xunit;
using static Atlas.Server.Tests.Relations.RelationsFixture;

namespace Atlas.Server.Tests.Relations;

/// <summary>
/// Spec §6: a reader without the right sees no row, edge, count or flag of what it may not read, and an unreadable end of an
/// object's relation is a lock without a name. Every actor kind of the handler (the API token and MCP are the same reader behind
/// another door: RelationHttpTests).
/// </summary>
public sealed class RelationRightsTests
{
    [PostgreSqlFact]
    public async Task Rows_counts_and_flags_follow_each_readers_rights()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var shared = await f.PageAsync("Pub");
        var draft = await f.PageAsync("Draft", publish: false);
        // A page of its own access without grants: only the space's Admin and the WikiAdmin read it.
        var secret = await f.PageAsync("Secret", inline: [PageRef(shared.Id)]);
        await f.Host.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=:id""", secret.Id);
        var deploy = await f.BoardAsync("Deploy");
        await f.AttachAsync(deploy, shared.Id);
        var own = await f.ContactAsync(f.Writer, ObjectScopes.Private, "Own");
        var plan = await f.BoardAsync("Plan");
        await f.Boards.ApplyAsync(f.Writer, plan, ObjectCard(Guid.NewGuid(), own));

        // (rows, links into Pub, attached, uses of Own as its owner sees them)
        var expected = new[]
        {
            (f.Admin, "Draft|Plan|Pub|Secret", 1, 1),
            (f.Manager, "Plan|Pub|Secret", 1, 1),
            (f.Writer, "Draft|Own|Plan|Pub", 0, 1),
            (f.Reader, "Plan|Pub", 0, 0)
        };
        foreach (var (actor, rows, linksIn, attached) in expected)
        {
            var contents = await f.ContentsAsync(actor);
            Assert.Equal((actor.Username, rows), (actor.Username, string.Join("|", contents.Items.Select(row => row.Title))));
            Assert.Equal((actor.Username, linksIn, attached), (actor.Username, contents.Items.Single(row => row.Title == "Pub").LinksIn, contents.Summary.Relations.Attached));
            // The card of a private contact the reader may not read is neither a use nor broken.
            Assert.Equal((actor.Username, 0), (actor.Username, contents.Summary.Relations.Broken));
            Assert.Equal(contents.Items.Count, contents.Summary.Types.Values.Sum());
        }
        Assert.Equal((1, ObjectScopes.Private), (await f.ContentsAsync(f.Writer)).Items.Where(row => row.Title == "Own").Select(row => (row.UsedCount, row.Scope)).Single());
        await BoardsFixture.Refused(404, () => f.ContentsAsync(f.Outsider));

        // Kde je použito: the unreadable linking page is a lock, the readable reader's own view of Pub otherwise.
        var asReader = await f.RelationsAsync(f.Reader, shared.Id);
        Assert.Equal(new RelationNodeDto(secret.Id, null, null, null, null, false), asReader.Nodes.Single(node => node.Id == secret.Id));
        Assert.False(asReader.Nodes.Single(node => node.Id == deploy).Readable);
        Assert.Equal("Secret", (await f.RelationsAsync(f.Manager, shared.Id)).Nodes.Single(node => node.Id == secret.Id).Title);
        await BoardsFixture.Refused(404, () => f.RelationsAsync(f.Reader, secret.Id));
        await BoardsFixture.Refused(404, () => f.RelationsAsync(f.Reader, deploy));
        await BoardsFixture.Refused(404, () => f.RelationsAsync(f.Outsider, shared.Id));
        await BoardsFixture.Refused(404, () => f.RelationsAsync(f.Admin, own));
        Assert.Equal([plan], (await f.RelationsAsync(f.Writer, own)).Edges.Select(edge => edge.TargetId));
        // To the reader the private card's object is a lock on the board, never named.
        Assert.Equal([false], (await f.RelationsAsync(f.Reader, plan)).Nodes.Where(node => node.Id == own).Select(node => node.Readable));
    }

    [PostgreSqlFact]
    public async Task A_drafts_own_board_is_as_hidden_as_its_draft()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var draft = await f.PageAsync("Draft", publish: false);
        var title = "Draft board";
        var deploy = await f.BoardAsync(title);
        await f.AttachAsync(deploy, draft.Id);

        // The space's Admin may write the page, yet another's draft is not listed to it (PageListing), so neither is the draft's board.
        await BoardsFixture.Refused(404, () => f.RelationsAsync(f.Manager, draft.Id));
        await BoardsFixture.Refused(404, () => f.RelationsAsync(f.Manager, deploy));
        var contents = await f.ContentsAsync(f.Manager, "attached");
        Assert.Empty(contents.Items);
        Assert.Equal(0, contents.Summary.Relations.Attached);
        Assert.Equal([title], (await f.ContentsAsync(f.Writer, "attached")).Items.Select(row => row.Title));
        Assert.Equal([title], (await f.ContentsAsync(f.Admin, "attached")).Items.Select(row => row.Title));
    }

    [PostgreSqlFact]
    public async Task A_trashed_target_is_broken_only_for_who_could_read_it()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var target = await f.PageAsync("Target");
        await f.Host.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=:id""", target.Id);
        await f.PageAsync("Source", inline: [PageRef(target.Id), PageRef(Guid.NewGuid())]);
        await f.TrashPageAsync(target, f.Manager);

        // The space's Admin could read the trashed page: broken. The writer could not: nothing. A link to a page that never existed
        // is broken only to the WikiAdmin, who reads everything; to anyone else it is as unreadable as a hidden one.
        Assert.Equal(1, (await f.ContentsAsync(f.Manager)).Summary.Relations.Broken);
        Assert.Equal(0, (await f.ContentsAsync(f.Writer)).Summary.Relations.Broken);
        Assert.Equal(["Source"], (await f.ContentsAsync(f.Admin, "broken")).Items.Select(row => row.Title));
        Assert.Equal(0, (await f.ContentsAsync(f.Writer)).Items.Single(row => row.Title == "Source").LinksOut);
    }
}
