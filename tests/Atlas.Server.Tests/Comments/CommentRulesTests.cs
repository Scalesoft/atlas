using Atlas.Server.Features.Comments.Domain;
using Xunit;

namespace Atlas.Server.Tests.Comments;

/// <summary>The pure rules of a comment (comments spec §2, §8): its text, its anchor, its mentions and who may change it.</summary>
public sealed class CommentRulesTests
{
    private static readonly Guid Author = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    [Fact]
    public void The_body_is_markdown_of_at_most_ten_thousand_characters_without_control_or_bidi_characters()
    {
        Assert.Equal("**Díky**,\n\nhotovo", CommentText.Body("**Díky**,\n\nhotovo  \n"));
        Assert.Equal(10_000, CommentText.Body(new string('a', 10_000)).Length);
        foreach (var refused in new[] { null, "", " \n ", new string('a', 10_001), "a\0b", "tab\there", "x‮y" })
        {
            Assert.Equal("invalid_comment", Assert.Throws<ApiException>(() => CommentText.Body(refused)).Code);
        }
    }

    [Fact]
    public void The_anchor_is_an_optional_heading_or_item_id()
    {
        Assert.Null(CommentText.Anchor(null));
        Assert.Null(CommentText.Anchor(" "));
        Assert.Equal("postup-instalace", CommentText.Anchor("postup-instalace"));
        Assert.Equal("6f1c2a0e-1d2b-4c3d-9e8f-0a1b2c3d4e5f", CommentText.Anchor("6f1c2a0e-1d2b-4c3d-9e8f-0a1b2c3d4e5f"));
        foreach (var refused in new[] { "a b", "x\"onclick", new string('a', 201), "../x" })
        {
            Assert.Equal("invalid_comment", Assert.Throws<ApiException>(() => CommentText.Anchor(refused)).Code);
        }
    }

    [Fact]
    public void Mentions_are_the_internal_users_of_contact_links_once_each()
    {
        var body = $"Ahoj [Jana](a-contact:?name=Jana&internalUser.id={Author}&internalUser.name=Jana) a "
                   + $"[Petr](a-contact:?internalUser.id={Other.ToString().ToUpperInvariant()}), znovu [Jana](a-contact:?internalUser.id={Author}); "
                   + "[kontakt](a-contact:?name=Externi) a [odkaz](https://example.com/?internalUser.id=00000000-0000-0000-0000-0000000000ff).";
        Assert.Equal([Author, Other], CommentText.Mentions(body));
        Assert.Empty(CommentText.Mentions("bez zmínek"));
    }

    [Fact]
    public void Only_the_author_edits_and_the_author_or_a_manager_deletes_a_live_comment()
    {
        var comment = New();
        Assert.True(comment.MayEdit(Author));
        Assert.False(comment.MayEdit(Other));
        Assert.True(comment.MayDelete(Author, manages: false));
        Assert.True(comment.MayDelete(Other, manages: true));
        Assert.False(comment.MayDelete(Other, manages: false));

        var at = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        comment.Edit("Opraveno", at);
        Assert.Equal(("Opraveno", (DateTime?)at), (comment.Body, comment.EditedAt));

        comment.Delete(Other, at.AddMinutes(1));
        // The row stays for the timeline; its text and place are gone for good.
        Assert.Equal(("", (string?)null, (DateTime?)at.AddMinutes(1), (Guid?)Other), (comment.Body, comment.Anchor, comment.DeletedAt, comment.DeletedById));
        Assert.True(comment.IsDeleted);
        Assert.False(comment.MayEdit(Author));
        Assert.False(comment.MayDelete(Author, manages: true));
    }

    private static Comment New() => new()
    {
        Id = Guid.NewGuid(), ObjectId = Guid.NewGuid(), AuthorId = Author, Body = "Text", Anchor = "uvod", CreatedAt = DateTime.UtcNow
    };
}
