using Atlas.Markdown.Sync;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// One contact per block (owner 2026-10-30, Atlas 3.2): how a ```a-contact body of several items becomes several cards. The stored
/// cards of several contacts are split by <see cref="LegacyDocumentRepair"/> wherever content is read.
/// </summary>
public static class ContactCardSplit
{
    /// <summary>
    /// The items of a ```a-contact body as the editor splits it (<c>sequenceItems</c> in markdownDialect.ts): the <c>-</c> lines at the
    /// first item's indentation, each with its lines up to the next one (the first also with what precedes it, a comment) and the
    /// offset of its <c>-</c> line from the fence line, where its errors point; one part (offset 0, the fence) when the first line of
    /// content is no item or there are fewer than two. Each part is validated on its own.
    /// </summary>
    public static IReadOnlyList<(string Text, int Line)> SequenceItems(string source)
    {
        var lines = source.Split('\n');
        var first = lines.FirstOrDefault(text => text.Trim().Length > 0 && !text.TrimStart().StartsWith('#'));
        var indent = first is null ? -1 : first.Length - first.TrimStart(' ').Length;
        bool Item(string text) => indent >= 0 && text.Length > indent && text[indent] == '-' && text[..indent].All(c => c == ' ')
            && (text.Length == indent + 1 || char.IsWhiteSpace(text[indent + 1]));
        var starts = Enumerable.Range(0, lines.Length).Where(index => Item(lines[index])).ToList();
        if (starts.Count < 2 || first is null || !Item(first))
        {
            return [(source, 0)];
        }
        return starts.Select((start, index) =>
        {
            var from = index == 0 ? 0 : start;
            return (string.Join('\n', lines[from..(index + 1 < starts.Count ? starts[index + 1] : lines.Length)]).TrimEnd('\n'), start + 1);
        }).ToList();
    }
}
