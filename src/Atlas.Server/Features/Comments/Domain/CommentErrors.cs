namespace Atlas.Server.Features.Comments.Domain;

/// <summary>The answers of the comment API. An object the reader may not read is the same 404 as a missing one.</summary>
public static class CommentErrors
{
    public static ApiException ObjectNotFound() => new(404, "Objekt nebyl nalezen.");

    public static ApiException NotFound() => new(404, "Komentář nebyl nalezen.");

    public static ApiException Invalid(string message) => new(400, message, "invalid_comment");

    public static ApiException Disabled() => new(403, "Komentáře jsou v tomto prostoru vypnuté.", "comments_disabled");

    public static ApiException NotYours() => new(403, "Upravit komentář smí jen jeho autor, smazat ho autor nebo správce.");

    public static ApiException Deleted() => new(409, "Komentář byl odstraněn.", "comment_deleted");
}
