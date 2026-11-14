using System.Text.Json;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Platform.Content;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Features.Search.Domain;

// Reads the widget snapshots from the exact revision selected by the authorized search query.
public static class SearchWidgets
{
    public static IReadOnlyList<SearchWidget> Read(string json, string normalizedQuery)
    {
        using var document = JsonDocument.Parse(json, MarkdownDocumentValidator.JsonOptions);
        var tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<SearchWidget>();
        string? headingId = null;
        foreach (var node in document.RootElement.EnumerateArray())
        {
            if (results.Count >= 20) break;
            var type = Text(node, "type");
            if (!node.TryGetProperty("attrs", out var attrs) || attrs.ValueKind != JsonValueKind.Object) continue;
            if (type == "heading")
            {
                headingId = Text(attrs, "headingId");
            }
            else
            {
                results.AddRange(ReadNode(type, attrs, headingId, tokens).Take(20 - results.Count));
            }
        }
        return results.Distinct().ToArray();
    }

    private static IEnumerable<SearchWidget> ReadNode(string? type, JsonElement attrs, string? headingId, string[] tokens)
    {
        if (type == "youtrackIssue")
        {
            if (ReadYouTrack(attrs, headingId, tokens) is { } issue) yield return issue;
            yield break;
        }
        foreach (var contact in ReadContacts(ContactSnapshots.Of(type, attrs), headingId, tokens)) yield return contact;
    }

    private static SearchWidget? ReadYouTrack(JsonElement attrs, string? headingId, string[] tokens)
    {
        if (Text(attrs, "title") is not { } title || Text(attrs, "key") is not { } key) return null;
        var state = Text(attrs, "state");
        if (!Matches($"{key} {title} {state}", tokens)) return null;
        return new("youtrack", title, key, headingId)
        {
            Key = key,
            State = state,
            Url = SafeUrl(Text(attrs, "url")),
        };
    }

    private static string? SafeUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;

    private static IEnumerable<SearchWidget> ReadContacts(IEnumerable<JsonElement> contacts, string? headingId, string[] tokens)
    {
        foreach (var contact in contacts)
        {
            var name = Text(contact, "name")!;
            var detail = string.Join(" · ", new[] { Text(contact, "title"), Text(contact, "organization"), Text(contact, "department"),
                Text(contact, "username"), Texts(contact, "emails"), Texts(contact, "phones"), Text(contact, "website"), Text(contact, "address") }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (Matches(name + " " + detail, tokens)) yield return new("contact", name, detail, headingId) { Contact = ContactSnapshots.FieldsOf(contact) };
        }
    }

    private static bool Matches(string value, string[] tokens)
    {
        // Widget text is bounded by the stored document, not the query's 200-character input limit.
        var normalized = SearchText.NormalizeContent(value);
        return tokens.All(token => normalized.Contains(token, StringComparison.Ordinal));
    }

    private static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var field)
        && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString()) ? field.GetString() : null;

    private static string? Texts(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Array
        ? string.Join(" · ", field.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString())) : null;
}
