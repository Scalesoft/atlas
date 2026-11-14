using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync;

namespace Atlas.Server.Platform.Content;

/// <summary>Who reads a document outside the wiki.</summary>
public enum OutboundAudience
{
    /// <summary>Anyone with a public link (/s/&lt;token&gt;): the page's text, never who is involved, where things are kept or what else exists.</summary>
    Public,
    /// <summary>A file the wiki writes to storage it does not guard (the automatic PDF export): what the page shows, without internal identities.</summary>
    ExternalFile,
    /// <summary>A model (the AI index and the answers it feeds): readable text, never a concealed block or a password reference (P11).</summary>
    Ai,
}

/// <summary>
/// What of a stored rich document leaves the wiki: an allow-list with a policy per node type of the host schema and audience. A node
/// type without one fails the copy instead of going out whole, and OutboundDocumentTests fails for a schema node nobody decided about.
/// Password references are decided before: the public link removes them all, a file keeps those its reader may discover.
/// </summary>
public static class OutboundDocument
{
    /// <summary>Changes a node in place; false removes it.</summary>
    private delegate bool Policy(JsonObject node, IReadOnlyDictionary<Guid, string> sharedPages);

    private static readonly Policy Keep = (_, _) => true;
    private static readonly Policy Remove = (_, _) => false;

    private static readonly FrozenDictionary<string, (Policy Public, Policy ExternalFile, Policy Ai)> Policies = new Dictionary<string, (Policy, Policy, Policy)>
    {
        ["contactCard"] = (NamesOnly, WithoutIdentity, ContactText),
        ["contactInline"] = (NamesOnly, WithoutIdentity, ContactText),
        ["requirement"] = (WithoutParty, WithoutPartyContact, Keep),
        // A requirement heading's party (shape §6) goes out as its contact node in the facts line did: the name, never the directory id.
        ["heading"] = (WithoutHeadingPartyContact, WithoutHeadingPartyContact, WithoutHeadingPartyContact),
        // The model gets the key and title, not the cached state the issue may have left long ago (GAP5-5).
        ["youtrackIssue"] = (IssueText, Keep, IssueText),
        ["openapi"] = (DocumentationOnly, WithoutUploader, Keep),
        ["pageReference"] = (PageText, ReadableLink, Keep),
        ["secretReference"] = (Remove, Keep, Remove),
        ["secretInlineReference"] = (Remove, Keep, Remove),
        // A concealed block is a spoiler every reader may reveal (docs/markdown/concealed-content.md), not a protection; it is never sent (P11).
        ["concealedContent"] = (Keep, Keep, Remove),
    }.Concat(new[]
    {
        "doc", "paragraph", "text", "hardBreak", "blockquote", "bulletList", "orderedList", "listItem", "taskList", "taskItem", "timedList", "timedItem",
        "codeBlock", "horizontalRule", "table", "tableRow", "tableHeader", "tableCell", "callout", "image", "attachment",
        "chart", "diagram", "mathBlock", "mathInline", "tableOfContents", "emoji", "overview", "requirementSummary",
    }.Select(type => KeyValuePair.Create(type, (Keep, Keep, Keep)))).ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Whether the node type has a policy for every audience.</summary>
    public static bool Covers(string type) => Policies.ContainsKey(type);

    /// <summary>A copy of a stored envelope (<c>{"document": …}</c>) for the audience.</summary>
    /// <exception cref="InvalidDataException">The document is not JSON or holds a node type without a policy.</exception>
    /// <param name="titledPages">The linked pages whose title the audience may see, with that title (<see cref="Apply"/>).</param>
    public static string ForAudience(string contentJson, OutboundAudience audience, IReadOnlyDictionary<Guid, string>? titledPages = null)
    {
        var envelope = Parse(contentJson);
        Apply(envelope, audience, titledPages ?? FrozenDictionary<Guid, string>.Empty);
        return envelope.ToJsonString(RichDocument.JsonWrite);
    }

    /// <summary>
    /// The Markdown a model reads of a stored envelope (<see cref="OutboundAudience.Ai"/>): the one text every AI path sends. The OpenAPI
    /// specifications follow the page's text as sections of their own (the Markdown carries only their summary), read from the same
    /// filtered copy, so a concealed one stays out too.
    /// </summary>
    /// <exception cref="InvalidDataException">The document is not JSON.</exception>
    public static string AiText(string contentJson)
    {
        var envelope = Parse(contentJson);
        Apply(envelope, OutboundAudience.Ai, FrozenDictionary<Guid, string>.Empty);
        return (envelope["document"] is JsonObject document ? EditorMarkdownWriter.Write(document) : "")
            + OpenApiSearchText.Of(envelope.ToJsonString(RichDocument.JsonWrite));
    }

    private static JsonNode Parse(string contentJson)
    {
        try
        {
            var envelope = JsonNode.Parse(contentJson, documentOptions: MarkdownDocumentValidator.JsonOptions) ?? throw new InvalidDataException("The document is invalid.");
            // A stored shape older Atlas wrote (a disclosure, 3.6) leaves as the editor opens it, never as a node without a policy.
            if (envelope["document"] is JsonObject document)
            {
                LegacyDocumentRepair.Repair(document);
            }
            return envelope;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidDataException("The document is invalid.");
        }
    }

    /// <summary>Changes a parsed envelope in place for the audience.</summary>
    /// <param name="sharedPages">For <see cref="OutboundAudience.Public"/> and <see cref="OutboundAudience.ExternalFile"/>: the linked pages whose
    /// title the audience may see, with that title.</param>
    public static void Apply(JsonNode? envelope, OutboundAudience audience, IReadOnlyDictionary<Guid, string> sharedPages)
    {
        if (envelope?["document"] is JsonObject document && !Allowed(document, audience, sharedPages))
            throw new InvalidDataException("The document is invalid.");
    }

    private static bool Allowed(JsonObject node, OutboundAudience audience, IReadOnlyDictionary<Guid, string> sharedPages)
    {
        if (!Policies.TryGetValue(RichDocument.TypeOf(node), out var policies))
        {
            // One page must not stop an index batch; leaving the node out is as closed.
            return audience == OutboundAudience.Ai ? false : throw new InvalidDataException("The document holds a node without an outbound policy.");
        }
        var policy = audience switch { OutboundAudience.Public => policies.Public, OutboundAudience.ExternalFile => policies.ExternalFile, _ => policies.Ai };
        if (!policy(node, sharedPages)) return false;
        if (node["content"] is JsonArray content)
            for (var index = content.Count - 1; index >= 0; index--)
                if (content[index] is JsonObject child && !Allowed(child, audience, sharedPages)) content.RemoveAt(index);
        return true;
    }

    // Only who is named, never the directory record behind it (spec 3.1 contacts §4).
    private static bool NamesOnly(JsonObject node, IReadOnlyDictionary<Guid, string> _) => OnlyContactFields(node, "name", "kind");

    // What the card shows stays; the login, the ids that tie it to accounts and the directory, and any field added later do not.
    private static bool WithoutIdentity(JsonObject node, IReadOnlyDictionary<Guid, string> _) => OnlyContactFields(node,
        "name", "role", "kind", "title", "organization", "department", "phones", "emails", "website", "address", "photo");

    private static bool OnlyContactFields(JsonObject node, params string[] keys)
    {
        foreach (var contact in Contacts(node))
            foreach (var (key, _) in contact.ToArray())
                if (!keys.Contains(key, StringComparer.Ordinal)) contact.Remove(key);
        return true;
    }

    private static bool ContactText(JsonObject node, IReadOnlyDictionary<Guid, string> _)
    {
        // A readable line per person (name, role on the page, function, contacts) instead of the dialect's YAML with directory and account
        // ids. The editor's text has no role ("PM projektu"), so it joins the name here.
        foreach (var contact in Contacts(node))
            if (StringField(contact, "role") is { Length: > 0 } role)
                contact["name"] = string.Join(" · ", new[] { StringField(contact, "name"), role }.Where(x => x.Length > 0));
        var inline = RichDocument.TypeOf(node) == "contactInline";
        var text = EditorComponentText.Contacts(node["attrs"]?["data"] as JsonObject, inline);
        if (text.Length == 0) return false;
        if (inline) return Text(node, text);
        node.Clear();
        node["type"] = "paragraph";
        node["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        return true;
    }

    private static string StringField(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";

    private static IEnumerable<JsonObject> Contacts(JsonObject node) => (node["attrs"]?["data"]?["contacts"] as JsonArray ?? []).OfType<JsonObject>();

    private static bool WithoutParty(JsonObject node, IReadOnlyDictionary<Guid, string> sharedPages)
    {
        // The requirement and its state, never who is involved or where it is stored (spec 2026-10-27 §3).
        if (node["attrs"] is JsonObject attrs)
        {
            attrs["party"] = "";
            attrs["where"] = "";
            // A dependency on another page (key@pageId) names that page and its requirement; one on this page stays.
            if (attrs.ContainsKey("requires"))
                attrs["requires"] = string.Join(",", StringField(attrs, "requires").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(key => !key.Contains('@')));
        }
        return WithoutPartyContact(node, sharedPages);
    }

    private static bool WithoutPartyContact(JsonObject node, IReadOnlyDictionary<Guid, string> _)
    {
        if (node["attrs"] is JsonObject attrs) attrs["partyContactId"] = "";
        return true;
    }

    private static bool WithoutHeadingPartyContact(JsonObject node, IReadOnlyDictionary<Guid, string> _)
    {
        (node["attrs"] as JsonObject)?.Remove("partyContactId");
        return true;
    }

    private static bool IssueText(JsonObject node, IReadOnlyDictionary<Guid, string> _)
    {
        // The editor persists cached integration state; the visible label stays as inert text, never the upstream identity, URL or state.
        var key = node["attrs"]?["key"]?.GetValue<string>() ?? "";
        var title = node["attrs"]?["title"]?.GetValue<string>() ?? "";
        return Text(node, string.Join(": ", new[] { key, title }.Where(value => !string.IsNullOrEmpty(value))) is { Length: > 0 } label ? label : "Požadavek");
    }

    private static bool DocumentationOnly(JsonObject node, IReadOnlyDictionary<Guid, string> sharedPages)
    {
        // Documentation only: no Try It and no uploader identity. The editor treats a missing mode as interactive, so everything except
        // download-only becomes the read-only reference. The exact source and filename stay, so the public download returns the original file.
        if (node["attrs"]?["data"] is JsonObject data)
        {
            var download = data["mode"] is JsonValue mode && mode.TryGetValue<string>(out var value) && value == "download";
            data["mode"] = download ? "download" : "reference";
        }
        return WithoutUploader(node, sharedPages);
    }

    private static bool WithoutUploader(JsonObject node, IReadOnlyDictionary<Guid, string> _)
    {
        (node["attrs"]?["data"] as JsonObject)?.Remove("uploadedBy");
        return true;
    }

    private static bool PageText(JsonObject node, IReadOnlyDictionary<Guid, string> sharedPages)
    {
        // A public reader opens no other page: a link is its title when the page is as public as its space, otherwise a neutral label,
        // never the stored title, id or route of a private page or board.
        return Text(node, sharedPages.TryGetValue(LinkedPage(node), out var title) ? title : "Odkaz na stránku");
    }

    private static bool ReadableLink(JsonObject node, IReadOnlyDictionary<Guid, string> readablePages)
    {
        // A file stays a link to a page its reader (the rule's owner) may open, by the title it would open, as MCP shows it; any other
        // link is a neutral label, never the title or id its author stored (GAP1-2).
        if (!readablePages.TryGetValue(LinkedPage(node), out var title)) return Text(node, "Odkaz na stránku");
        node["attrs"]!["title"] = title;
        return true;
    }

    private static Guid LinkedPage(JsonObject node) =>
        node["attrs"]?["id"] is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var page) ? page : Guid.Empty;

    private static bool Text(JsonObject node, string text)
    {
        node.Clear();
        node["type"] = "text";
        node["text"] = text;
        return true;
    }
}
