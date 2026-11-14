using Atlas.Server.Features.Comments.Commands;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Platform.Resources;
using Xunit;

namespace Atlas.Server.Tests.Comments;

/// <summary>Comments in search (comments spec §4): their own „Komentář“ hits linking to the object and its anchor, only for readers
/// of the object, following edits and deletion, and never changing the pages' results.</summary>
public sealed class CommentSearchTests
{
    [PostgreSqlFact]
    public async Task A_comment_is_found_by_readers_of_its_object_beside_unchanged_page_results()
    {
        await using var f = await CommentsFixture.SharedAsync();
        // A word of the test's own: the admin reads every test's comments on the shared host, and the hits are capped.
        var term = "kube" + string.Concat(Guid.NewGuid().ToString("N")[..8].Select(c => (char)('a' + c % 26)));
        var page = await f.PageAsync($"{term} runbook", $"Upgrade the {term} cluster");
        await f.PageAsync($"{term} notes", $"More {term}");
        var board = await f.BoardAsync("Roadmap");
        var hidden = await f.Boards.CreateBoardAsync(f.Admin, f.Boards.OtherSpaceId, "Hidden");
        var pagesBefore = await PagesAsync(f, f.Reader, term);

        var onPage = await f.PostAsync(f.Writer, page, $"{term} upgrade postponed, ask [Jana](a-contact:?name=Jana&internalUser.id=00000000-0000-0000-0000-0000000000aa)", "steps");
        await f.PostAsync(f.Reader, board, $"{term} **{term}** {term} on the board");
        await f.PostAsync(f.Admin, hidden, $"{term} secret plans");

        // The pages answer exactly as before; the comments come as their own group.
        Assert.Equal(pagesBefore, await PagesAsync(f, f.Reader, term));
        var hits = await CommentsAsync(f, f.Reader, term);
        Assert.Equal([board, page], hits.Select(hit => hit.Target!.ObjectId));
        var pageHit = hits[1];
        Assert.Equal((onPage.Id, f.SpaceId, "Discussion", $"{term} runbook", "atlas.page", "steps"),
            (pageHit.Id, pageHit.SpaceId, pageHit.SpaceName, pageHit.Title, pageHit.Target!.TypeId, pageHit.Target.Anchor));
        Assert.Contains("postponed", pageHit.Snippet);
        // A mention's link target is not text: its parameters are neither found nor shown.
        Assert.DoesNotContain("internalUser", pageHit.Snippet);
        Assert.Empty(await CommentsAsync(f, f.Reader, "internaluser"));
        Assert.Empty(await CommentsAsync(f, f.Outsider, term));
        Assert.Equal(3, (await CommentsAsync(f, f.Admin, term)).Count);

        await f.As(f.Writer, new DeleteComment(page, onPage.Id));
        Assert.Equal([board], (await CommentsAsync(f, f.Reader, term)).Select(hit => hit.Target!.ObjectId));
    }

    private static async Task<SearchResponse> SearchAsync(CommentsFixture f, UserAccount actor, string query)
    {
        return await f.As<SearchResponse>(actor, new Atlas.Server.Features.Search.Queries.Search(query, null, 0, 20, new SearchOptions()));
    }

    private static async Task<string> PagesAsync(CommentsFixture f, UserAccount actor, string query)
    {
        return string.Join(", ", (await SearchAsync(f, actor, query)).Items.Select(item => item.Id + ":" + item.Snippet));
    }

    private static async Task<IReadOnlyList<SearchResourceHit>> CommentsAsync(CommentsFixture f, UserAccount actor, string query)
    {
        return ((await SearchAsync(f, actor, query)).Resources ?? []).Where(hit => hit.Type == "comment").ToArray();
    }
}
