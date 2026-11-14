using System.Text;
using System.Text.Json.Nodes;
using Atlas.Server.Features.Mcp.Contracts;
using Atlas.Server.Features.PageLinks;
using Atlas.Server.Features.PageLinks.Contracts;
using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Platform.Content;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Mcp.Publishing;

/// <summary>
/// The summary of a publication over MCP (plan C3), shown to the person who confirms it in the web and, as counts, to the model:
/// title, space, versions, the last change's author and channel, the sections that differ from the publication and the link
/// findings of the check before publication (P5, <see cref="PageLinkStore.CheckAsync"/>). The caller has already decided EditContent
/// on the page in the same transaction. Never page text. Virtual for a test seam between the tool's two transactions.
/// </summary>
public class McpPublishSummaries
{
    private readonly ISession m_session;
    private readonly PageLinkStore m_links;

    public McpPublishSummaries(ISession session, PageLinkStore links)
    {
        m_session = session;
        m_links = links;
    }

    public virtual async Task<McpPublishSummary> BuildAsync(UserAccount actor, Page page, CancellationToken ct)
    {
        var working = await m_session.QueryOver<PageRevision>().Where(x => x.PageId == page.Id && x.ActiveTo == null).SingleOrDefaultAsync(ct)
            ?? throw new ApiException(404, McpVisibility.MissingPage);
        var published = page.PublishedRevisionId is { } publishedId ? await m_session.GetAsync<PageRevision>(publishedId, ct) : null;
        var space = await m_session.GetAsync<Space>(page.SpaceId, ct);
        var author = await m_session.GetAsync<UserAccount>(working.AuthorId, ct);
        var findings = new PublicationCheck(page.Version, await m_links.CheckAsync(actor, page.Id, working.Id, ct));
        return new McpPublishSummary(page.Id, page.SpaceId, working.Title, space?.Name ?? "", page.Version, published?.Version, published is null,
            ChangedSections(published?.ContentJson, working.ContentJson), author?.DisplayName, ContentChannels.ForRead(working.Channel), findings);
    }

    /// <summary>
    /// Sections that were added, removed or changed between the publication and the working revision. A section is a heading and its
    /// own blocks up to the next heading of any level (subsections count on their own), plus the blocks before the first heading.
    /// Sections pair by headingId, or by level and text for a heading without one, and compare structurally on the rich JSON, so a
    /// changed component counts as well. Without a publication every section is new.
    /// </summary>
    public static int ChangedSections(string? publishedJson, string workingJson)
    {
        var working = Sections(workingJson);
        if (publishedJson is null)
        {
            return working.Count;
        }
        var published = Sections(publishedJson);
        var changed = working.Count(section => !published.TryGetValue(section.Key, out var before) || !Same(before, section.Value));
        return changed + published.Keys.Count(key => !working.ContainsKey(key));
    }

    private static bool Same(IReadOnlyList<JsonObject> left, IReadOnlyList<JsonObject> right)
        => left.Count == right.Count && left.Zip(right).All(pair => RichDocument.StructurallyEqual(pair.First, pair.Second));

    private static Dictionary<string, List<JsonObject>> Sections(string contentJson)
    {
        var sections = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        var current = new List<JsonObject>();
        var key = "";
        foreach (var block in RichDocument.Parse(contentJson).Blocks)
        {
            if (RichDocument.TypeOf(block) == "heading")
            {
                Close(sections, key, current);
                key = Unique(sections, KeyOf(block));
                current = [];
            }
            current.Add(block);
        }
        Close(sections, key, current);
        return sections;
    }

    private static void Close(Dictionary<string, List<JsonObject>> sections, string key, List<JsonObject> blocks)
    {
        // The text before the first heading is a section only when there is some.
        if (key.Length > 0 || blocks.Count > 0)
        {
            sections[key] = blocks;
        }
    }

    private static string Unique(Dictionary<string, List<JsonObject>> sections, string key)
    {
        var candidate = key;
        for (var n = 2; sections.ContainsKey(candidate); n++)
        {
            candidate = key + "#" + n;
        }
        return candidate;
    }

    private static string KeyOf(JsonObject heading)
    {
        var attrs = heading["attrs"] as JsonObject;
        if (attrs?["headingId"] is JsonValue id && id.TryGetValue<string>(out var value) && HeadingIds.IsValid(value))
        {
            return "id:" + value;
        }
        var level = attrs?["level"] is JsonValue number && number.TryGetValue<int>(out var parsed) ? parsed : 1;
        var text = new StringBuilder();
        Collect(heading, text);
        return "h" + level + ":" + text.ToString().Trim();
    }

    private static void Collect(JsonNode? node, StringBuilder text)
    {
        if (node is not JsonObject value)
        {
            return;
        }
        if (value["text"] is JsonValue part && part.TryGetValue<string>(out var s))
        {
            text.Append(s);
        }
        if (value["content"] is JsonArray children)
        {
            foreach (var child in children)
            {
                Collect(child, text);
            }
        }
    }
}
