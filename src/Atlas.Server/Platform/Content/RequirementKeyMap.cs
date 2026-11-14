using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Content;

/// <summary>One requirement of a working or published revision, as <c>ProjectorRequirementIndex</c> holds it (<c>BlockId</c>,
/// <c>Key</c>, <c>Title</c>), or a requirement heading of a page already converted: then <paramref name="HeadingId"/> is fixed and
/// <paramref name="Key"/> one of its ids (the heading id or an alias).</summary>
public sealed record RequirementKeyRow(Guid PageId, bool IsWorking, string BlockId, string Key, string Title, string? HeadingId = null);

/// <summary>A key that more than one block of a page holds (in one revision or across the working and the published one).</summary>
public sealed record RequirementDuplicateKey(Guid PageId, string Key, IReadOnlyList<string> BlockIds);

/// <summary>
/// The global map of the requirement conversion (plan 2026-11-04 §5, model §10b): the heading id of every old block, the same in the
/// working and the published revision (keyed by the block id), and <c>(page, key) → heading id</c> for the dependency lines. A block's
/// heading id is its key when the key fits the heading charset and no other block of the page holds it, else its old id. A repeated key
/// resolves to its last block, the working revision's over the published one's (as the web's Map kept the last entry), and is reported.
/// </summary>
public sealed partial class RequirementKeyMap
{
    private readonly Dictionary<(Guid Page, string Block), string> headingIds = [];
    private readonly Dictionary<(Guid Page, string Key), (string HeadingId, string Title)> keys = [];

    public IReadOnlyList<RequirementDuplicateKey> Duplicates { get; private set; } = [];

    /// <summary>The heading id charset of <c>heading-anchors</c> (the dot is new in schema 3).</summary>
    public static bool IsHeadingId(string id) => HeadingIdPattern().IsMatch(id);

    private IReadOnlySet<Guid>? read;

    /// <summary>Whether the map read the page's rows (it may hold none): true for every page of a map built without <c>read</c>.</summary>
    public bool HasRead(Guid pageId) => read is null || read.Contains(pageId);

    /// <param name="read">The pages whose rows were read (a page may have none); null: all pages are.</param>
    public static RequirementKeyMap Build(IEnumerable<RequirementKeyRow> rows, IEnumerable<Guid>? read = null)
    {
        var map = new RequirementKeyMap { read = read?.ToHashSet() };
        var duplicates = new List<RequirementDuplicateKey>();
        // Published rows first, so a working row is the one a repeated key and a block's own key are taken from.
        foreach (var page in rows.OrderBy(row => row.IsWorking).GroupBy(row => row.PageId))
        {
            var blocks = new Dictionary<string, (string Key, string Title, string? Fixed)>(StringComparer.Ordinal);
            var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var row in page)
            {
                blocks[row.BlockId] = (row.Key, row.Title, row.HeadingId);
                if (row.Key.Length == 0)
                {
                    continue;
                }
                var holders = owners.TryGetValue(row.Key, out var list) ? list : owners[row.Key] = [];
                holders.Remove(row.BlockId);
                holders.Add(row.BlockId);
            }
            foreach (var (block, (key, _, heading)) in blocks)
            {
                map.headingIds[(page.Key, block)] = heading
                    ?? (IsHeadingId(key) && owners[key].Count == 1 && (key == block || !blocks.ContainsKey(key)) ? key : block);
            }
            foreach (var (key, holders) in owners)
            {
                var last = holders[^1];
                map.keys[(page.Key, key)] = (map.headingIds[(page.Key, last)], blocks[last].Title);
                if (holders.Count > 1)
                {
                    duplicates.Add(new RequirementDuplicateKey(page.Key, key, holders));
                }
            }
        }
        map.Duplicates = duplicates;
        return map;
    }

    /// <summary>The heading id of an old block; null when the map has not read the block.</summary>
    public string? HeadingId(Guid pageId, string blockId) => headingIds.GetValueOrDefault((pageId, blockId));

    /// <summary>The heading a dependency names, with the target's title; null when no block of that page holds the key.</summary>
    public (string HeadingId, string Title)? Resolve(Guid pageId, string key)
        => keys.TryGetValue((pageId, key), out var target) ? target : null;

    /// <summary>The rows of a stored revision in the old shape: every <c>requirement</c> block, nested ones included.</summary>
    public static IEnumerable<RequirementKeyRow> Rows(Guid pageId, bool isWorking, JsonNode document)
        => RequirementHeadingConverter.Blocks(document).Select(block => new RequirementKeyRow(pageId, isWorking,
            RequirementText.Value(block, "id"), RequirementText.Value(block, "key"), RequirementText.Title(block)));

    /// <summary>The rows of a converted revision: each requirement heading under its own id and every alias (the old id and key).</summary>
    public static IEnumerable<RequirementKeyRow> ConvertedRows(Guid pageId, bool isWorking, JsonNode document)
    {
        foreach (var heading in (document["content"] as JsonArray ?? []).Where(RequirementHeadingReader.IsRequirementHeading))
        {
            var id = RequirementText.Value(heading, "headingId");
            var title = RequirementHeadingReader.Text(heading).Trim();
            foreach (var key in RequirementText.Value(heading, "aliases").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Prepend(id).Distinct())
            {
                yield return new RequirementKeyRow(pageId, isWorking, id, key, title, id);
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex HeadingIdPattern();
}
