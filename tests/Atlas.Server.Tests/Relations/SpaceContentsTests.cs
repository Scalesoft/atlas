using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Comments.Commands;
using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.Relations.Contracts;
using Atlas.Server.Features.TreeReferences.Commands;
using Atlas.Server.Features.TreeReferences.Contracts;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Xunit;
using Xunit.Abstractions;
using static Atlas.Server.Tests.Relations.RelationsFixture;

namespace Atlas.Server.Tests.Relations;

/// <summary>The relation sources through the space's contents and an object's relations (spec 2026-10-27 §3, §13): links, placements
/// (deleted cards, a trashed target is broken), a page's own board, mentions, and the counts, flags and filters built on them.</summary>
public sealed class SpaceContentsTests(ITestOutputHelper output)
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Every_source_counts_its_relations_and_trashed_ends_are_broken()
    {
        await using var f = await RelationsFixture.SharedAsync();
        // A wiki contact's address is unique in the whole wiki and a mention matches it only when unambiguous: one of this test's own.
        var address = $"w@{Guid.NewGuid():N}.example.com";
        var wiki = await f.ContactAsync(f.Admin, ObjectScopes.Wiki, "Wiki", address);
        var cyril = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Cyril");
        var xena = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Xena");
        var beta = await f.PageAsync("Beta");
        var tau = await f.PageAsync("Tau");
        var alfa = await f.PageAsync("Alfa", inline: [PageRef(beta.Id), PageRef(tau.Id), ContactInline(address.ToUpperInvariant())]);
        var plan = await f.BoardAsync("Plan");
        var deleted = Guid.NewGuid();
        await f.Boards.ApplyAsync(f.Writer, plan, PageCard(Guid.NewGuid(), alfa.Id, "uvod"), PageCard(Guid.NewGuid(), alfa.Id, "uvod"),
            ObjectCard(Guid.NewGuid(), wiki), ObjectCard(Guid.NewGuid(), xena), PageCard(deleted, beta.Id));
        await f.Boards.ApplyAsync(f.Writer, plan, new Atlas.Server.Features.Boards.Contracts.BoardChange("delete", ItemId: deleted));
        var deploy = await f.BoardAsync("Deploy");
        await f.AttachAsync(deploy, alfa.Id);
        await f.As<CommentDto>(f.Writer, new PostComment(alfa.Id, "Pozor", null));
        await f.TrashPageAsync(tau, f.Writer);
        await f.As(f.Writer, new TrashObject(xena));

        var contents = await f.ContentsAsync(f.Writer);
        Assert.Equal(["Alfa", "Beta", "Cyril", "Plan", "Wiki"], contents.Items.Select(row => row.Title));
        var rows = contents.Items.ToDictionary(row => row.Title);
        // Použito counts places as „Použito na“ does: Plan with its two cards of Alfa is one place (plan 3.1, ObjectUsage).
        Assert.Equal((0, 1, 1, 1), (rows["Alfa"].LinksIn, rows["Alfa"].LinksOut, rows["Alfa"].UsedCount, rows["Alfa"].Comments));
        // A link is a use too, as in „Použito na“ and atlas_list_usage: Beta is used where Alfa links to it.
        Assert.Equal((1, 0, 1), (rows["Beta"].LinksIn, rows["Beta"].LinksOut, rows["Beta"].UsedCount));
        Assert.Equal((ObjectScopes.Wiki, (Guid?)null, 2), (rows["Wiki"].Scope, rows["Wiki"].SpaceId, rows["Wiki"].UsedCount));
        Assert.Equal(new RelationCounts(1, 1, 2, 1), contents.Summary.Relations);
        Assert.Equal(new Dictionary<string, int> { ["atlas.page"] = 2, ["atlas.contact"] = 2, ["atlas.board"] = 1 }, contents.Summary.Types);
        Assert.Null(contents.NextCursor);

        Assert.Equal(["Cyril"], (await f.ContentsAsync(f.Writer, "unused")).Items.Select(row => row.Title));
        Assert.Equal(["Plan"], (await f.ContentsAsync(f.Writer, "noIncoming")).Items.Select(row => row.Title));
        Assert.Equal(["Alfa", "Plan"], (await f.ContentsAsync(f.Writer, "broken")).Items.Select(row => row.Title));
        var attached = Assert.Single((await f.ContentsAsync(f.Writer, "attached")).Items);
        Assert.Equal((deploy, (Guid?)alfa.Id, "Alfa"), (attached.Id, attached.HostId, attached.Path));
        Assert.Equal(["Alfa", "Wiki", "Beta", "Cyril", "Plan"], (await f.ContentsAsync(f.Writer, "mostUsed")).Items.Select(row => row.Title));
        Assert.Equal(["Cyril", "Wiki"], (await f.ContentsAsync(f.Writer, type: "atlas.contact")).Items.Select(row => row.Title));
        Assert.Equal(["Alfa"], (await f.ContentsAsync(f.Writer, q: "ALF")).Items.Select(row => row.Title));
        await BoardsFixture.Refused(400, () => f.ContentsAsync(f.Writer, "orphans"), "invalid_relation");

        var relations = await f.RelationsAsync(f.Writer, alfa.Id);
        Assert.Equal(alfa.Id, relations.Nodes[0].Id);
        Assert.Equal(new[]
            {
                new RelationEdgeDto(deploy, alfa.Id, RelationKinds.Attached, 1, null), new RelationEdgeDto(alfa.Id, beta.Id, RelationKinds.Link, 1, null),
                new RelationEdgeDto(alfa.Id, tau.Id, RelationKinds.Link, 1, null), new RelationEdgeDto(alfa.Id, wiki, RelationKinds.Mention, 1, null),
                new RelationEdgeDto(alfa.Id, plan, RelationKinds.Placement, 2, "uvod")
            }.OrderBy(edge => edge.Kind, StringComparer.Ordinal).ThenBy(edge => edge.SourceId).ThenBy(edge => edge.TargetId),
            relations.Edges);
        // The trashed page is a node without anything about it.
        Assert.Equal(new RelationNodeDto(tau.Id, null, null, null, null, false), relations.Nodes.Single(node => node.Id == tau.Id));
        Assert.Equal(["link"], (await f.RelationsAsync(f.Writer, alfa.Id, kinds: "link")).Edges.Select(edge => edge.Kind).Distinct());
        // Depth 2 from Beta reaches Alfa's other relations through Alfa.
        Assert.Contains((await f.RelationsAsync(f.Writer, beta.Id, depth: 2)).Edges, edge => edge.Kind == RelationKinds.Mention);
        Assert.Single((await f.RelationsAsync(f.Writer, beta.Id)).Edges);
        await BoardsFixture.Refused(404, () => f.RelationsAsync(f.Writer, tau.Id));
        await BoardsFixture.Refused(400, () => f.RelationsAsync(f.Writer, alfa.Id, depth: 3), "invalid_depth");
    }

    [PostgreSqlFact]
    public async Task Pages_of_fifty_follow_the_cursor_and_cost_the_same_statements_whatever_the_space_holds()
    {
        await using var f = await RelationsFixture.SharedAsync();
        // A neighbour from the start: depth 2 reads a second round only when there is a readable neighbour to continue from.
        var neighbour = await f.PageAsync("Soused");
        var first = await f.PageAsync("První", inline: [PageRef(neighbour.Id)]);
        var small = await StatementsAsync(() => f.ContentsAsync(f.Reader));
        var smallRelations = await StatementsAsync(() => f.RelationsAsync(f.Reader, first.Id, 2));
        output.WriteLine($"contents {small} statements, relations at depth 2 {smallRelations}");

        var pages = new List<Guid> { first.Id };
        for (var i = 0; i < 6; i++)
        {
            pages.Add((await f.PageAsync("Stránka " + i, inline: pages.Select(PageRef).ToArray())).Id);
        }
        var board = await f.BoardAsync("Plan");
        await f.Boards.ApplyAsync(f.Writer, board, pages.Select(page => PageCard(Guid.NewGuid(), page)).ToArray());
        for (var i = 0; i < 60; i++)
        {
            await f.ContactAsync(f.Writer, ObjectScopes.Space, $"Kontakt {i:00}");
        }

        Assert.Equal(small, await StatementsAsync(() => f.ContentsAsync(f.Reader)));
        Assert.Equal(smallRelations, await StatementsAsync(() => f.RelationsAsync(f.Reader, first.Id, 2)));
        Assert.True((await f.RelationsAsync(f.Reader, first.Id, 2)).Edges.Count > 10);

        var page = await f.ContentsAsync(f.Reader);
        var seen = page.Items.Select(row => row.Id).ToList();
        Assert.Equal(50, seen.Count);
        var next = await f.ContentsAsync(f.Reader, cursor: page.NextCursor);
        seen.AddRange(next.Items.Select(row => row.Id));
        Assert.Null(next.NextCursor);
        Assert.Equal(69, seen.Distinct().Count());
        await BoardsFixture.Refused(400, () => f.ContentsAsync(f.Reader, sort: "updated", cursor: page.NextCursor), "invalid_cursor");
        Assert.Equal(seen.Take(50), (await f.ContentsAsync(f.Reader)).Items.Select(row => row.Id));
    }

    private static async Task<int> StatementsAsync(Func<Task> read)
    {
        using var recording = SqlRecording.Start();
        await read();
        return recording.Statements.Count;
    }

    [PostgreSqlFact]
    public async Task Deleting_the_board_removes_its_placements()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var page = await f.PageAsync("Alfa");
        var board = await f.BoardAsync("Plan");
        await f.Boards.ApplyAsync(f.Writer, board, PageCard(Guid.NewGuid(), page.Id));
        Assert.Single((await f.RelationsAsync(f.Writer, page.Id)).Edges);
        await f.As(f.Writer, new DeleteBoard(board));
        Assert.Empty((await f.RelationsAsync(f.Writer, page.Id)).Edges);
        Assert.Equal(0, (await f.ContentsAsync(f.Writer)).Items.Single().UsedCount);
    }

    [PostgreSqlFact]
    public async Task A_reference_node_in_the_tree_uses_its_target_and_shows_where()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var cyril = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Cyril");
        var beta = await f.PageAsync("Beta");
        Assert.Equal(["Cyril"], (await f.ContentsAsync(f.Writer, "unused")).Items.Select(row => row.Title));

        await f.As<PageTreeItem>(f.Writer, new AddTreeReference(f.SpaceId, new AddTreeReferenceRequest(cyril, "kontakt", beta.Id)));

        Assert.Empty((await f.ContentsAsync(f.Writer, "unused")).Items);
        Assert.Equal(1, (await f.ContentsAsync(f.Writer, type: "atlas.contact")).Items.Single().UsedCount);
        var edge = Assert.Single((await f.RelationsAsync(f.Reader, cyril)).Edges);
        Assert.Equal((beta.Id, cyril, RelationKinds.Reference, "kontakt"), (edge.SourceId, edge.TargetId, edge.Kind, edge.Anchor));
    }

    [PostgreSqlFact]
    public async Task A_reference_at_the_root_uses_its_target_in_the_contents_only()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var cyril = await f.ContactAsync(f.Writer, ObjectScopes.Space, "Cyril");
        var jana = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, "Jana");
        await f.As<PageTreeItem>(f.Writer, new AddTreeReference(f.SpaceId, new AddTreeReferenceRequest(cyril)));
        await f.As<PageTreeItem>(f.Writer, new AddTreeReference(f.SpaceId, new AddTreeReferenceRequest(jana)));

        // The wiki contact placed at the root is in the space's contents; both are used, neither is broken.
        var contacts = await f.ContentsAsync(f.Reader, type: "atlas.contact");
        Assert.Equal([("Cyril", 1), ("Jana", 1)], contacts.Items.Select(row => (row.Title, row.UsedCount)));
        Assert.Equal(new RelationCounts(0, 0, 0, 0), contacts.Summary.Relations);
        Assert.Equal(new RelationCounts(0, 0, 0, 0), (await f.ContentsAsync(f.Admin, type: "atlas.contact")).Summary.Relations);
        // Kde je použito names no place for it: no edge, no lock.
        Assert.Empty((await f.RelationsAsync(f.Reader, cyril)).Edges);
    }
}
