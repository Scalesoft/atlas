using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Markdown.Sync;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.ExternalSharing;

/// <summary>Transforms a copy at the HTTP boundary. The immutable revision and stored URLs stay untouched.</summary>
public static partial class SharedDocument
{
    /// <summary>The copy of a stored envelope a public reader gets (<see cref="OutboundAudience.Public"/>), without any password reference.</summary>
    /// <param name="sharedPages">The linked pages whose title a public reader may see, with that title.</param>
    public static string ForPublic(string contentJson, IReadOnlyDictionary<Guid, string> sharedPages)
    {
        try
        {
            var document = JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions);
            if (document is not JsonObject || IsSecretNode(document))
                throw new InvalidDataException("Shared document is invalid.");
            RemoveSecrets(document);
            // Public readers never see a password reference: every one is removed above, and a copy that still declares one fails.
            using var copy = JsonDocument.Parse(document.ToJsonString(), MarkdownDocumentValidator.JsonOptions);
            if (SecretReferenceContent.ContainsReservedContent(copy.RootElement))
                throw new InvalidDataException("Shared document is invalid.");
            OutboundDocument.Apply(document, OutboundAudience.Public, sharedPages);
            RemoveYouTrackRequirement(document);
            return document.ToJsonString(RichDocument.JsonWrite);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("Shared document is invalid.");
        }
    }

    /// <summary>The public copy (<see cref="ForPublic"/>) with its attachments served under the token.</summary>
    public static SharedPageDto ForToken(SharedPageDto page, string token)
    {
        try
        {
            var document = JsonNode.Parse(page.ContentJson, documentOptions: MarkdownDocumentValidator.JsonOptions);
            Visit(document, $"/api/shared/{token}/attachments/");
            return page with { ContentJson = document!.ToJsonString(RichDocument.JsonWrite) };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("Shared document is invalid.");
        }
    }

    private static bool IsSecretNode(JsonNode? node) => node is JsonObject obj
        && obj["type"] is JsonValue type && type.TryGetValue<string>(out var name) && SecretReferenceContent.IsSecretNodeType(name);

    private static bool IsSecretRequirement(JsonNode? node) => node switch
    {
        JsonValue scalar => scalar.TryGetValue<string>(out var id) && id == SecretReferenceContent.PluginId,
        JsonObject obj => obj["id"] is JsonValue id && id.TryGetValue<string>(out var name) && name == SecretReferenceContent.PluginId,
        _ => false
    };

    private static void RemoveSecrets(JsonNode node)
    {
        if (node is JsonArray array)
        {
            for (var index = array.Count - 1; index >= 0; index--)
                if (IsSecretNode(array[index])) array.RemoveAt(index);
                else if (array[index] is { } child) RemoveSecrets(child);
        }
        else if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (IsSecretNode(value) || key == "plugins" && IsSecretRequirement(value))
                    obj.Remove(key);
                else if (key == "plugins" && value is JsonArray plugins)
                {
                    for (var index = plugins.Count - 1; index >= 0; index--)
                        if (IsSecretRequirement(plugins[index])) plugins.RemoveAt(index);
                    RemoveSecrets(plugins);
                }
                else if (value is not null) RemoveSecrets(value);
            }
        }
    }

    private static void RemoveYouTrackRequirement(JsonNode? document)
    {
        // All issue atoms are now plain text. Requiring the private integration plugin would make
        // the static public renderer reject the envelope; retain every other plugin declaration.
        if (document?["plugins"] is not JsonArray plugins) return;
        for (var index = plugins.Count - 1; index >= 0; index--)
            if (plugins[index]?["id"]?.GetValue<string>() == "youtrack") plugins.RemoveAt(index);
    }

    private static void Visit(JsonNode? node, string prefix)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
                if (!RewriteAttachment(obj, key, value, prefix)) Visit(value, prefix);
        }
        else if (node is JsonArray array)
            foreach (var child in array) Visit(child, prefix);
    }

    private static bool RewriteAttachment(JsonObject obj, string key, JsonNode? value, string prefix)
    {
        if (key is "src" or "url" or "href" && value is JsonValue scalar && scalar.TryGetValue<string>(out var url)
            && AttachmentUrl().Match(url) is { Success: true } match && match.Index == 0 && match.Length == url.Length)
        {
            obj[key] = prefix + match.Groups[1].Value;
            return true;
        }
        return false;
    }

    [GeneratedRegex(@"/api/attachments/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/(?:content|download))", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentUrl();
}
