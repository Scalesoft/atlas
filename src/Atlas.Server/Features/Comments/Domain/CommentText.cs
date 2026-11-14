using System.Text.RegularExpressions;

namespace Atlas.Server.Features.Comments.Domain;

/// <summary>What a comment may say and where it may point (comments spec §2), and whom it mentions (§6).</summary>
public static partial class CommentText
{
    public const int MaxBody = 10_000;
    public const int MaxAnchor = 200;
    /// <summary>More mentions are kept as links, not as accounts to notify.</summary>
    public const int MaxMentions = 50;

    /// <summary>Markdown of a board text's characters (no NUL, control or bidi characters but line breaks), trailing space trimmed.</summary>
    public static string Body(string? body)
    {
        var text = body?.TrimEnd() ?? "";
        if (text.Trim().Length == 0)
        {
            throw CommentErrors.Invalid("Napište text komentáře.");
        }
        if (text.Length > MaxBody)
        {
            throw CommentErrors.Invalid($"Komentář smí mít nejvýše {MaxBody} znaků.");
        }
        return Rules.IsMultiLineText(text) ? text : throw CommentErrors.Invalid("Komentář nesmí obsahovat řídicí znaky.");
    }

    /// <summary>A heading or item id: letters, digits, '-', '_', ':' and '.'; blank is none.</summary>
    public static string? Anchor(string? anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return null;
        }
        return anchor.Length <= MaxAnchor && anchor.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.')
            ? anchor
            : throw CommentErrors.Invalid("Místo komentáře není platné.");
    }

    /// <summary>The accounts of the editor's contact mentions (<c>[Jana](a-contact:?…&amp;internalUser.id=…)</c>), first ones first.</summary>
    public static IReadOnlyList<Guid> Mentions(string body)
    {
        return MentionPattern().Matches(body).Select(match => Guid.Parse(match.Groups[1].Value)).Distinct().Take(MaxMentions).ToArray();
    }

    [GeneratedRegex(@"\]\(a-contact:\?(?:[^)\s]*&)?internalUser\.id=([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})")]
    private static partial Regex MentionPattern();
}
