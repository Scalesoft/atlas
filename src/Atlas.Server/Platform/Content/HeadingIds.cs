using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// What a heading id of a rich document (<c>attrs.headingId</c>) may look like, and the temporary ids that
/// <see cref="RichMarkdownProjection"/> gives a heading without a stable one.
/// </summary>
public static partial class HeadingIds
{
    /// <summary>
    /// A letter or digit followed by letters, digits, <c>.</c>, <c>_</c> or <c>-</c>, 128 characters at most (the dot since Atlas 3.6, heading-anchors 2). The pattern ends in
    /// <c>$</c>, which also accepts one final newline after them; the three copies this replaced did the same.
    /// </summary>
    public static bool IsValid(string id)
    {
        return Valid().IsMatch(id);
    }

    /// <summary><c>v{version}-h{ordinal}</c>: valid only for the page version it was projected from.</summary>
    public static bool IsTemporary(string id)
    {
        return Temporary().IsMatch(id);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex Valid();

    [GeneratedRegex(@"^v\d+-h\d+$")]
    private static partial Regex Temporary();
}
