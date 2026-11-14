using System.Text.Json.Nodes;
using Atlas.Server.Features.Boards.Commands;
using Atlas.Server.Features.Comments.Commands;
using Atlas.Server.Features.Comments.Contracts;
using Atlas.Server.Features.Comments.Queries;
using Atlas.Server.Features.Objects.Commands;
using Atlas.Server.Features.SpaceSettings.Commands;
using Atlas.Server.Features.SpaceSettings.Contracts;
using Atlas.Server.Features.SpaceSettings.Queries;
using Atlas.Server.Platform.Live;
using Atlas.Server.Tests.Boards;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Comments;

/// <summary>
/// Comments on objects through their commands and queries (comments spec §2, §3, §8): who reads, posts, edits and deletes, the
/// deleted placeholder, the newest-first cursor pages with a constant number of statements, the object's trash, the space switch,
/// the stored mentions and the ID-only notice to the object's view sessions.
/// </summary>
public sealed class CommentHandlerTests
{
    [PostgreSqlFact]
    public async Task Readers_comment_authors_edit_their_own_and_authors_or_managers_delete_leaving_a_placeholder()
    {
        await using var f = await CommentsFixture.SharedAsync();
        var board = await f.BoardAsync();

        var first = await f.PostAsync(f.Writer, board, "**Návrh** je hotový", anchor: "frame-1");
        var second = await f.PostAsync(f.Reader, board, "Souhlasím");
        Assert.Equal((f.Writer.Id, "writer", "**Návrh** je hotový", "frame-1", false, true, true),
            (first.AuthorId, first.AuthorName, first.Body, first.Anchor, first.Deleted, first.CanEdit, first.CanDelete));
        await BoardsFixture.Refused(404, () => f.PostAsync(f.Outsider, board, "Ahoj"));
        await BoardsFixture.Refused(404, () => f.PageOfAsync(f.Outsider, board));
        await BoardsFixture.Refused(400, () => f.PostAsync(f.Reader, board, "  "), "invalid_comment");

        var edited = await f.As<CommentDto>(f.Reader, new EditComment(board, second.Id, "Souhlasím, _díky_"));
        Assert.Equal("Souhlasím, _díky_", edited.Body);
        Assert.NotNull(edited.EditedAt);
        await BoardsFixture.Refused(403, () => f.As(f.Reader, new EditComment(board, first.Id, "Cizí")));
        await BoardsFixture.Refused(403, () => f.As(f.Writer, new EditComment(board, second.Id, "Cizí")));
        await BoardsFixture.Refused(403, () => f.As(f.Reader, new DeleteComment(board, first.Id)));
        // A comment is reached only through its own object.
        var other = await f.BoardAsync("Other");
        await BoardsFixture.Refused(404, () => f.As(f.Writer, new EditComment(other, first.Id, "Jinde")));

        // The writer sees the reader's comment but may not delete it; the space's Admin manages the object and may.
        var asWriter = await f.PageOfAsync(f.Writer, board);
        Assert.Equal([(second.Id, false, false), (first.Id, true, true)], asWriter.Items.Select(item => (item.Id, item.CanEdit, item.CanDelete)));
        Assert.Equal((2, false, true), (asWriter.Count, asWriter.HasMore, asWriter.CanPost));
        await f.As(f.Manager, new DeleteComment(board, second.Id));

        var page = await f.PageOfAsync(f.Reader, board);
        var placeholder = page.Items[0];
        Assert.Equal((second.Id, true, "", (string?)null, false, false), (placeholder.Id, placeholder.Deleted, placeholder.Body, placeholder.Anchor, placeholder.CanEdit, placeholder.CanDelete));
        Assert.Equal(1, page.Count);
        await BoardsFixture.Refused(409, () => f.As(f.Reader, new EditComment(board, second.Id, "Zpět")), "comment_deleted");
        await BoardsFixture.Refused(409, () => f.As(f.Manager, new DeleteComment(board, second.Id)), "comment_deleted");
        Assert.Equal(new CommentCount(1, first.Id), await f.As<CommentCount>(f.Reader, new GetCommentCount(board)));
        // The text is gone from the database, not only from the answer.
        using var session = f.Host.OpenSession();
        Assert.Equal("", await session.CreateSQLQuery("""SELECT "Body" FROM "Comment" WHERE "Id"=:id""").SetParameter("id", second.Id).UniqueResultAsync<string>());
    }

    /// <summary>Newest first, twenty a page, the cursor (CreatedAt, Id) stable under equal times; a page costs the same statements
    /// whatever it holds and whoever wrote it.</summary>
    [PostgreSqlFact]
    public async Task Pages_of_twenty_follow_the_cursor_with_a_constant_number_of_statements()
    {
        await using var f = await CommentsFixture.SharedAsync();
        var board = await f.BoardAsync();
        var few = await f.BoardAsync("Few");
        await f.PostAsync(f.Writer, few, "Jeden");
        await f.PostAsync(f.Reader, few, "Dva");
        var authors = new[] { f.Writer, f.Reader, f.Manager, f.Admin };
        for (var i = 0; i < 45; i++)
        {
            await f.PostAsync(authors[i % authors.Length], board, "Komentář " + i);
        }
        // Equal times: the id breaks the tie, so no comment is skipped or repeated across pages.
        await f.Host.ExecuteAsync("""UPDATE "Comment" SET "CreatedAt" = '2026-10-26T10:00:00Z' WHERE "ObjectId"=:id AND "Body" NOT LIKE '%4_'""", board);

        var seen = new List<Guid>();
        var pages = new List<(int Items, bool HasMore)>();
        Guid? before = null;
        do
        {
            var page = await f.PageOfAsync(f.Reader, board, before);
            seen.AddRange(page.Items.Select(item => item.Id));
            pages.Add((page.Items.Count, page.HasMore));
            before = page.HasMore ? page.Items[^1].Id : null;
        } while (before is not null);
        Assert.Equal([(20, true), (20, true), (5, false)], pages);
        Assert.Equal(45, seen.Distinct().Count());
        Assert.StartsWith("Komentář 4", (await f.PageOfAsync(f.Reader, board)).Items[0].Body);

        // Counted in this flow only (the session factory's statistics would count every test of the shared host).
        async Task<int> StatementsAsync(Guid objectId)
        {
            using var recording = SqlRecording.Start();
            await f.PageOfAsync(f.Reader, objectId);
            return recording.Statements.Count;
        }
        Assert.Equal(await StatementsAsync(few), await StatementsAsync(board));
    }

    [PostgreSqlFact]
    public async Task The_objects_trash_hides_its_comments_and_restoring_it_brings_them_back()
    {
        await using var f = await CommentsFixture.SharedAsync();
        var board = await f.BoardAsync();
        var comment = await f.PostAsync(f.Writer, board, "Před košem");

        await f.As(f.Writer, new DeleteBoard(board));
        await BoardsFixture.Refused(404, () => f.PageOfAsync(f.Writer, board));
        await BoardsFixture.Refused(404, () => f.PostAsync(f.Writer, board, "V koši"));
        await BoardsFixture.Refused(404, () => f.As(f.Writer, new DeleteComment(board, comment.Id)));

        await f.As(f.Manager, new RestoreObject(board));
        Assert.Equal([comment.Id], (await f.PageOfAsync(f.Reader, board)).Items.Select(item => item.Id));
    }

    [PostgreSqlFact]
    public async Task With_comments_switched_off_in_the_space_they_are_read_and_deleted_but_not_written()
    {
        await using var f = await CommentsFixture.SharedAsync();
        var board = await f.BoardAsync();
        var comment = await f.PostAsync(f.Reader, board, "Ještě zapnuto");

        var settings = await f.As<SpaceSettingsDto>(f.Manager, new GetSpaceSettings(f.SpaceId));
        Assert.True(settings.CommentsEnabled);
        settings = await f.As<SpaceSettingsDto>(f.Manager, new SaveSpaceSettings(f.SpaceId,
            new SaveSpaceSettingsRequest(settings.Name, settings.Icon, settings.Color, settings.Description, settings.PinPolicy, settings.Version, CommentsEnabled: false)));
        Assert.False(settings.CommentsEnabled);

        var page = await f.PageOfAsync(f.Reader, board);
        Assert.Equal((false, 1), (page.CanPost, page.Count));
        await BoardsFixture.Refused(403, () => f.PostAsync(f.Reader, board, "Vypnuto"), "comments_disabled");
        await BoardsFixture.Refused(403, () => f.As(f.Reader, new EditComment(board, comment.Id, "Vypnuto")), "comments_disabled");
        await f.As(f.Reader, new DeleteComment(board, comment.Id));
    }

    [PostgreSqlFact]
    public async Task Mentioned_accounts_are_stored_for_the_inbox_and_follow_edits_and_deletion()
    {
        await using var f = await CommentsFixture.SharedAsync();
        var board = await f.BoardAsync();
        string Mention(Guid id) => $"[Kolega](a-contact:?name=Kolega&internalUser.id={id})";

        var comment = await f.PostAsync(f.Writer, board, $"{Mention(f.Reader.Id)} a {Mention(Guid.NewGuid())}");
        Assert.Equal([f.Reader.Id], await MentionsAsync(f, comment.Id));
        await f.As<CommentDto>(f.Writer, new EditComment(board, comment.Id, $"{Mention(f.Manager.Id)}"));
        Assert.Equal([f.Manager.Id], await MentionsAsync(f, comment.Id));
        await f.As(f.Writer, new DeleteComment(board, comment.Id));
        Assert.Empty(await MentionsAsync(f, comment.Id));
    }

    /// <summary>The object's view sessions hear of a comment after the commit, by ids and the count only; whoever may not read the
    /// object hears nothing.</summary>
    [PostgreSqlFact]
    public async Task A_comment_is_announced_by_id_to_the_objects_readers_only()
    {
        await using var f = await CommentsFixture.SharedAsync();
        var board = await f.BoardAsync();
        var reader = f.Connect(f.Reader);
        var outsider = f.Connect(f.Outsider);
        await f.Views.JoinAsync(reader, "board:" + board, default);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Views.JoinAsync(outsider, "board:" + board, default));

        var mark = f.Mark();
        var comment = await f.PostAsync(f.Writer, board, "Tajný obsah");
        var message = Assert.Single(f.Since<ObjectChangedMessage>(mark, reader, "ObjectChanged"));
        Assert.Equal(board, message.ObjectId);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "comments", ["count"] = 1, ["latestId"] = comment.Id.ToString("D") }, message.Change));
        Assert.DoesNotContain("Tajný", message.Change!.ToJsonString());
        Assert.Empty(f.Since<ObjectChangedMessage>(mark, outsider, "ObjectChanged"));

        mark = f.Mark();
        await f.As(f.Writer, new DeleteComment(board, comment.Id));
        var deleted = Assert.Single(f.Since<ObjectChangedMessage>(mark, reader, "ObjectChanged"));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["kind"] = "comments", ["count"] = 0, ["latestId"] = null }, deleted.Change));
    }

    /// <summary>The badges of a board's cards in one call: the live counts of the objects the reader may read, nothing of the others.</summary>
    [PostgreSqlFact]
    public async Task Counts_of_many_objects_name_only_readable_objects_with_comments()
    {
        await using var f = await CommentsFixture.SharedAsync();
        var board = await f.BoardAsync();
        var quiet = await f.BoardAsync("Quiet");
        var hidden = await f.Boards.CreateBoardAsync(f.Admin, f.Boards.OtherSpaceId, "Hidden");
        await f.PostAsync(f.Writer, board, "Jeden");
        await f.PostAsync(f.Writer, board, "Dva");
        await f.PostAsync(f.Admin, hidden, "Tajné");

        var counts = await f.As<CommentCounts>(f.Reader, new GetCommentCounts([board, quiet, hidden, Guid.NewGuid()]));
        Assert.Equal(new Dictionary<Guid, int> { [board] = 2 }, counts.Counts);
        await BoardsFixture.Refused(400, () => f.As(f.Reader, new GetCommentCounts(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray())), "invalid_comment");
    }

    private static async Task<Guid[]> MentionsAsync(CommentsFixture f, Guid commentId)
    {
        using var session = f.Host.OpenSession();
        return (await session.CreateSQLQuery("""SELECT "UserId" FROM "CommentMention" WHERE "CommentId"=:id""").SetParameter("id", commentId).ListAsync<Guid>()).ToArray();
    }
}
