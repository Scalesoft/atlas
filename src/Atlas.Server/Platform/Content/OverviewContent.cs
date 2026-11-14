using System.Text.Json.Nodes;

namespace Atlas.Server.Platform.Content;

/// <summary>
/// What an overview (the <c>overview</c> block, src/Atlas.Web/src/overview) collects from a stored document, as its kinds' <c>collect</c> on
/// the web: the page's document, or a board as one document (its blocks, then what its other items stand for). One walk per kind, for
/// atlas_get_overview. The requirements kind is <see cref="RequirementText.Summary"/> over the same document.
/// </summary>
public static class OverviewContent
{
    public const string Requirements = "requirements";
    public const string Contacts = "contacts";
    public const string Issues = "issues";

    public static readonly IReadOnlyList<string> Kinds = [Requirements, Contacts, Issues];

    /// <summary>An issue's stored snapshot (the <c>youtrackIssue</c> node).</summary>
    public sealed record Issue(string Key, string Title, string State, string Url);

    /// <summary>Every node of the document, itself first, in document order.</summary>
    public static IEnumerable<JsonNode> Nodes(JsonNode? node)
    {
        if (node is null)
        {
            yield break;
        }
        yield return node;
        foreach (var child in node["content"] as JsonArray ?? [])
        {
            foreach (var descendant in Nodes(child))
            {
                yield return descendant;
            }
        }
    }

    private static string Type(JsonNode node) => node["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : "";

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";

    /// <summary>The people of contact cards and mentions, each once (by objectId, else by name and first e-mail), in document order;
    /// a template's empty place and an unresolved name are no person yet (pageContacts.ts).</summary>
    public static List<JsonObject> People(JsonNode? document)
    {
        var people = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes(document).Where(x => Type(x) is "contactCard" or "contactInline"))
        {
            foreach (var contact in (node["attrs"]?["data"]?["contacts"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var name = Text(contact["name"]);
                var key = Text(contact["objectId"]) is { Length: > 0 } id ? id.ToLowerInvariant() : name + "|" + Text((contact["emails"] as JsonArray)?.FirstOrDefault());
                if (name.Length > 0 && contact["unresolved"]?.GetValueKind() != System.Text.Json.JsonValueKind.True && seen.Add(key))
                {
                    people.Add(contact);
                }
            }
        }
        return people;
    }

    /// <summary>The YouTrack issues, each once by key (the first snapshot), in document order (issueOverview.ts).</summary>
    public static List<Issue> IssuesOf(JsonNode? document)
    {
        var issues = new List<Issue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in Nodes(document).Where(x => Type(x) == "youtrackIssue"))
        {
            var attrs = node["attrs"];
            if (Text(attrs?["key"]) is { Length: > 0 } key && seen.Add(key))
            {
                issues.Add(new Issue(key, Text(attrs?["title"]), Text(attrs?["state"]), Text(attrs?["url"])));
            }
        }
        return issues;
    }
}
